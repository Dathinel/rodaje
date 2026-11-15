using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>Borrar, mover, muletillas y pausas desde el texto (D-061).</summary>
public sealed class TranscriptEditsTests
{
    /// <summary>Video y audio de m2 enlazados, enteros, en v1 y a1; palabras <c>w{i}</c> en <c>[10i, 10i + 6)</c>.</summary>
    private static TrimFixture Interview() => new TrimFixture()
        .Add("v1", "v", "m2", 0, 200, 0, link: "l")
        .Add("a1", "a", "m2", 0, 200, 0, link: "l")
        .WithTranscript();

    internal static void AssertLinksInSync(Sequence sequence)
    {
        foreach (IGrouping<string, Clip> group in sequence.AllTracks.SelectMany(t => t.Clips).Where(c => c.LinkId is not null).GroupBy(c => c.LinkId!))
        {
            Assert.Single(group.Select(c => (c.Start - c.SourceIn, c.Duration)).Distinct());
        }
    }

    [Fact]
    public void Spans_go_from_the_middle_of_the_gap_before_to_the_middle_of_the_gap_after()
    {
        TrimFixture f = Interview();
        TranscriptDocument doc = f.Doc();

        Assert.Equal([("a", F(18), F(38))], TranscriptEdits.SpansOf(doc, f.Sequence, [3, 2]));
        Assert.Equal([("a", F(0), F(8)), ("a", F(48), F(58))], TranscriptEdits.SpansOf(doc, f.Sequence, [0, 5]));
        Assert.Equal([("a", F(188), F(200))], TranscriptEdits.SpansOf(doc, f.Sequence, [19]));
        Assert.Equal([("a", F(20), F(36))], TranscriptEdits.SpansOf(doc, f.Sequence, [2, 3], (a, b) => b == F(20) ? b : a));
        Assert.Equal([("a", F(18), F(28))], TranscriptEdits.SpansOf(doc, f.Sequence, [2], (_, _) => F(1000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptEdits.SpansOf(doc, f.Sequence, [20]));
    }

    [Fact]
    public void Deleting_words_closes_the_gap_with_the_linked_video_and_removes_exactly_their_span()
    {
        TrimFixture f = Interview();
        string[] before = f.Doc().Texts();

        IReadOnlyList<(Time Start, Time End)> removed = TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [2, 3, 7], ripple: true, RippleScope.Track);

        Assert.Equal([(F(18), F(38)), (F(68), F(78))], removed);
        Assert.Equal(F(170), f.Sequence.Duration);
        Assert.Equal(before.Where((_, i) => i is not (2 or 3 or 7)), f.Doc().Texts());
        AssertLinksInSync(f.Sequence);
        f.AssertValid();
    }

    [Fact]
    public void Without_ripple_the_gap_stays()
    {
        TrimFixture f = Interview();

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [5], ripple: false, RippleScope.Track);

        Assert.Equal(F(200), f.Sequence.Duration);
        Assert.DoesNotContain("w5", f.Doc().Texts());
        Assert.Equal(F(60), f.Doc().Words[5].SeqStart);
    }

    [Fact]
    public void Across_clips_the_later_one_is_cut_first_so_the_earlier_spans_still_hold()
    {
        TrimFixture f = new TrimFixture()
            .Add("a1", "a", "m2", 0, 100, 0)
            .Add("a1", "b", "m2", 100, 200, 100)
            .WithTranscript();
        string[] before = f.Doc().Texts();

        IReadOnlyList<(Time Start, Time End)> removed = TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [4, 9, 10, 15], ripple: true, RippleScope.Track);

        Assert.Equal(F(200) - removed.Aggregate(Time.Zero, (sum, s) => sum + (s.End - s.Start)), f.Sequence.Duration);
        Assert.Equal([(F(38), F(48)), (F(88), F(100)), (F(100), F(108)), (F(148), F(158))], removed);
        Assert.Equal(before.Where((_, i) => i is not (4 or 9 or 10 or 15)), f.Doc().Texts());
    }

    [Fact]
    public void Deleting_every_word_of_a_clip_removes_the_whole_clip()
    {
        TrimFixture f = new TrimFixture()
            .Add("a1", "a", "m2", 0, 50, 0)
            .Add("a1", "b", "m2", 50, 200, 50)
            .WithTranscript();

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [0, 1, 2, 3, 4], ripple: true, RippleScope.Track);

        Assert.Equal(["b"], f.Track("a1").Clips.Select(c => c.Id));
        Assert.Equal(Time.Zero, f.Clip("b").Start);
        Assert.Equal("w5", f.Doc().Texts()[0]);
    }

    [Fact]
    public void With_all_tracks_a_music_clip_across_the_phrase_makes_the_whole_delete_fail()
    {
        TrimFixture f = Interview().Add("a2", "music", "m3", 0, 50, 0);
        Project before = f.Project.Clone();
        SequenceEditCommand command = Edits.DeleteWords("seq", null, [2, 3], ripple: true, RippleScope.AllTracks, ids: new SequentialIdGenerator());

        Assert.Throws<EditException>(() => command.Apply(f.Project));

        Assert.Equal(ProjectSerializer.Serialize(before), ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void A_locked_track_makes_the_delete_fail_without_changes()
    {
        TrimFixture f = Interview().Lock("v1");
        Project before = f.Project.Clone();

        Assert.Throws<EditException>(() => Edits.DeleteWords("seq", null, [2], ripple: true).Apply(f.Project));

        Assert.Equal(ProjectSerializer.Serialize(before), ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Undo_brings_back_the_identical_sequence()
    {
        TrimFixture f = Interview();
        string before = ProjectSerializer.Serialize(f.Project);
        var undo = new UndoStack(f.Project);

        undo.Execute(Edits.DeleteWords("seq", null, [1, 2, 3, 11], ripple: true, ids: new SequentialIdGenerator()));
        Assert.NotEqual(before, ProjectSerializer.Serialize(f.Project));
        undo.Undo();

        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Moving_a_phrase_and_back_keeps_text_duration_and_sync()
    {
        TrimFixture f = Interview();
        string[] original = f.Doc().Texts();

        TranscriptEdits.MoveWords(f.Ctx, f.Doc(), first: 2, count: 2, before: 8, RippleScope.Track);

        Assert.Equal(["w0", "w1", "w4", "w5", "w6", "w7", "w2", "w3", "w8"], f.Doc().Texts().Take(9));
        Assert.Equal(F(200), f.Sequence.Duration);
        AssertLinksInSync(f.Sequence);
        f.AssertValid();

        TranscriptEdits.MoveWords(f.Ctx, f.Doc(), first: 6, count: 2, before: 2, RippleScope.Track);

        Assert.Equal(original, f.Doc().Texts());
        Assert.Equal(F(200), f.Sequence.Duration);
        AssertLinksInSync(f.Sequence);
    }

    [Fact]
    public void Moving_to_the_end_and_across_clips()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "v", "m2", 0, 100, 0, link: "l1")
            .Add("a1", "a", "m2", 0, 100, 0, link: "l1")
            .Add("v1", "w", "m2", 100, 200, 100, link: "l2")
            .Add("a1", "b", "m2", 100, 200, 100, link: "l2")
            .WithTranscript();

        TranscriptEdits.MoveWords(f.Ctx, f.Doc(), first: 9, count: 2, before: 20, RippleScope.Track);

        string[] texts = f.Doc().Texts();
        Assert.Equal(["w18", "w19", "w9", "w10"], texts[^4..]);
        Assert.Equal(20, texts.Length);
        Assert.Equal(F(200), f.Sequence.Duration);
        AssertLinksInSync(f.Sequence);
        f.AssertValid();
    }

    [Theory]
    [InlineData(2, 2, 2)]
    [InlineData(2, 2, 3)]
    [InlineData(2, 2, 4)]
    public void Dropping_a_phrase_on_itself_fails(int first, int count, int before)
    {
        TrimFixture f = Interview();

        Assert.Throws<EditException>(() => TranscriptEdits.MoveWords(f.Ctx, f.Doc(), first, count, before, RippleScope.Track));
    }

    [Fact]
    public void Move_is_one_undo_step()
    {
        TrimFixture f = Interview();
        string before = ProjectSerializer.Serialize(f.Project);
        var undo = new UndoStack(f.Project);

        undo.Execute(Edits.MoveWords("seq", null, 0, 3, 12, ids: new SequentialIdGenerator()));
        undo.Undo();

        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Fillers_safe_are_marked_and_doubtful_phrases_are_offered()
    {
        TrimFixture f = Interview();
        List<SpokenWord> words = f.Project.Transcripts[0].Words;
        words[1].Text = "Eh,";
        words[3].Text = "bueno";
        words[5].Text = "o";
        words[6].Text = "sea";
        words[9].Text = "o";
        words[12].Text = "MMM...";

        IReadOnlyList<(int Word, bool Safe)> found = TranscriptEdits.FillerCandidates(f.Doc(), "es");

        Assert.Equal([(1, true), (3, false), (5, false), (6, false), (12, true)], found);
        Assert.Empty(TranscriptEdits.FillerCandidates(f.Doc(), "de"));
    }

    [Fact]
    public void Portuguese_keeps_accents_apart()
    {
        TrimFixture f = Interview();
        List<SpokenWord> words = f.Project.Transcripts[0].Words;
        words[0].Text = "hã";
        words[1].Text = "há";
        words[2].Text = "é";
        words[3].Text = "e";

        Assert.Equal([(0, true), (2, false)], TranscriptEdits.FillerCandidates(f.Doc(), "pt"));
    }

    [Fact]
    public void Long_pauses_are_shortened_to_what_is_kept()
    {
        TrimFixture f = new TrimFixture().Add("a1", "a", "m2", 0, 200, 0).WithTranscript(words: 4, step: 50, length: 10);

        IReadOnlyList<(string ClipId, Time Start, Time End)> pauses = TranscriptEdits.Pauses(f.Doc(), F(30));
        IReadOnlyList<(Time Start, Time End)> removed = TranscriptEdits.RemovePauses(f.Ctx, f.Doc(), F(30), keep: F(10), ripple: true, RippleScope.Track);

        Assert.Equal([("a", F(10), F(50)), ("a", F(60), F(100)), ("a", F(110), F(150))], pauses);
        Assert.Equal([(F(15), F(45)), (F(65), F(95)), (F(115), F(145))], removed);
        Assert.Equal(F(110), f.Sequence.Duration);
        Assert.Equal(4, f.Doc().Words.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptEdits.RemovePauses(f.Ctx, f.Doc(), F(10), F(10), true, RippleScope.Track));
    }
}
