using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Interchange;

/// <summary>
/// Importar y exportar líneas de tiempo (EDL, FCPXML, OTIO; D-062) sin WinUI: leer y escribir fuera del hilo de la
/// interfaz, sondear los medios que existen y crear el paso del historial. Los diálogos están en <c>Views/Interchange</c>.
/// </summary>
public sealed class InterchangeService(IMediaProber prober, IIdGenerator? ids = null)
{
    /// <summary>Cuántos medios se sondean a la vez al importar.</summary>
    public const int ProbeParallelism = 4;

    private readonly IIdGenerator _ids = ids ?? GuidIdGenerator.Instance;

    /// <summary>Opciones para leer o escribir desde la secuencia activa: su tasa es la de una EDL, que no la trae.</summary>
    public static InterchangeOptions OptionsFor(Sequence active) => new() { FrameRate = active.FrameRate };

    /// <summary>
    /// Lee el archivo contra una copia de los medios del proyecto (el proyecto solo se toca desde el hilo de la interfaz) y
    /// sondea los medios nuevos que existen en el disco.
    /// </summary>
    public async Task<InterchangeResult> ReadAsync(string path, Project project, InterchangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = new Project { Id = project.Id, Media = [.. project.Media.Select(m => m.Clone())] };
        InterchangeResult result = await Task.Run(() => InterchangeFiles.Read(path, snapshot, null, options, _ids), cancellationToken);
        await ProbeAsync(result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Sondea los medios nuevos cuyo archivo existe (solo rutas locales completas), <see cref="ProbeParallelism"/> a la vez,
    /// y les copia sus datos. Un medio provisional de una EDL se corre a su TC real (<see cref="MediaRelinkEdits.RebaseShift"/>);
    /// si el archivo no cubre lo que usan sus clips, se queda provisional y lo resuelve el reenlazado.
    /// </summary>
    public async Task ProbeAsync(InterchangeResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<MediaRef> local = [.. result.Media.Where(m => IsLocal(m.Path))];
        var probes = new RelinkProbe?[local.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, local.Count), new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = cancellationToken },
            (i, _) =>
            {
                if (File.Exists(local[i].Path) && prober.Probe(local[i].Path) is { IsUsable: true, Info: { } info })
                {
                    var probed = new MediaRef { Name = local[i].Name, Path = local[i].Path };
                    info.ApplyTo(probed);
                    probes[i] = RelinkProbe.Of(probed);
                }

                return ValueTask.CompletedTask;
            });

        var project = new Project { Media = [.. result.Media], Sequences = [.. result.Compounds, result.Sequence] };
        for (int i = 0; i < local.Count; i++)
        {
            if (probes[i] is not { } probe)
            {
                result.Report.Add(IssueSeverity.Info, IssueKind.Media, "El archivo está pero no se pudo leer: se reenlaza a mano.", local[i].Name);
                continue;
            }

            if (MediaRelinkEdits.CheckReplacement(project, local[i], probe) is { } problem)
            {
                result.Report.Add(IssueSeverity.Approximate, IssueKind.Media, $"El archivo no sirve tal cual: {problem}", local[i].Name);
                continue;
            }

            Time shift = MediaRelinkEdits.RebaseShift(local[i], probe);
            foreach (Clip clip in project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips).Where(c => c.Generator is null && c.MediaId == local[i].Id))
            {
                Time duration = clip.Duration;
                clip.SourceIn += shift;
                clip.SourceOut += shift;
                clip.Duration = duration;
            }

            probe.ApplyTo(local[i]);
        }
    }

    /// <summary>
    /// El paso del historial de importar: los medios nuevos, las secuencias de los compuestos (de dentro hacia fuera), la
    /// secuencia importada y, en la secuencia activa, un clip compuesto que la muestra en una pista de video nueva (y otro,
    /// enlazado, en una de audio nueva) a partir de <paramref name="at"/>. «Descomponer» deja sus clips en la secuencia.
    /// </summary>
    public ICommand ImportCommand(InterchangeResult result, string activeSequenceId, Time at)
    {
        ArgumentNullException.ThrowIfNull(result);
        Sequence imported = result.Sequence;
        Time duration = imported.Duration;
        var commands = new List<ICommand>();
        commands.AddRange(result.Media.Select(m => new AddMediaCommand(m)));
        commands.AddRange(result.Compounds.Select(s => new AddSequenceCommand(s)));
        commands.Add(new AddSequenceCommand(imported));
        if (duration > Time.Zero)
        {
            commands.Add(new SequenceEditCommand("Colocar la línea de tiempo importada", activeSequenceId, ctx =>
            {
                bool video = imported.VideoTracks.Exists(t => t.Clips.Count > 0);
                bool audio = imported.AudioTracks.Exists(t => t.Clips.Count > 0);
                string? link = video && audio ? ctx.Ids.NewId("link") : null;
                var created = new List<string>();
                foreach ((TrackKind kind, bool used) in new[] { (TrackKind.Video, video), (TrackKind.Audio, audio) })
                {
                    if (!used)
                    {
                        continue;
                    }

                    List<Track> tracks = kind == TrackKind.Video ? ctx.Sequence.VideoTracks : ctx.Sequence.AudioTracks;
                    string trackId = TimelineEdits.AddTrack(ctx, kind, tracks.Count, imported.Name);
                    var clip = new Clip { Id = ctx.Ids.NewId("clip"), SequenceId = imported.Id, Start = at, SourceIn = Time.Zero, SourceOut = duration, LinkId = link };
                    ctx.Sequence.GetTrack(trackId).Clips.Add(clip);
                    created.Add(clip.Id);
                }

                return created;
            }, _ids));
        }

        return new CompositeCommand($"Importar «{imported.Name}»", commands);
    }

    /// <summary>Los medios nuevos cuyo archivo no está: los que abre el reenlazado después de importar.</summary>
    public static IReadOnlyList<MediaRef> Missing(InterchangeResult result, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        exists ??= File.Exists;
        return [.. result.Media.Where(m => !IsLocal(m.Path) || !exists(m.Path) || RelinkProbe.Of(m).IsProvisional)];
    }

    /// <summary>Una ruta local completa: las de red (UNC, <c>//</c>) no se tocan solas, podrían abrir una conexión.</summary>
    private static bool IsLocal(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// Escribe la secuencia fuera del hilo de la interfaz sobre una copia del proyecto; devuelve los archivos escritos (la
    /// EDL escribe uno por pista de video) y el informe de pérdidas.
    /// </summary>
    public static Task<(IReadOnlyList<string> Files, InterchangeReport Report)> ExportAsync(
        string path, Project project, string sequenceId, InterchangeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(project);
        InterchangeFormat format = InterchangeFiles.FormatOf(path) ?? throw new InterchangeException($"«{Path.GetFileName(path)}» no termina en .edl, .fcpxml ni .otio.");
        Project copy = project.Clone();
        Sequence sequence = copy.GetSequence(sequenceId);
        return Task.Run(() =>
        {
            var report = new InterchangeReport();
            IReadOnlyList<string> files = InterchangeFiles.Write(path, copy, sequence, null, format, options, report);
            return (files, report);
        }, cancellationToken);
    }

    /// <summary>Lo que se perdería al exportar, sin escribir nada (el resumen del diálogo).</summary>
    public static InterchangeReport Preview(Project project, string sequenceId, InterchangeFormat format, InterchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        return InterchangeFiles.Preview(project, project.GetSequence(sequenceId), format, options);
    }

    /// <summary>El informe con el TC de la secuencia (para «Copiar» y «Guardar como .txt»).</summary>
    public static string ReportText(InterchangeReport report, Sequence sequence, InterchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(sequence);
        Rational rate = sequence.FrameRate;
        string start = sequence.StartTimecode ?? options.DefaultStartTimecode;
        bool known = SmpteTimecode.TryParse(start, rate, out SmpteTimecode origin);
        return report.ToText(t => known
            ? SmpteTimecode.FromFrame(origin.Frame + Math.Max(0, t.ToFrames(rate)), rate, origin.DropFrame).Format()
            : t.ToSeconds().ToString("0.00", Format.SpanishNumbers.Format) + " s");
    }
}
