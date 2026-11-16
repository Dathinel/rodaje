using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;
using Rational = Rodaje.Core.Timing.Rational;

namespace Rodaje.Core.Tests.Editing.Relink;

/// <summary>
/// Medios importados de una EDL sin haber visto su archivo (D-062): sin tamaño ni sonido conocidos, con la duración que
/// usan sus clips y los clips en el TC de origen del evento.
/// </summary>
public sealed class ProvisionalRelinkTests
{
    private static readonly Rational Rate = new(25, 1);

    private static CoreTime Frames(long frames) => CoreTime.FromFrames(frames, Rate);

    /// <summary>Un clip de 50 cuadros que en la EDL empieza en <c>01:00:10:00</c>: el medio supone TC 00:00:00:00.</summary>
    private static Project Imported(string path = "TOMA01")
    {
        var media = new MediaRef { Id = "m", Name = "TOMA01", Path = path, Duration = Frames(90_300), HasVideo = true };
        var clip = new Clip { Id = "c", MediaId = "m", SourceIn = Frames(90_250), SourceOut = Frames(90_300) };
        var sequence = new Sequence { Id = "s", Name = "s", FrameRate = Rate };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1", Clips = [clip] });
        return new Project { Id = "p", Name = "p", Media = [media], Sequences = [sequence] };
    }

    private static RelinkProbe Real(double seconds, string? timecode) =>
        new(CoreTime.FromSeconds(seconds), true, true, Rate, 1920, 1080, 48000, 2, Timecode: timecode);

    [Fact]
    public void Imported_media_without_size_or_sound_is_provisional_and_real_media_is_not()
    {
        Assert.True(RelinkProbe.Of(Imported().Media[0]).IsProvisional);
        Assert.False(Real(10, null).IsProvisional);
        Assert.False(RelinkProbe.Of(new MediaRef { Duration = CoreTime.Zero, IsStillImage = true, HasVideo = true }).IsProvisional);
    }

    [Fact]
    public void Relinking_moves_the_clips_to_the_real_timecode_of_the_file_and_undo_puts_them_back()
    {
        Project project = Imported();
        RelinkProbe file = Real(seconds: 60, timecode: "01:00:00:00");
        MediaRelinkTarget target = MediaRelinkEdits.TargetFor(project, project.Media[0], @"C:\material\TOMA01.mov", file);
        var command = new RelinkMediaCommand([target]);

        command.Apply(project);

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.Same(file, target.Probe);
        Assert.Equal((Frames(250), Frames(300)), (clip.SourceIn, clip.SourceOut));
        Assert.Equal(("01:00:00:00", 1920, CoreTime.FromSeconds(60)), (project.Media[0].Timecode, project.Media[0].Width, project.Media[0].Duration));
        Assert.Empty(ModelValidator.Validate(project));

        command.Revert(project);

        Assert.Equal((Frames(90_250), Frames(90_300)), (clip.SourceIn, clip.SourceOut));
        Assert.Equal(0, project.Media[0].Width);
    }

    [Fact]
    public void A_file_that_does_not_cover_the_used_range_at_its_timecode_is_refused()
    {
        Project project = Imported();

        Assert.NotNull(MediaRelinkEdits.CheckReplacement(project, project.Media[0], Real(seconds: 5, timecode: "01:00:00:00")));
        Assert.NotNull(MediaRelinkEdits.CheckReplacement(project, project.Media[0], Real(seconds: 3600, timecode: "02:00:00:00")));
        Assert.Null(MediaRelinkEdits.CheckReplacement(project, project.Media[0], Real(seconds: 12, timecode: "01:00:00:00")));
    }

    [Fact]
    public void Search_finds_the_reel_by_file_name_without_extension_and_the_timecode_breaks_the_tie()
    {
        Project project = Imported();
        var disk = new FakeRelinkFileSystem()
            .Add("/n/dia1/TOMA01.mov", Real(seconds: 60, timecode: "05:00:00:00"))
            .Add("/n/dia2/toma01.MXF", Real(seconds: 60, timecode: "01:00:00:00"))
            .Add("/n/dia2/TOMA02.mov", Real(seconds: 60, timecode: "01:00:00:00"));

        RelinkSearchResult result = MediaRelinkSearch.Search(project, [project.Media[0]], "/n", disk);

        RelinkMatch match = Assert.Single(result.Found);
        Assert.Equal("/n/dia2/toma01.MXF", match.Path);
        Assert.NotNull(match.Target.Probe);
    }

    [Fact]
    public void Media_with_known_data_still_needs_the_same_duration()
    {
        Project project = Imported("toma.mov");
        project.Media[0].Width = 1920;
        project.Media[0].Height = 1080;

        Assert.False(Real(seconds: 9999, timecode: null).Matches(RelinkProbe.Of(project.Media[0]), MediaRelinkEdits.DefaultDurationTolerance));
        Assert.Equal(CoreTime.Zero, MediaRelinkEdits.RebaseShift(project.Media[0], Real(60, "01:00:00:00")));
    }
}
