using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>Cuántas pistas de video y de audio trae un proyecto nuevo (se elige en Ajustes).</summary>
public readonly record struct NewProjectTracks(int Video, int Audio)
{
    public const int Min = 1;
    public const int Max = 8;

    /// <summary>Los valores de siempre: dos pistas de video y dos de audio.</summary>
    public static NewProjectTracks Default { get; } = new(2, 2);

    /// <summary>Lleva cada cantidad a 1–8.</summary>
    public NewProjectTracks Clamped() => new(Math.Clamp(Video, Min, Max), Math.Clamp(Audio, Min, Max));

    /// <summary>
    /// Crea el proyecto vacío de <see cref="ProjectFactory"/> y le añade o quita pistas por el final con las ediciones
    /// <see cref="Edits.AddTrack"/> y <see cref="Edits.RemoveTrack"/>; así «Video 1» sigue siendo la pista principal.
    /// </summary>
    public Project CreateProject(string name, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ids);
        NewProjectTracks wanted = Clamped();
        Project project = ProjectFactory.CreateDefault(name, ids);
        string sequenceId = project.Sequences[0].Id;
        Resize(project, sequenceId, TrackKind.Video, "Video", wanted.Video, ids);
        Resize(project, sequenceId, TrackKind.Audio, "Audio", wanted.Audio, ids);
        return project;
    }

    private static void Resize(Project project, string sequenceId, TrackKind kind, string label, int count, IIdGenerator ids)
    {
        Sequence sequence = project.GetSequence(sequenceId);
        List<Track> tracks = kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks;
        while (tracks.Count > count)
        {
            Edits.RemoveTrack(sequenceId, tracks[^1].Id, ids).Apply(project);
        }

        while (tracks.Count < count)
        {
            Edits.AddTrack(sequenceId, kind, tracks.Count, $"{label} {tracks.Count + 1}", ids).Apply(project);
        }
    }
}
