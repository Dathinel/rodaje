using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>
/// Pone, reemplaza o quita (con <c>null</c>) la transcripción de un medio en un paso del historial (D-061). Se valida
/// como el resto del proyecto: una transcripción que el archivo rechazaría no entra.
/// </summary>
public sealed class SetTranscriptCommand : ICommand
{
    private readonly string _mediaId;
    private readonly MediaTranscript? _transcript;
    private readonly string? _description;
    private (int Index, MediaTranscript? Saved)? _before;

    public SetTranscriptCommand(string mediaId, MediaTranscript? transcript, string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaId);
        if (transcript is not null && transcript.MediaId != mediaId)
        {
            throw new ArgumentException("La transcripción es de otro medio.", nameof(transcript));
        }

        _mediaId = mediaId;
        _transcript = transcript?.Clone();
        _description = description;
    }

    public string Description => _description ?? (_transcript is null ? "Quitar transcripción" : "Transcribir");

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int index = project.Transcripts.FindIndex(t => t.MediaId == _mediaId);
        (int Index, MediaTranscript? Saved) before = (index, index >= 0 ? project.Transcripts[index].Clone() : null);
        ProjectGuard.ApplyValidated(project, "No se pudo guardar la transcripción", () => Put(project, before.Index, _transcript?.Clone()), () => Put(project, -1, null, before));
        _before = before;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_before is not { } before)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        Put(project, -1, null, before);
    }

    public bool TryMerge(ICommand following) => false;

    // Quita la que haya del medio y pone la nueva donde estaba la vieja (o al final); con «restore», la vieja en su sitio.
    private void Put(Project project, int at, MediaTranscript? replacement, (int Index, MediaTranscript? Saved)? restore = null)
    {
        int current = project.Transcripts.FindIndex(t => t.MediaId == _mediaId);
        if (current >= 0)
        {
            project.Transcripts.RemoveAt(current);
        }

        if (restore is { Saved: { } saved, Index: int index })
        {
            project.Transcripts.Insert(Math.Min(index, project.Transcripts.Count), saved);
        }
        else if (replacement is not null)
        {
            project.Transcripts.Insert(at >= 0 ? Math.Min(at, project.Transcripts.Count) : project.Transcripts.Count, replacement);
        }
    }
}
