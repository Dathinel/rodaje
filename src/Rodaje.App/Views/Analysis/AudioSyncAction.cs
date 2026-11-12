using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Library;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// «Sincronizar por el sonido»: con dos o más clips con sonido elegidos en la línea de tiempo (la cámara y la grabadora,
/// dos cámaras), compara sus formas de onda y mueve los demás para que suenen a la vez que el que empieza antes, que no
/// se mueve. El análisis es el de la multicámara (<see cref="MulticamSync"/>); el movimiento, un paso del historial.
/// </summary>
internal static class AudioSyncAction
{
    public const string Title = "Sincronizar por el sonido";

    /// <summary>Un clip por grupo enlazado con sonido, en orden de inicio: el primero es la referencia.</summary>
    public static IReadOnlyList<Clip> Candidates(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clips = new List<Clip>();
        foreach (string id in session.SelectedClipIds)
        {
            if (session.Sequence.FindClip(id) is { } found && ClipAudioSource.HasAudio(session, found.Clip) && seen.Add(found.Clip.LinkId ?? found.Clip.Id))
            {
                clips.Add(found.Clip);
            }
        }

        return [.. clips.OrderBy(c => c.Start)];
    }

    public static async Task RunAsync(EditorServices services, LongActionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        EditorSession session = services.Session;
        IReadOnlyList<Clip> clips = Candidates(session);
        if (clips.Count < 2)
        {
            session.Notify(NotificationSeverity.Informational, Title, "Selecciona dos clips con sonido o más: la referencia es el que empieza antes.");
            return;
        }

        Project project = session.Project;
        List<(string MediaId, string Path, bool HasAudio)> inputs = [];
        foreach (Clip clip in clips)
        {
            if (project.FindMedia(clip.MediaId) is { } media && inputs.All(i => i.MediaId != media.Id))
            {
                inputs.Add((media.Id, media.Path, media.HasAudio));
            }
        }

        // Con un solo medio (dos tramos del mismo archivo) la referencia ya da el reloj: el desfase es 0 sin analizar.
        IReadOnlyList<MulticamSync.AngleResult> results = inputs.Count == 1
            ? [new MulticamSync.AngleResult(inputs[0].MediaId, Time.Zero, 1)]
            : await Task.Run(() => MulticamSync.Run(inputs, progress: (done, total) => scope?.Report((double)done / total, $"{done} de {total} pasos")));

        Clip reference = clips[0];
        var offsets = results.Where(r => r.Offset is not null).ToDictionary(r => r.MediaId, r => r.Offset!.Value, StringComparer.Ordinal);
        List<(string ClipId, Time Offset)> pairs = [];
        List<string> failed = [];
        foreach (Clip clip in clips.Skip(1))
        {
            if (offsets.TryGetValue(clip.MediaId, out Time offset))
            {
                pairs.Add((clip.Id, offset));
            }
            else
            {
                failed.Add(ClipName(project, clip));
            }
        }

        if (pairs.Count == 0)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo sincronizar", "Los clips no comparten sonido suficiente para alinearlos.");
            return;
        }

        // La edición mueve los mismos objetos Clip: el inicio de antes se guarda aparte para decir cuánto se corrió cada uno.
        var startsBefore = clips.ToDictionary(c => c.Id, c => c.Start, StringComparer.Ordinal);
        if (session.Sequence.FindClip(reference.Id) is null || !session.Execute(Edits.AlignByAudio(session.SequenceId, reference.Id, pairs)))
        {
            return;
        }

        Rational rate = session.Sequence.FrameRate;
        string detail = string.Join(", ", pairs.Select(p => session.Sequence.FindClip(p.ClipId) is { } moved
            ? $"«{ClipName(project, moved.Clip)}» {ClipLabels.FrameShift(moved.Clip.Start - startsBefore[p.ClipId], rate)}"
            : ""));
        session.Notify(NotificationSeverity.Success, "Sincronizados por el sonido",
            $"Referencia: «{ClipName(project, reference)}». {detail}." + (failed.Count > 0 ? $" Sin sonido común: {string.Join(", ", failed)}." : ""));
    }

    private static string ClipName(Project project, Clip clip) => project.FindMedia(clip.MediaId)?.Name is { Length: > 0 } name ? name : clip.Id;
}
