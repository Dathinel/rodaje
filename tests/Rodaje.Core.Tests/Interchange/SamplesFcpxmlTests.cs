using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Serialization;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Pruebas de oráculo para las muestras FCPXML 1.10 escritas a mano (fase F4t de D-062).</summary>
public sealed class SamplesFcpxmlTests
{
    private static string LoadFixture(string name)
    {
        string path = Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Interchange", "Fixtures", "Fcpxml", name);
        return File.ReadAllText(path);
    }

    private static string FixtureDirectory =>
        Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Interchange", "Fixtures", "Fcpxml");

    [Fact]
    public void Sample01_two_clips_and_gap()
    {
        string xml = LoadFixture("01_two_clips_and_gap.fcpxml");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, FixtureDirectory, options, report);

        var rate = new Rational(30000, 1001);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Track videoTrack = draft.Sequence.VideoTracks[0];
        Assert.Equal(2, videoTrack.Clips.Count);

        Clip clip1 = videoTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), clip1.Start);
        Assert.Equal(Time.FromFrames(10, rate), clip1.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clip1.SourceIn);
        MediaRef media1 = Assert.Single(draft.Project.Media, m => m.Id == clip1.MediaId);
        Assert.Equal(@"C:\Medios\Toma01.mov", media1.Path);
        Assert.Equal("Toma01", media1.Name);

        Clip clip2 = videoTrack.Clips[1];
        Assert.Equal(Time.FromFrames(10, rate), clip2.Start);
        Assert.Equal(Time.FromFrames(20, rate), clip2.Duration);
        Assert.Equal(Time.FromFrames(5, rate), clip2.SourceIn);
        MediaRef media2 = Assert.Single(draft.Project.Media, m => m.Id == clip2.MediaId);
        Assert.Equal(@"C:\Medios\Toma02.mov", media2.Path);
        Assert.Equal("Toma02", media2.Name);
    }

    [Fact]
    public void Sample02_transition()
    {
        string xml = LoadFixture("02_transition.fcpxml");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, FixtureDirectory, options, report);

        var rate = new Rational(25, 1);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Track videoTrack = draft.Sequence.VideoTracks[0];
        Assert.Equal(2, videoTrack.Clips.Count);

        Clip clipA = videoTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), clipA.Start);
        Assert.Equal(Time.FromFrames(50, rate), clipA.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipA.SourceIn);
        MediaRef mediaA = Assert.Single(draft.Project.Media, m => m.Id == clipA.MediaId);
        Assert.Equal(@"C:\Medios\PlanoA.mov", mediaA.Path);
        Assert.Equal("PlanoA", mediaA.Name);

        Clip clipB = videoTrack.Clips[1];
        Assert.Equal(Time.FromFrames(50, rate), clipB.Start);
        Assert.Equal(Time.FromFrames(50, rate), clipB.Duration);
        Assert.Equal(Time.FromFrames(10, rate), clipB.SourceIn);
        MediaRef mediaB = Assert.Single(draft.Project.Media, m => m.Id == clipB.MediaId);
        Assert.Equal(@"C:\Medios\PlanoB.mov", mediaB.Path);
        Assert.Equal("PlanoB", mediaB.Name);

        TransitionInstance transition = Assert.Single(videoTrack.Transitions);
        Assert.Equal(clipA.Id, transition.From);
        Assert.Equal(clipB.Id, transition.To);
        Assert.Equal(Time.FromFrames(20, rate), transition.Duration);
        Assert.Equal(TransitionAlignment.Center, transition.Alignment);
    }

    [Fact]
    public void Sample03_connected_lanes()
    {
        string xml = LoadFixture("03_connected_lanes.fcpxml");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, FixtureDirectory, options, report);

        var rate = new Rational(24, 1);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Equal(2, draft.Sequence.VideoTracks.Count);
        Assert.Single(draft.Sequence.AudioTracks);

        // Pista de video principal (spine / carril 0)
        Track mainTrack = draft.Sequence.VideoTracks[0];
        Clip mainClip = Assert.Single(mainTrack.Clips);
        Assert.Equal(Time.FromFrames(0, rate), mainClip.Start);
        Assert.Equal(Time.FromFrames(120, rate), mainClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), mainClip.SourceIn);
        MediaRef mediaMain = Assert.Single(draft.Project.Media, m => m.Id == mainClip.MediaId);
        Assert.Equal(@"C:\Medios\Fondo.mov", mediaMain.Path);
        Assert.Equal("Fondo", mediaMain.Name);

        // Pista de video conectada (lane="1")
        Track overlayTrack = draft.Sequence.VideoTracks[1];
        Clip overlayClip = Assert.Single(overlayTrack.Clips);
        Assert.Equal(Time.FromFrames(24, rate), overlayClip.Start);
        Assert.Equal(Time.FromFrames(48, rate), overlayClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), overlayClip.SourceIn);
        MediaRef mediaOverlay = Assert.Single(draft.Project.Media, m => m.Id == overlayClip.MediaId);
        Assert.Equal(@"C:\Medios\Broll.mov", mediaOverlay.Path);
        Assert.Equal("Broll", mediaOverlay.Name);

        // Pista de audio conectada (lane="-1")
        Track audioTrack = draft.Sequence.AudioTracks[0];
        Clip audioClip = Assert.Single(audioTrack.Clips);
        Assert.Equal(Time.FromFrames(12, rate), audioClip.Start);
        Assert.Equal(Time.FromFrames(96, rate), audioClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), audioClip.SourceIn);
        MediaRef mediaAudio = Assert.Single(draft.Project.Media, m => m.Id == audioClip.MediaId);
        Assert.Equal(@"C:\Medios\Musica.wav", mediaAudio.Path);
        Assert.Equal("Musica", mediaAudio.Name);
    }

    [Fact]
    public void Sample04_compound_clip()
    {
        string xml = LoadFixture("04_compound_clip.fcpxml");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, FixtureDirectory, options, report);

        var rate = new Rational(50, 1);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Clip compoundRef = Assert.Single(draft.Sequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromFrames(0, rate), compoundRef.Start);
        Assert.Equal(Time.FromFrames(100, rate), compoundRef.Duration);
        Assert.Equal(Time.FromFrames(0, rate), compoundRef.SourceIn);
        Assert.NotNull(compoundRef.SequenceId);
        Assert.True(compoundRef.IsCompound);

        Sequence innerSequence = Assert.Single(draft.Project.Sequences, s => s.Id == compoundRef.SequenceId);
        Assert.Equal(rate, innerSequence.FrameRate);
        Assert.Single(innerSequence.VideoTracks);

        Clip innerClip = Assert.Single(innerSequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromFrames(0, rate), innerClip.Start);
        Assert.Equal(Time.FromFrames(100, rate), innerClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), innerClip.SourceIn);
        MediaRef innerMedia = Assert.Single(draft.Project.Media, m => m.Id == innerClip.MediaId);
        Assert.Equal(@"C:\Medios\ClipAnidado.mov", innerMedia.Path);
        Assert.Equal("ClipAnidado", innerMedia.Name);
    }

    [Fact]
    public void Sample05_markers_speed_conform()
    {
        string xml = LoadFixture("05_markers_speed_conform.fcpxml");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(xml, InterchangeFormat.Fcpxml, FixtureDirectory, options, report);

        var rate = new Rational(30000, 1001);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Equal("01:00:00;00", draft.Sequence.StartTimecode);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Clip clip = Assert.Single(draft.Sequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromFrames(0, rate), clip.Start);
        Assert.Equal(Time.FromFrames(60, rate), clip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clip.SourceIn);

        MediaRef media = Assert.Single(draft.Project.Media, m => m.Id == clip.MediaId);
        Assert.Equal(@"C:\Medios\TomaVeloz.mov", media.Path);
        Assert.Equal("TomaVeloz", media.Name);

        Assert.NotNull(clip.Speed);
        Assert.Equal(new Rational(2, 1), clip.Speed.Rate);
        Assert.False(clip.Speed.Freeze);

        Marker marker = Assert.Single(draft.Sequence.Markers, m => m.Name == "MarcaImportante");
        Assert.Equal(Time.FromFrames(10, rate), marker.Time);

        // El chapter-marker se mapea como marcador o región al inicio (30 cuadros)
        bool hasChapterMarker = draft.Sequence.Markers.Any(m => m.Name == "CapituloUno" && m.Time == Time.FromFrames(30, rate))
            || draft.Sequence.Regions.Any(r => r.Name == "CapituloUno" && r.Start == Time.FromFrames(30, rate));
        Assert.True(hasChapterMarker);
    }
}
