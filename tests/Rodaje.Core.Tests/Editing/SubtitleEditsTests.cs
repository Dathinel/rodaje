using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>De una transcripción con tiempos por palabra a clips de texto en la pista de subtítulos.</summary>
public class SubtitleEditsTests
{
    private static readonly ClipGenerator Template = new() { Text = "", Style = ModelSamples.FullStyle() };

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Palabras seguidas desde <paramref name="start"/>, de <paramref name="each"/> s cada una.</summary>
    private static SpeechSegment Speech(double start, string text, double each = 0.3)
    {
        string[] parts = text.Split(' ');
        List<SpeechWord> words = [.. parts.Select((w, i) => new SpeechWord(S(start + (i * each)), S(start + ((i + 1) * each)), w))];
        return new SpeechSegment(words[0].Start, words[^1].End, text, words);
    }

    [Fact]
    public void A_short_segment_is_one_cue_on_one_line_snapped_to_frames()
    {
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout([Speech(1.013, "Hola a todos")], SubtitleLayout.Default, Rational.Fps25);
        SubtitleCue cue = Assert.Single(cues);
        Assert.Equal("Hola a todos", cue.Text);
        Assert.Equal(F(25), cue.Start);
        Assert.Equal(F(25) + S(1), cue.End);
    }

    [Fact]
    public void Long_speech_is_split_into_cues_of_at_most_two_balanced_lines()
    {
        string text = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"palabra{i % 10}"));
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout([Speech(0, text, 0.15)], SubtitleLayout.Default, Rational.Fps25);

        Assert.True(cues.Count >= 4);
        Assert.Equal(40, cues.Sum(c => c.Text.Split('\n', ' ').Length));
        foreach (SubtitleCue cue in cues)
        {
            string[] lines = cue.Text.Split('\n');
            Assert.InRange(lines.Length, 1, 2);
            Assert.All(lines, l => Assert.True(l.Length <= 42, l));
            if (lines.Length == 2)
            {
                Assert.True(Math.Abs(lines[0].Length - lines[1].Length) <= 10, cue.Text);
            }
        }

        for (int i = 1; i < cues.Count; i++)
        {
            Assert.True(cues[i].Start >= cues[i - 1].End);
        }
    }

    [Fact]
    public void Pauses_sentences_and_maximum_duration_cut_cues()
    {
        SpeechSegment pause = new(S(0), S(5), "uno dos tres",
        [
            new SpeechWord(S(0), S(0.3), "uno"),
            new SpeechWord(S(0.3), S(0.6), "dos"),
            new SpeechWord(S(2), S(2.3), "tres"),
        ]);
        Assert.Equal(["uno dos", "tres"], SubtitleEdits.Layout([pause], SubtitleLayout.Default, Rational.Fps25).Select(c => c.Text));

        SpeechSegment sentence = Speech(10, "Esta es una frase completa. Y aquí sigue otra");
        Assert.Equal(["Esta es una frase completa.", "Y aquí sigue otra"],
            SubtitleEdits.Layout([sentence], SubtitleLayout.Default, Rational.Fps25).Select(c => c.Text.Replace('\n', ' ')));

        SpeechSegment slow = Speech(20, "a b c d e f", 2);
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout([slow], SubtitleLayout.Default, Rational.Fps25);
        Assert.All(cues, c => Assert.True(c.End - c.Start <= S(7) + F(1), c.ToString()));
        Assert.True(cues.Count >= 2);
    }

    [Fact]
    public void Minimum_duration_never_overlaps_the_next_cue_and_a_lone_tail_joins_the_previous()
    {
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout(
            [Speech(0, "Corto.", 0.2), Speech(0.5, "Otro.", 0.2)], SubtitleLayout.Default, Rational.Fps25);
        Assert.Equal(2, cues.Count);
        Assert.Equal(F(13), cues[0].End);
        Assert.Equal(cues[0].End, cues[1].Start);
        Assert.Equal(cues[1].Start + S(1), cues[1].End);

        string text = string.Join(' ', Enumerable.Repeat("abcdefghi", 8)) + " fin";
        IReadOnlyList<SubtitleCue> tail = SubtitleEdits.Layout([Speech(0, text, 0.1)], SubtitleLayout.Default, Rational.Fps25);
        Assert.EndsWith("fin", tail[^1].Text, StringComparison.Ordinal);
        Assert.NotEqual("fin", tail[^1].Text);
    }

    [Fact]
    public void Segments_without_words_spread_times_by_length_and_text_is_cleaned()
    {
        SubtitleCue cue = Assert.Single(SubtitleEdits.Layout(
            [new SpeechSegment(S(2), S(4), "  hola\tmundo\u0007 ")], SubtitleLayout.Default, Rational.Fps25));
        Assert.Equal("hola mundo", cue.Text);
        Assert.Equal(S(2), cue.Start);
        Assert.Equal(S(4), cue.End);
        Assert.Empty(SubtitleEdits.Layout([new SpeechSegment(S(0), S(1), "   ")], SubtitleLayout.Default, Rational.Fps25));
    }

    [Fact]
    public void Wrap_balances_two_lines_and_keeps_a_long_word_alone()
    {
        Assert.Equal(["uno dos tres", "cuatro cinco"], SubtitleEdits.Wrap(["uno", "dos", "tres", "cuatro", "cinco"], 16));
        Assert.Equal(["supercalifragilístico", "y"], SubtitleEdits.Wrap(["supercalifragilístico", "y"], 10));
    }

    [Fact]
    public void AddSubtitles_creates_the_subtitle_track_and_one_text_clip_per_cue()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c", "m1", 0, 100, 0);
        IReadOnlyList<string> ids = SubtitleEdits.AddSubtitles(f.Ctx, [Speech(1, "Hola a todos."), Speech(4, "Empezamos ya.")], Template);

        Assert.Equal(3, ids.Count);
        Track track = f.Track(ids[^1]);
        Assert.Equal(TrackRoles.Subtitles, track.Role);
        Assert.Equal("Subtítulos", track.Name);
        Assert.Equal(["Hola a todos.", "Empezamos ya."], track.Clips.Select(c => c.Generator!.Text));
        Assert.All(track.Clips, c => Assert.NotNull(c.Generator!.Style));
        Assert.Equal(F(25), track.Clips[0].Start);
        f.AssertValid();
    }

    [Fact]
    public void AddSubtitles_reuses_a_free_subtitle_track_and_skips_busy_or_locked_ones()
    {
        TrimFixture f = new();
        string first = TitleEdits.AddSubtitleTrack(f.Ctx, "Subtítulos");
        IReadOnlyList<string> again = SubtitleEdits.AddSubtitles(f.Ctx, [Speech(1, "Uno.")], Template);
        Assert.Single(again);
        Assert.Single(f.Track(first).Clips);

        IReadOnlyList<string> busy = SubtitleEdits.AddSubtitles(f.Ctx, [Speech(1, "Encima.")], Template, newTrackName: "Traducción");
        Assert.Equal(2, busy.Count);
        Assert.Equal("Traducción", f.Track(busy[^1]).Name);

        f.Lock(busy[^1]);
        IReadOnlyList<string> later = SubtitleEdits.AddSubtitles(f.Ctx, [Speech(20, "Más tarde.")], Template);
        Assert.Single(later);
        Assert.Same(f.Track(first), f.Sequence.GetClip(later[0]).Track);
    }

    [Fact]
    public void AddSubtitles_refuses_empty_text_and_negative_times()
    {
        TrimFixture f = new();
        AssertFails(() => SubtitleEdits.AddSubtitles(f.Ctx, [new SpeechSegment(S(0), S(1), " ")], Template), "no trae texto");
        AssertFails(() => SubtitleEdits.AddSubtitles(f.Ctx, [Speech(-2, "antes")], Template), "antes del inicio");
        Assert.Throws<ArgumentException>(() => SubtitleEdits.Layout([], SubtitleLayout.Default with { MaxLines = 0 }, Rational.Fps25));
    }

    [Fact]
    public void The_command_is_a_single_undo_step()
    {
        TrimFixture f = new();
        var stack = new UndoStack(f.Project);
        SequenceEditCommand command = Edits.AddSubtitles(f.Sequence.Id, [Speech(0, "Uno."), Speech(3, "Dos."), Speech(6, "Tres.")], Template);
        stack.Execute(command);

        Assert.Equal(4, command.CreatedIds.Count);
        Assert.Equal(3, f.Sequence.VideoTracks.Count);
        stack.Undo();
        Assert.Equal(2, f.Sequence.VideoTracks.Count);
        stack.Redo();
        Assert.Equal(3, f.Sequence.VideoTracks[^1].Clips.Count);
    }
}
