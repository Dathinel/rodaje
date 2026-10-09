using System.Globalization;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Editing.Placement;

/// <summary>Secuencia a 25 fps con v1, v2, a1, a2 y medios «av» (100 cuadros), «vid» (50, solo imagen) y «aud» (80, solo sonido).</summary>
internal sealed class PlacementBuilder
{
    /// <summary>Un cuadro a 25 fps son 705 600 000 / 25 = 28 224 000 ticks.</summary>
    public const long FrameTicks = 28_224_000;

    public PlacementBuilder()
    {
        Project = new Project
        {
            Id = "proyecto",
            Name = "Prueba",
            Media =
            [
                new MediaRef { Id = "av", Name = "Toma", Duration = F(100), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "vid", Name = "Imagen", Duration = F(50), HasVideo = true },
                new MediaRef { Id = "aud", Name = "Música", Duration = F(80), HasAudio = true },
            ],
        };

        Sequence = new Sequence
        {
            Id = "secuencia",
            Name = "Principal",
            FrameRate = Rational.Fps25,
            VideoTracks =
            [
                new Track { Id = "v1", Name = "Video 1", Kind = TrackKind.Video },
                new Track { Id = "v2", Name = "Video 2", Kind = TrackKind.Video },
            ],
            AudioTracks =
            [
                new Track { Id = "a1", Name = "Audio 1", Kind = TrackKind.Audio },
                new Track { Id = "a2", Name = "Audio 2", Kind = TrackKind.Audio },
            ],
        };

        Project.Sequences.Add(Sequence);
    }

    public Project Project { get; }

    public Sequence Sequence { get; }

    public SequentialIdGenerator Ids { get; } = new();

    public static Time F(long frames) => Time.FromFrames(frames, Rational.Fps25);

    public EditContext Context() => new(Project, Sequence, Ids);

    public Track Track(string id) => Sequence.GetTrack(id);

    public Clip Clip(string id) => Sequence.GetClip(id).Clip;

    /// <summary>Agrega un clip que empieza en el cuadro <paramref name="start"/> con el rango de origen [inF, outF).</summary>
    public Clip Add(string trackId, string id, long start, long inF, long outF, string media = "av", string? link = null)
    {
        var clip = new Clip
        {
            Id = id,
            MediaId = media,
            Start = F(start),
            SourceIn = F(inF),
            SourceOut = F(outF),
            LinkId = link,
        };
        TrackOps.AddClipSorted(Track(trackId), clip);
        return clip;
    }

    /// <summary>Describe la pista como «id@inicio[entrada,salida)» en cuadros, comprobando que cada posición sea exacta.</summary>
    public string Layout(string trackId) =>
        string.Join(" ", Track(trackId).Clips.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Id}@{Frames(c.Start)}[{Frames(c.SourceIn)},{Frames(c.SourceOut)})")));

    public void AssertValid() => Assert.Empty(ModelValidator.Validate(Project));

    /// <summary>Huella completa de la secuencia para comprobar que una operación fallida no tocó nada.</summary>
    public string Snapshot() =>
        string.Join(" | ", Sequence.AllTracks.Select(t => t.Id + ": " + string.Join(" ", t.Clips.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"{c.Id}/{c.LinkId}@{c.Start.Ticks}[{c.SourceIn.Ticks},{c.SourceOut.Ticks})")))));

    private static long Frames(Time t)
    {
        Assert.Equal(0, t.Ticks % FrameTicks);
        return t.Ticks / FrameTicks;
    }
}
