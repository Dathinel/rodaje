using System.Text;
using System.Text.Json;
using Rodaje.App.Services.Titles;
using Rodaje.App.Views.Subtitles;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Tests.Subtitles;

/// <summary>«Subtítulos automáticos» del lado de la app: resultado del módulo, tiempos, partición y el paso del historial.</summary>
public sealed class AutoSubtitlesTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un video de 60 s con su audio enlazado: el clip usa de 10 a 30 s del medio y empieza en 5 s.</summary>
    private static Project Talk(bool linkedAudio = true)
    {
        var media = new MediaRef { Id = "m", Path = @"C:\videos\entrevista.mp4", Name = "entrevista.mp4", Duration = S(60), HasVideo = true, HasAudio = true, SampleRate = 48_000, AudioChannels = 2, Width = 1920, Height = 1080, FrameRate = Rational.Fps30 };
        Clip video = new() { Id = "v", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = S(5), LinkId = linkedAudio ? "L" : null };
        Clip audio = new() { Id = "a", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = S(5), LinkId = "L" };
        var sequence = new Sequence
        {
            Id = "s", Name = "s",
            VideoTracks = [new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1", Clips = [video] }],
            AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "A1", Clips = linkedAudio ? [audio] : [] }],
        };
        return new Project { Id = "p", Name = "p", Media = [media], Sequences = [sequence] };
    }

    private static Transcript Hello() => new("es",
    [
        new TranscriptSegment(1.0, 2.0, " Hola a todos.", [new(1.0, 1.3, " Hola"), new(1.3, 1.4, " a"), new(1.4, 2.0, " todos.")]),
        new TranscriptSegment(4.0, 5.5, " Bienvenidos al canal.", []),
    ]);

    private const string HelloJson = """
        { "language": "es", "device": "vulkan", "segments": [
          { "start": 1.0, "end": 2.0, "text": " Hola a todos.", "words": [
            { "start": 1.0, "end": 1.3, "text": " Hola", "p": 0.98 }, { "start": 1.3, "end": 1.4, "text": " a", "p": 0.9 },
            { "start": 1.4, "end": 2.0, "text": " todos.", "p": 0.95 } ] },
          { "start": 4.0, "end": 5.5, "text": " Bienvenidos al canal.", "words": [] },
          { "start": "x", "end": 6.0, "text": "sin tiempos" } ] }
        """;

    [Fact]
    public void FromResult_ReadsTheOutputBuffer_OrTheInlineValue()
    {
        byte[] json = Encoding.UTF8.GetBytes(HelloJson);
        byte[] buffer = new byte[json.Length + 100];
        json.CopyTo(buffer, 0);
        using JsonDocument size = JsonDocument.Parse($$"""{"bytes":{{json.Length}}}""");

        Transcript fromBuffer = AutoSubtitles.FromResult(size.RootElement, buffer);
        Assert.Equal("es", fromBuffer.Language);
        Assert.Equal(2, fromBuffer.Segments.Count);
        Assert.Equal(3, fromBuffer.Segments[0].Words.Count);
        Assert.Equal(" todos.", fromBuffer.Segments[0].Words[2].Text);
        Assert.Empty(fromBuffer.Segments[1].Words);

        using JsonDocument inline = JsonDocument.Parse(HelloJson);
        Assert.Equal(2, AutoSubtitles.FromResult(inline.RootElement, []).Segments.Count);
    }

    [Fact]
    public void FromResult_RejectsBrokenResults()
    {
        using JsonDocument tooBig = JsonDocument.Parse("""{"bytes":50}""");
        Assert.Throws<ModuleProtocolException>(() => AutoSubtitles.FromResult(tooBig.RootElement, new byte[10]));
        Assert.Throws<ModuleProtocolException>(() => AutoSubtitles.FromResult(null, []));
        using JsonDocument noSegments = JsonDocument.Parse("""{"language":"es"}""");
        Assert.Throws<ModuleProtocolException>(() => AutoSubtitles.FromResult(noSegments.RootElement, []));
        using JsonDocument size = JsonDocument.Parse("""{"bytes":3}""");
        Assert.Throws<ModuleProtocolException>(() => AutoSubtitles.FromResult(size.RootElement, "{]x"u8));
    }

    [Fact]
    public void Parameters_CarryTheLanguage()
    {
        using JsonDocument p = AutoSubtitles.Parameters("es");
        Assert.Equal("es", p.RootElement.GetProperty("language").GetString());
        Assert.Contains(AutoSubtitles.Languages, l => l.Id == "auto");
    }

    [Fact]
    public void ResultCapacity_GrowsWithTheSpan()
    {
        Assert.Equal(AutoSubtitles.BaseResultBytes, AutoSubtitles.ResultCapacity(Time.Zero));
        Assert.Equal(AutoSubtitles.BaseResultBytes + (600 * AutoSubtitles.ResultBytesPerSecond), AutoSubtitles.ResultCapacity(S(600)));
    }

    [Fact]
    public void TargetClip_PrefersTheLinkedAudio_ThenAVideoWithSound()
    {
        Project p = Talk();
        Sequence s = p.Sequences[0];
        Assert.Equal("a", AutoSubtitles.TargetClip(s, ["v"], _ => true)!.Id);
        Assert.Null(AutoSubtitles.TargetClip(s, ["v"], _ => false));

        Sequence alone = Talk(linkedAudio: false).Sequences[0];
        Assert.Equal("v", AutoSubtitles.TargetClip(alone, ["v"], _ => true)!.Id);
        Assert.Null(AutoSubtitles.TargetClip(alone, [], _ => true));
    }

    [Fact]
    public void ToSequence_MapsBufferSecondsToTheTimeline_AndSpreadsMissingWords()
    {
        Clip clip = Talk().Sequences[0].GetClip("a").Clip;

        IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, Hello());

        Assert.Equal(2, segments.Count);
        // El búfer empieza en el punto de entrada (10 s del medio), que se ve en 5 s de la secuencia.
        Assert.Equal(S(6), segments[0].Start);
        Assert.Equal(["Hola", "a", "todos."], segments[0].Words!.Select(w => w.Text));
        Assert.Equal(S(6.3), segments[0].Words![0].End);
        Assert.Equal(["Bienvenidos", "al", "canal."], segments[1].Words!.Select(w => w.Text));
        Assert.Equal(S(9), segments[1].Words![0].Start);
        Assert.Equal(S(10.5), segments[1].Words![^1].End);
        Assert.True(segments[1].Words![1].Start > segments[1].Words![0].Start);
    }

    [Fact]
    public void ToSequence_RespectsSpeed_AndDropsWhatFallsAfterTheClip()
    {
        Clip clip = Talk().Sequences[0].GetClip("a").Clip;
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        clip.Duration = S(10);
        var transcript = new Transcript("es",
        [
            new TranscriptSegment(4, 5, "uno dos", [new(4, 4.5, "uno"), new(4.5, 5, "dos")]),
            new TranscriptSegment(19.5, 21, "fin fuera", [new(19.5, 20.5, "fin"), new(20.5, 21, "fuera")]),
        ]);

        IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, transcript);

        Assert.Equal(S(7), segments[0].Start);
        Assert.Equal(S(7.25), segments[0].Words![0].End);
        Assert.Equal(["fin"], segments[1].Words!.Select(w => w.Text));
        Assert.Equal(clip.End, segments[1].End);
    }

    [Fact]
    public void ToSequence_SplitsWordsWithSpaces_AndShortensLongOnes()
    {
        Clip clip = Talk().Sequences[0].GetClip("a").Clip;
        string longWord = new('x', ModelValidator.MaxWordLength + 20);
        var transcript = new Transcript(null, [new TranscriptSegment(0, 2, "", [new(0, 1, "dos\tpalabras"), new(1, 2, longWord), new(2, 2, " \n ")])]);

        SpeechSegment segment = Assert.Single(AutoSubtitles.ToSequence(clip, transcript));

        Assert.Equal(["dos", "palabras", longWord[..ModelValidator.MaxWordLength]], segment.Words!.Select(w => w.Text));
        Assert.Equal(S(5), segment.Words![0].Start);
        Assert.Equal(segment.Words[0].End, segment.Words[1].Start);
    }

    [Fact]
    public void Command_PutsTheSubtitlesInOneStep_WithTheirWords()
    {
        Project p = Talk();
        Clip clip = p.Sequences[0].GetClip("a").Clip;
        IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, Hello());
        IReadOnlyList<SubtitleCue> cues = AutoSubtitles.Cues(segments, p.Sequences[0].FrameRate);
        string before = ProjectSerializer.Serialize(p);

        SequenceEditCommand command = AutoSubtitles.Command("s", segments, SubtitleStyles.NewSubtitle());
        command.Apply(p);

        Assert.Equal(AutoSubtitles.Description, command.Description);
        Track track = p.Sequences[0].VideoTracks[^1];
        Assert.Equal(TrackRoles.Subtitles, track.Role);
        Assert.Equal(cues.Count, track.Clips.Count);
        Assert.Equal(cues.Select(c => c.Text), track.Clips.Select(c => c.Generator!.Text));
        Assert.Empty(ModelValidator.Validate(p));

        List<GeneratorWord> all = [.. track.Clips.SelectMany(c => c.Generator!.Words!)];
        Assert.Equal(["Hola", "a", "todos.", "Bienvenidos", "al", "canal."], all.Select(w => w.Text));
        Clip first = track.Clips[0];
        GeneratorWord hola = first.Generator!.Words![0];
        // Las palabras van en el origen del clip: 6 s de la secuencia es el borde izquierdo del subtítulo.
        Assert.Equal(S(6) - first.Start + first.SourceIn, hola.Start);
        foreach (Clip c in track.Clips)
        {
            Assert.All(c.Generator!.Words!, w => Assert.True(w.Start >= c.SourceIn - S(0.05) && w.Start <= c.SourceOut));
        }

        string json = ProjectSerializer.Serialize(p);
        Assert.Contains("\"words\"", json, StringComparison.Ordinal);
        Assert.Equal(json, ProjectSerializer.Serialize(ProjectSerializer.Deserialize(json)));

        command.Revert(p);
        Assert.Equal(before, ProjectSerializer.Serialize(p));
    }

    [Fact]
    public void Command_WithLongSpeech_SplitsTheWordsAmongManySubtitles()
    {
        Project p = Talk();
        p.Sequences[0].GetClip("a").Clip.SourceOut = S(50);
        p.Sequences[0].GetClip("v").Clip.SourceOut = S(50);
        Clip clip = p.Sequences[0].GetClip("a").Clip;
        string[] text = [.. Enumerable.Range(0, 60).Select(i => i % 9 == 8 ? $"palabra{i}." : $"palabra{i}")];
        List<TranscriptWord> words = [.. text.Select((t, i) => new TranscriptWord(i * 0.4, (i * 0.4) + 0.3, t))];
        var transcript = new Transcript("es", [new TranscriptSegment(0, 24, string.Join(' ', text), words)]);

        IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, transcript);
        SequenceEditCommand command = AutoSubtitles.Command("s", segments, SubtitleStyles.NewSubtitle());
        command.Apply(p);

        Track track = p.Sequences[0].VideoTracks[^1];
        Assert.True(track.Clips.Count > 3);
        Assert.Equal(text, track.Clips.SelectMany(c => c.Generator!.Words!).Select(w => w.Text));
        foreach (Clip c in track.Clips)
        {
            Assert.Equal(c.Generator!.Text.Split([' ', '\n']), c.Generator.Words!.Select(w => w.Text));
        }
    }

    [Fact]
    public void Command_WithNothingToSay_Fails()
    {
        Project p = Talk();
        SequenceEditCommand command = AutoSubtitles.Command("s", [], SubtitleStyles.NewSubtitle());
        Assert.Throws<EditException>(() => command.Apply(p));
        Assert.Empty(AutoSubtitles.Cues([], p.Sequences[0].FrameRate));
    }

    [Fact]
    public void Range_FormatsMinutesAndTenths_WithADecimalComma()
    {
        Assert.Equal("0:01,3–1:15,0", AutoSubtitles.Range(S(1.25), S(75)));
        // 59,96 s redondea al minuto siguiente, no a «0:60,0».
        Assert.Equal("1:00,0–0:00,0", AutoSubtitles.Range(S(59.96), S(-3)));
    }

    [Fact]
    public void ToSequence_InAReversedClip_KeepsTheWordsInTimelineOrder()
    {
        Clip clip = Talk().Sequences[0].GetClip("a").Clip;
        clip.Speed = new ClipSpeed { Reverse = true };
        clip.Duration = S(20);
        var transcript = new Transcript("es", [new TranscriptSegment(1, 3, "uno dos", [new(1, 2, "uno"), new(2, 3, "dos")])]);

        SpeechSegment segment = Assert.Single(AutoSubtitles.ToSequence(clip, transcript));

        // Al revés, 3 s del búfer (13 s del medio) se ven en 5 + (30 − 13) = 22 s, antes que 1 s (11 s del medio, en 24 s).
        Assert.Equal(["dos", "uno"], segment.Words!.Select(w => w.Text));
        // Al revés el mapa cuenta desde el último tick del tramo: a lo sumo un tick de diferencia.
        Assert.InRange((segment.Start - S(22)).Ticks, -1, 1);
        Assert.InRange((segment.End - S(24)).Ticks, -1, 1);
        Assert.All(segment.Words!, w => Assert.True(w.Start <= w.End));
    }

    [Fact]
    public void ToSequence_ClampsNegativeTimes_AndSkipsEmptySegments()
    {
        Clip clip = Talk().Sequences[0].GetClip("a").Clip;
        var transcript = new Transcript(null,
        [
            new TranscriptSegment(-0.5, 0.5, "antes", [new(-0.5, 0.5, "antes")]),
            new TranscriptSegment(2, 3, "   ", []),
        ]);

        SpeechSegment segment = Assert.Single(AutoSubtitles.ToSequence(clip, transcript));

        Assert.Equal(clip.Start, segment.Start);
        Assert.Equal(S(5.5), segment.End);
    }
}
