using Rodaje.App.Services.Transcripts;
using Rodaje.App.Views.Subtitles;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Subtitles;

/// <summary>La transcripción del medio entero (D-061) y los subtítulos que salen de ella sin llamar al módulo.</summary>
public sealed class MediaTranscriberTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static readonly MediaRef Media = new() { Id = "m", Name = "entrevista.mp4", Duration = S(60), HasVideo = true, HasAudio = true };

    /// <summary>En segundos del medio entero: una frase con palabras (y confianza), otra sin ellas y una que se pasa del final.</summary>
    private static Transcript Whole() => new("es",
    [
        new TranscriptSegment(11.0, 12.0, " Hola a todos.",
            [new(11.0, 11.3, " Hola") { Confidence = 0.98 }, new(11.3, 11.4, " a"), new(11.4, 12.0, " todos.") { Confidence = 1.7 }]),
        new TranscriptSegment(14.0, 15.5, " Bienvenidos al canal.", []),
        new TranscriptSegment(59.5, 61.0, " Adiós", [new(59.5, 61.0, " Adiós")]),
    ]);

    private static MediaTranscript Stored() => MediaTranscriber.FromTranscript(Whole(), Media, "t", "auto", "large-v3-turbo-q5_0", new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void The_result_becomes_a_media_transcript_with_paragraphs_and_clamped_times()
    {
        MediaTranscript stored = Stored();

        Assert.Equal(["Hola", "a", "todos.", "Bienvenidos", "al", "canal.", "Adiós"], stored.Words.Select(w => w.Text));
        Assert.Equal([true, false, false, true, false, false, true], stored.Words.Select(w => w.ParagraphStart));
        Assert.Equal(S(11), stored.Words[0].Start);
        Assert.Equal([0.98, 1, 1], stored.Words.Take(3).Select(w => w.Confidence));
        Assert.Equal(S(60), stored.Words[^1].End);
        Assert.Equal(("es", "m", S(60)), (stored.Language, stored.MediaId, stored.MediaDuration));

        var project = new Project { Id = "p", Name = "p", Media = [Media.Clone()] };
        new SetTranscriptCommand("m", stored).Apply(project);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void A_clip_shows_only_its_part_and_matches_what_the_module_gives_for_the_clip()
    {
        // El clip usa de 10 a 30 s del medio y empieza en 5 s: lo mismo que transcribir solo el clip.
        Clip clip = new() { Id = "a", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = S(5) };
        Transcript clipOnly = new("es",
        [
            new TranscriptSegment(1.0, 2.0, " Hola a todos.", [new(1.0, 1.3, " Hola"), new(1.3, 1.4, " a"), new(1.4, 2.0, " todos.")]),
            new TranscriptSegment(4.0, 5.5, " Bienvenidos al canal.", []),
        ]);

        IReadOnlyList<SpeechSegment> fromStored = AutoSubtitles.ToSequence(clip, Stored());
        IReadOnlyList<SpeechSegment> fromModule = AutoSubtitles.ToSequence(clip, clipOnly);

        Assert.Equal(fromModule.Select(s => s.Text), fromStored.Select(s => s.Text));
        Assert.Equal(
            fromModule.SelectMany(s => s.Words!).Select(w => (w.Start, w.End)),
            fromStored.SelectMany(s => s.Words!).Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void A_reversed_clip_keeps_its_words_in_timeline_order()
    {
        var clip = new Clip { Id = "a", MediaId = "m", SourceIn = S(10), SourceOut = S(30), Start = Time.Zero, Speed = new ClipSpeed { Reverse = true } };
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);

        IReadOnlyList<SpeechSegment> segments = AutoSubtitles.ToSequence(clip, Stored());

        Assert.Equal(["canal. al Bienvenidos", "todos. a Hola"], segments.Select(s => s.Text));
        Assert.All(segments.SelectMany(s => s.Words!), w => Assert.True(w.Start <= w.End));
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("es", true)]
    [InlineData("en", false)]
    public void A_stored_transcript_is_reused_for_its_language_or_auto(string language, bool reused)
    {
        var project = new Project { Media = [Media.Clone()], Transcripts = [Stored()] };

        Assert.Equal(reused, MediaTranscriber.Reusable(project, "m", language) is not null);
        Assert.Null(MediaTranscriber.Reusable(project, "otro", "auto"));
        Assert.Equal((Time.Zero, S(60)), MediaTranscriber.Window(Media));
    }
}
