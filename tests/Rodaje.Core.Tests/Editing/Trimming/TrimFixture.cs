using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Trimming;

/// <summary>Secuencia a 25 fps con v1, v2, a1, a2 y medios m1 (100 cuadros, AV), m2 (200, AV) y m3 (50, audio).</summary>
internal sealed class TrimFixture
{
    public TrimFixture()
    {
        Project = new Project
        {
            Id = "proyecto",
            Name = "Prueba",
            Media =
            [
                new MediaRef { Id = "m1", Name = "uno", Duration = F(100), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "m2", Name = "dos", Duration = F(200), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "m3", Name = "tres", Duration = F(50), HasAudio = true },
            ],
        };

        Sequence = new Sequence
        {
            Id = "seq",
            Name = "Secuencia",
            FrameRate = Rational.Fps25,
            VideoTracks =
            [
                new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" },
                new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2" },
            ],
            AudioTracks =
            [
                new Track { Id = "a1", Kind = TrackKind.Audio, Name = "A1" },
                new Track { Id = "a2", Kind = TrackKind.Audio, Name = "A2" },
            ],
        };

        Project.Sequences.Add(Sequence);
        Ctx = new EditContext(Project, Sequence, new SequentialIdGenerator());
    }

    public Project Project { get; }

    public Sequence Sequence { get; }

    public EditContext Ctx { get; }

    /// <summary>Instante del cuadro <paramref name="frames"/> a 25 fps.</summary>
    public static Time F(long frames) => Time.FromFrames(frames, Rational.Fps25);

    public Track Track(string id) => Sequence.GetTrack(id);

    public Clip Clip(string id) => Sequence.GetClip(id).Clip;

    /// <summary>Agrega un clip dados sus puntos en cuadros: entrada y salida en el origen, e inicio en la pista.</summary>
    public TrimFixture Add(string trackId, string clipId, string mediaId, long inFrame, long outFrame, long startFrame, string? link = null)
    {
        Track track = Track(trackId);
        track.Clips.Add(new Clip
        {
            Id = clipId,
            MediaId = mediaId,
            SourceIn = F(inFrame),
            SourceOut = F(outFrame),
            Start = F(startFrame),
            LinkId = link,
        });
        track.SortClips();
        return this;
    }

    /// <summary>Agrega un título (clip generador con estilo completo) con su origen virtual en cuadros.</summary>
    public TrimFixture AddTitle(string trackId, string clipId, long inFrame, long outFrame, long startFrame, string? link = null)
    {
        Clip title = Model.ModelSamples.MakeTitle(clipId, 0, 0, 1);
        title.SourceIn = F(inFrame);
        title.SourceOut = F(outFrame);
        title.Start = F(startFrame);
        title.LinkId = link;
        Track(trackId).Clips.Add(title);
        Track(trackId).SortClips();
        return this;
    }

    public TrimFixture Lock(string trackId)
    {
        Track(trackId).Locked = true;
        return this;
    }

    /// <summary>Comprueba los tres instantes de un clip en cuadros.</summary>
    public void AssertClip(string clipId, long inFrame, long outFrame, long startFrame)
    {
        Clip clip = Clip(clipId);
        Assert.Equal(F(inFrame), clip.SourceIn);
        Assert.Equal(F(outFrame), clip.SourceOut);
        Assert.Equal(F(startFrame), clip.Start);
    }

    public void AssertValid()
    {
        IReadOnlyList<string> errors = ModelValidator.Validate(Project);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    /// <summary>La edición debe fallar con una frase (mayúscula inicial, punto final) que contenga el texto esperado.</summary>
    public static EditException AssertFails(Action edit, string expectedFragment)
    {
        EditException ex = Assert.Throws<EditException>(edit);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.True(char.IsUpper(ex.Message[0]), ex.Message);
        Assert.EndsWith(".", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        return ex;
    }
}
