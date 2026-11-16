using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange;

/// <summary>Lo común al leer cualquier formato: fusión con el proyecto abierto, identificadores nuevos y validación.</summary>
public static class SequenceBuilder
{
    /// <summary>
    /// Prepara <paramref name="draft"/> para añadirlo a <paramref name="target"/> sin tocarlo: identificadores nuevos para
    /// todo (medios, secuencias, pistas, clips, enlaces, grupos, transiciones y marcadores), medios deduplicados por ruta
    /// normalizada y reutilizados si el proyecto ya los tiene, solapes sin transición resueltos con aviso, y el conjunto
    /// validado con <see cref="ModelValidator"/>. Las rutas relativas de <paramref name="target"/> se resuelven contra
    /// <paramref name="targetDirectory"/>. Lanza <see cref="InterchangeException"/> si no es válido.
    /// </summary>
    public static InterchangeResult Build(TimelineDraft draft, Project target, string? targetDirectory, IIdGenerator ids, InterchangeReport report) =>
        Build(draft, target, targetDirectory, ids, report, InterchangeLimits.Default);

    public static InterchangeResult Build(TimelineDraft draft, Project target, string? targetDirectory, IIdGenerator ids, InterchangeReport report, InterchangeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(report);
        Project working = draft.Project.Clone();
        Sequence main = working.FindSequence(draft.Sequence.Id) ?? throw new InterchangeException("Falta la secuencia principal.");

        // Un mismo archivo usado por varios medios del archivo importado es un solo medio, y si el proyecto ya lo tiene, el suyo.
        var reuse = new Dictionary<string, string>(StringComparer.Ordinal);
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, MediaRef>(StringComparer.OrdinalIgnoreCase);
        foreach (MediaRef media in working.Media)
        {
            string key = MediaKey(media.Path, null);
            if (key.Length == 0)
            {
                continue;
            }

            if (byKey.TryGetValue(key, out MediaRef? first))
            {
                alias[media.Id] = first.Id;
                continue;
            }

            byKey[key] = media;
            MediaRef? existing = target.Media.FirstOrDefault(m => m.Path.Length > 0 && string.Equals(MediaKey(m.Path, targetDirectory), key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing.IsStillImage == media.IsStillImage)
            {
                reuse[media.Id] = existing.Id;
            }
        }

        if (alias.Count > 0)
        {
            working.Media.RemoveAll(m => alias.ContainsKey(m.Id));
            foreach (Clip clip in working.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips))
            {
                clip.MediaId = alias.GetValueOrDefault(clip.MediaId, clip.MediaId);
            }
        }

        FitMedia(working, target, reuse, report);
        foreach (Sequence sequence in working.Sequences)
        {
            SeparateOverlaps(sequence, report);
        }

        RenumberedDraft copy = DraftCopy.Renumber(new TimelineDraft(working, main), ids, limits, reuse);
        var merged = new Project
        {
            Id = target.Id.Length > 0 ? target.Id : "project",
            Name = target.Name,
            Media = [.. target.Media, .. copy.Media],
            Sequences = [.. target.Sequences, .. copy.Compounds, copy.Main],
        };
        List<string> errors = [.. copy.Compounds.Append(copy.Main).SelectMany(s => ModelValidator.ValidateSequence(merged, s))];
        errors.AddRange(ModelValidator.Validate(new Project { Id = merged.Id, Media = copy.Media }));
        if (errors.Count > 0)
        {
            throw new InterchangeException("El archivo no se puede importar: " + string.Join(" ", errors.Take(3)));
        }

        return new InterchangeResult(copy.Media, copy.Compounds, copy.Main, report);
    }

    /// <summary>
    /// Ruta local de un medio importado: <c>file://</c> con <c>%20</c> y tildes, relativa al archivo importado o absoluta.
    /// Nula si es de red (UNC o URL que no es <c>file</c>): no se sondea sola.
    /// </summary>
    public static string? NormalizePath(string raw, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string path = raw.Trim();
        if (path.Length == 0 || path.Any(char.IsControl))
        {
            return null;
        }

        if (path.StartsWith("file://localhost/", StringComparison.OrdinalIgnoreCase))
        {
            path = "file:///" + path["file://localhost/".Length..];
        }

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) || !uri.IsFile || uri.IsUnc || uri.Host.Length > 0)
            {
                return null;
            }

            path = uri.LocalPath;

            // Una ruta de Windows («file:///C:/…») leída en otro sistema sigue siendo de Windows: así vuelve igual que salió.
            string trimmed = path.TrimStart('/');
            if (!OperatingSystem.IsWindows() && trimmed.Length >= 3 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':' && trimmed[2] is '/' or '\\')
            {
                return trimmed.Replace('/', '\\');
            }
        }
        else if (path.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        // Una ruta de macOS («/Volumes/Disco/…», «/Users/…») no existe en Windows: se queda tal cual para buscarla por nombre.
        if (OperatingSystem.IsWindows() && (path.StartsWith('/') || path.StartsWith('\\')) && !Path.IsPathFullyQualified(path))
        {
            return path.Replace('\\', '/');
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
                : baseDirectory is null ? path
                : Path.GetFullPath(path, Path.GetFullPath(baseDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>file:///</c> de una ruta local, igual en Windows y en otros sistemas: una ruta de Windows con su letra de unidad no
    /// se resuelve contra la carpeta actual al escribirla desde Linux.
    /// </summary>
    public static string ToFileUri(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string slashed = path.Replace('\\', '/');
        bool drive = slashed.Length >= 3 && char.IsAsciiLetter(slashed[0]) && slashed[1] == ':' && slashed[2] == '/';
        if (!drive && !slashed.StartsWith('/'))
        {
            try
            {
                slashed = Path.GetFullPath(path).Replace('\\', '/');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "file:///";
            }
        }

        IEnumerable<string> parts = slashed.TrimStart('/').Split('/').Select((part, i) => i == 0 && drive ? part : Uri.EscapeDataString(part));
        return "file:///" + string.Join('/', parts);
    }

    /// <summary>Clave con que se comparan dos medios: la ruta completa, o el texto tal cual si no es una ruta local completa (un carrete).</summary>
    private static string MediaKey(string path, string? directory)
    {
        if (path.Length == 0)
        {
            return "";
        }

        try
        {
            string full = directory is not null && !Path.IsPathFullyQualified(path) ? Path.GetFullPath(path, Path.GetFullPath(directory)) : path;
            return Path.IsPathFullyQualified(full) ? MediaRelinkEdits.NormalizePath(full) : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// Un medio provisional (sin archivo visto, <see cref="RelinkProbe.IsProvisional"/>) dura lo que usan sus clips. Un
    /// medio que se reutiliza del proyecto no cambia: un clip que se sale de él se recorta, con aviso.
    /// </summary>
    private static void FitMedia(Project working, Project target, Dictionary<string, string> reuse, InterchangeReport report)
    {
        ILookup<string, (Track Track, Clip Clip)> byMedia = working.Sequences
            .SelectMany(s => s.AllTracks.SelectMany(t => t.Clips.Where(c => c.Generator is null && c.SequenceId is null).Select(c => (t, c))))
            .ToLookup(p => p.c.MediaId, StringComparer.Ordinal);
        foreach (MediaRef media in working.Media)
        {
            MediaRef actual = reuse.TryGetValue(media.Id, out string? existing) ? target.FindMedia(existing)! : media;
            List<(Track Track, Clip Clip)> clips = [.. byMedia[media.Id]];
            if (actual.IsStillImage || clips.Count == 0)
            {
                continue;
            }

            Time used = clips.Max(c => c.Clip.SourceOut);
            if (ReferenceEquals(actual, media) && RelinkProbe.Of(media).IsProvisional)
            {
                media.Duration = Time.Max(media.Duration, used);
                continue;
            }

            foreach ((Track track, Clip clip) in clips.Where(c => c.Clip.SourceOut > actual.Duration && c.Clip.Speed is null))
            {
                if (clip.SourceIn >= actual.Duration)
                {
                    track.Clips.Remove(clip);
                    track.Transitions.RemoveAll(t => t.From == clip.Id || t.To == clip.Id);
                    report.Add(IssueSeverity.Lost, IssueKind.Media, "El clip empieza después del final de su medio y no se importa.", actual.Name, clip.Id, clip.Start);
                    continue;
                }

                clip.SourceOut = actual.Duration;
                track.Transitions.RemoveAll(t => t.From == clip.Id);
                report.Add(IssueSeverity.Approximate, IssueKind.Media, "El clip se salía del final de su medio y se recortó.", actual.Name, clip.Id, clip.Start);
            }
        }
    }

    /// <summary>
    /// Los clips de una pista no se solapan en Rodaje. Un clip que pisa al anterior (sin transición: las de los lectores ya
    /// van entre clips seguidos) pasa a otra pista del mismo tipo justo encima, sin perder nada, y va al informe.
    /// </summary>
    private static void SeparateOverlaps(Sequence sequence, InterchangeReport report)
    {
        foreach (List<Track> tracks in new[] { sequence.VideoTracks, sequence.AudioTracks })
        {
            for (int index = 0; index < tracks.Count; index++)
            {
                Track track = tracks[index];
                track.SortClips();
                Track? overflow = null;
                for (int i = 1; i < track.Clips.Count; i++)
                {
                    Clip previous = track.Clips[i - 1];
                    Clip clip = track.Clips[i];
                    if (clip.Start >= previous.End)
                    {
                        continue;
                    }

                    overflow ??= new Track { Id = track.Id + "+", Kind = track.Kind, Name = track.Name + " (solapes)" };
                    track.Clips.RemoveAt(i--);
                    track.Transitions.RemoveAll(t => t.From == clip.Id || t.To == clip.Id);
                    overflow.Clips.Add(clip);
                    report.Add(IssueSeverity.Approximate, IssueKind.Track, "Un clip pisaba a otro y pasó a una pista nueva.", track.Name, clip.Id, clip.Start);
                }

                if (overflow is not null)
                {
                    tracks.Insert(index + 1, overflow);
                }
            }
        }
    }
}
