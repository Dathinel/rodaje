using FotoToma.Core.Timing;

namespace FotoToma.Core.Model;

/// <summary>Crea proyectos nuevos con la estructura con la que se empieza a editar.</summary>
public static class ProjectFactory
{
    /// <summary>
    /// Proyecto vacío con una secuencia «Secuencia 1» de 1920×1080 y 48 kHz, con dos pistas de video y
    /// dos de audio.
    /// </summary>
    /// <param name="name">Nombre visible del proyecto.</param>
    /// <param name="ids">Fuente de identificadores.</param>
    /// <param name="frameRate">Tasa de la secuencia; 30 fps si no se indica.</param>
    public static Project CreateDefault(string name, IIdGenerator ids, Rational? frameRate = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(ids);

        Track NewTrack(TrackKind kind, string trackName) => new()
        {
            Id = ids.NewId("track"),
            Kind = kind,
            Name = trackName,
        };

        var project = new Project
        {
            Id = ids.NewId("project"),
            Name = name,
        };

        project.Sequences.Add(new Sequence
        {
            Id = ids.NewId("sequence"),
            Name = "Secuencia 1",
            FrameRate = frameRate ?? Rational.Fps30,
            Width = 1920,
            Height = 1080,
            SampleRate = 48000,
            VideoTracks = [NewTrack(TrackKind.Video, "Video 1"), NewTrack(TrackKind.Video, "Video 2")],
            AudioTracks = [NewTrack(TrackKind.Audio, "Audio 1"), NewTrack(TrackKind.Audio, "Audio 2")],
        });

        return project;
    }
}
