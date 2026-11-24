using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Serialization;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Pruebas de oráculo para las muestras OpenTimelineIO escritas a mano (fase F4t de D-062).</summary>
public sealed class SamplesOtioTests
{
    private static string LoadFixture(string name)
    {
        string path = Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Interchange", "Fixtures", "Otio", name);
        return File.ReadAllText(path);
    }

    private static string FixtureDirectory =>
        Path.Combine(ProjectSchemaTests.FindRepoRoot(), "tests", "Rodaje.Core.Tests", "Interchange", "Fixtures", "Otio");

    [Fact]
    public void Sample01_clips_gap_linear_timewarp()
    {
        string json = LoadFixture("01_clips_gap_linear_timewarp.otio");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, FixtureDirectory, options, report);

        // 23.976 se normaliza a 24000/1001 y se anota en el informe
        var rate = new Rational(24000, 1001);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Info);

        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Single(draft.Sequence.AudioTracks);

        Track videoTrack = draft.Sequence.VideoTracks[0];
        Assert.Equal(3, videoTrack.Clips.Count);

        // Clip 1 con Clip.2 y active_media_reference_key
        Clip clip1 = videoTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), clip1.Start);
        Assert.Equal(Time.FromFrames(24, rate), clip1.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clip1.SourceIn);
        MediaRef media1 = Assert.Single(draft.Project.Media, m => m.Id == clip1.MediaId);
        Assert.Equal(@"C:\Medios\toma1.mp4", media1.Path);
        Assert.Equal("Media1", media1.Name);

        // Hueco de 12 cuadros: el siguiente clip empieza en el cuadro 36
        Clip clip2 = videoTrack.Clips[1];
        Assert.Equal(Time.FromFrames(36, rate), clip2.Start);
        Assert.Equal(Time.FromFrames(48, rate), clip2.Duration);
        Assert.Equal(Time.FromFrames(48, rate), clip2.SourceIn);
        MediaRef media2 = Assert.Single(draft.Project.Media, m => m.Id == clip2.MediaId);
        Assert.Equal(@"C:\Medios\toma2.mp4", media2.Path);
        Assert.Equal("Media2", media2.Name);
        Assert.NotNull(clip2.Speed);
        Assert.Equal(new Rational(2, 1), clip2.Speed.Rate);
        Assert.False(clip2.Speed.Freeze);

        // Clip 3 con FreezeFrame.1
        Clip clip3 = videoTrack.Clips[2];
        Assert.Equal(Time.FromFrames(84, rate), clip3.Start);
        Assert.Equal(Time.FromFrames(36, rate), clip3.Duration);
        Assert.Equal(Time.FromFrames(10, rate), clip3.SourceIn);
        MediaRef media3 = Assert.Single(draft.Project.Media, m => m.Id == clip3.MediaId);
        Assert.Equal(@"C:\Medios\toma3.mp4", media3.Path);
        Assert.Equal("Media3", media3.Name);
        Assert.NotNull(clip3.Speed);
        Assert.True(clip3.Speed.Freeze);

        // Pista de audio
        Track audioTrack = draft.Sequence.AudioTracks[0];
        Clip audioClip = Assert.Single(audioTrack.Clips);
        Assert.Equal(Time.FromFrames(0, rate), audioClip.Start);
        Assert.Equal(Time.FromFrames(120, rate), audioClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), audioClip.SourceIn);
        MediaRef mediaAudio = Assert.Single(draft.Project.Media, m => m.Id == audioClip.MediaId);
        Assert.Equal(@"C:\Medios\audio1.wav", mediaAudio.Path);
        Assert.Equal("Audio1", mediaAudio.Name);
    }

    [Fact]
    public void Sample02_transitions_and_markers()
    {
        string json = LoadFixture("02_transitions_and_markers.otio");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, FixtureDirectory, options, report);

        // 29.97 se normaliza a 30000/1001
        var rate = new Rational(30000, 1001);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Info);

        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Track videoTrack = draft.Sequence.VideoTracks[0];
        Assert.Equal(3, videoTrack.Clips.Count);

        Clip clipA = videoTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), clipA.Start);
        Assert.Equal(Time.FromFrames(60, rate), clipA.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipA.SourceIn);
        MediaRef mediaA = Assert.Single(draft.Project.Media, m => m.Id == clipA.MediaId);
        Assert.Equal(@"C:\Medios\clipA.mp4", mediaA.Path);
        Assert.Equal("MediaA", mediaA.Name);

        Clip clipB = videoTrack.Clips[1];
        Assert.Equal(Time.FromFrames(60, rate), clipB.Start);
        Assert.Equal(Time.FromFrames(60, rate), clipB.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipB.SourceIn);
        MediaRef mediaB = Assert.Single(draft.Project.Media, m => m.Id == clipB.MediaId);
        Assert.Equal(@"C:\Medios\clipB.mp4", mediaB.Path);
        Assert.Equal("MediaB", mediaB.Name);

        // Clip con MissingReference.1
        Clip clipMissing = videoTrack.Clips[2];
        Assert.Equal(Time.FromFrames(120, rate), clipMissing.Start);
        Assert.Equal(Time.FromFrames(30, rate), clipMissing.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipMissing.SourceIn);

        // Transición SMPTE_Dissolve entre clipA y clipB
        TransitionInstance transition = Assert.Single(videoTrack.Transitions);
        Assert.Equal(clipA.Id, transition.From);
        Assert.Equal(clipB.Id, transition.To);
        Assert.Equal(Time.FromFrames(20, rate), transition.Duration);
        Assert.Equal(TransitionAlignment.Center, transition.Alignment);

        // Marcador Marker.2 en el clip o en la secuencia
        Marker marker = Assert.Single(draft.Sequence.Markers);
        Assert.Equal("PuntoClave", marker.Name);
        Assert.Equal(Time.FromFrames(15, rate), marker.Time);
    }

    [Fact]
    public void Sample03_nested_stack_compound()
    {
        string json = LoadFixture("03_nested_stack_compound.otio");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, FixtureDirectory, options, report);

        var rate = new Rational(25, 1);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        Track mainTrack = draft.Sequence.VideoTracks[0];
        Assert.Equal(3, mainTrack.Clips.Count);

        Clip clipIntro = mainTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), clipIntro.Start);
        Assert.Equal(Time.FromFrames(50, rate), clipIntro.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipIntro.SourceIn);
        MediaRef mediaIntro = Assert.Single(draft.Project.Media, m => m.Id == clipIntro.MediaId);
        Assert.Equal(@"C:\Medios\intro.mp4", mediaIntro.Path);
        Assert.Equal("MediaIntro", mediaIntro.Name);

        // Clip compuesto (Stack anidado)
        Clip compoundClip = mainTrack.Clips[1];
        Assert.Equal(Time.FromFrames(50, rate), compoundClip.Start);
        Assert.Equal(Time.FromFrames(100, rate), compoundClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), compoundClip.SourceIn);
        Assert.NotNull(compoundClip.SequenceId);
        Assert.True(compoundClip.IsCompound);

        Sequence innerSequence = Assert.Single(draft.Project.Sequences, s => s.Id == compoundClip.SequenceId);
        Assert.Equal(rate, innerSequence.FrameRate);
        Assert.Single(innerSequence.VideoTracks);

        Clip innerClip = Assert.Single(innerSequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromFrames(0, rate), innerClip.Start);
        Assert.Equal(Time.FromFrames(100, rate), innerClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), innerClip.SourceIn);
        MediaRef innerMedia = Assert.Single(draft.Project.Media, m => m.Id == innerClip.MediaId);
        Assert.Equal(@"C:\Medios\interior.mp4", innerMedia.Path);
        Assert.Equal("MediaInterior", innerMedia.Name);

        Clip clipOutro = mainTrack.Clips[2];
        Assert.Equal(Time.FromFrames(150, rate), clipOutro.Start);
        Assert.Equal(Time.FromFrames(50, rate), clipOutro.Duration);
        Assert.Equal(Time.FromFrames(0, rate), clipOutro.SourceIn);
        MediaRef mediaOutro = Assert.Single(draft.Project.Media, m => m.Id == clipOutro.MediaId);
        Assert.Equal(@"C:\Medios\outro.mp4", mediaOutro.Path);
        Assert.Equal("MediaOutro", mediaOutro.Name);
    }

    [Fact]
    public void Sample04_generator_and_unknown()
    {
        string json = LoadFixture("04_generator_and_unknown.otio");
        var options = new InterchangeOptions();
        var report = new InterchangeReport();

        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, FixtureDirectory, options, report);

        var rate = new Rational(30, 1);
        Assert.Equal(rate, draft.Sequence.FrameRate);
        Assert.Single(draft.Sequence.VideoTracks);
        Assert.Empty(draft.Sequence.AudioTracks);

        // El generador y la clase desconocida deben aparecer en el informe
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Title || i.Message.Contains("SMPTEBars") || i.Message.Contains("Generator"));
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Unknown || i.Message.Contains("Foo.1"));

        Track videoTrack = draft.Sequence.VideoTracks[0];

        // Clip con generador
        Clip genClip = videoTrack.Clips[0];
        Assert.Equal(Time.FromFrames(0, rate), genClip.Start);
        Assert.Equal(Time.FromFrames(60, rate), genClip.Duration);

        // El elemento desconocido Foo.1 se convierte en hueco de 30 cuadros, por lo que el clip final empieza en 90
        Clip finalClip = videoTrack.Clips[^1];
        Assert.Equal(Time.FromFrames(90, rate), finalClip.Start);
        Assert.Equal(Time.FromFrames(60, rate), finalClip.Duration);
        Assert.Equal(Time.FromFrames(0, rate), finalClip.SourceIn);
        MediaRef finalMedia = Assert.Single(draft.Project.Media, m => m.Id == finalClip.MediaId);
        Assert.Equal(@"C:\Medios\final.mp4", finalMedia.Path);
        Assert.Equal("MediaFinal", finalMedia.Name);
    }
}
