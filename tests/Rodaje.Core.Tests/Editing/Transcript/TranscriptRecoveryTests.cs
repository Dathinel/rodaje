using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

public sealed class TranscriptRecoveryTests
{
    private static TrimFixture Talk(bool linked) => (linked
        ? new TrimFixture().Add("v1", "v", "m2", 0, 200, 0, link: "link").Add("a1", "a", "m2", 0, 200, 0, link: "link")
        : new TrimFixture().Add("a1", "a", "m2", 0, 200, 0)).WithTranscript();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_and_recover_restore_text_duration_and_links_in_one_history_step(bool linked)
    {
        TrimFixture f = Talk(linked);
        string original = ProjectSerializer.Serialize(f.Project);
        string[] text = f.Doc().Texts();
        Time duration = f.Sequence.Duration;
        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.DeleteWords("seq", null, [2, 3, 4], true));
        string deleted = ProjectSerializer.Serialize(f.Project);
        RemovedTranscriptSpan removed = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        Assert.Equal((2, 3, F(18), F(48)), (removed.FirstWord, removed.Count, removed.SourceIn, removed.SourceOut));
        var recover = new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), removed));
        stack.Execute(recover);
        Assert.Equal(2, stack.Count);
        Assert.Equal(linked ? 2 : 1, recover.CreatedIds.Count);
        Assert.Equal(text, f.Doc().Texts());
        Assert.Equal(duration, f.Sequence.Duration);
        Assert.Empty(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        f.AssertValid();
        string recovered = ProjectSerializer.Serialize(f.Project);
        stack.Undo();
        Assert.Equal(deleted, ProjectSerializer.Serialize(f.Project));
        stack.Redo();
        Assert.Equal(recovered, ProjectSerializer.Serialize(f.Project));
        stack.Undo();
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Removed_words_are_only_between_neighbors_and_exclude_words_still_visible_on_the_guide()
    {
        TrimFixture f = new TrimFixture().Add("a1", "left", "m2", 20, 40, 0)
            .Add("a1", "right", "m2", 80, 100, 20).Add("a1", "repeat", "m2", 50, 60, 40).WithTranscript();
        RemovedTranscriptSpan[] removed = [.. TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc())];
        Assert.Equal([4, 6], removed.Select(s => s.FirstWord));
        Assert.Equal([1, 2], removed.Select(s => s.Count));
        Assert.All(removed, s => Assert.Equal(2, s.BeforeWord));
        f.WithTranscript("m1", words: 10);
        f.Sequence.AudioTracks[0].Clips[1].MediaId = "m1";
        Assert.Empty(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
    }

    [Fact]
    public void A_locked_linked_track_rejects_recovery_without_changes()
    {
        TrimFixture f = Talk(true);
        Edits.DeleteWords("seq", null, [2, 3], true).Apply(f.Project);
        RemovedTranscriptSpan span = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        f.Lock("v1");
        string before = ProjectSerializer.Serialize(f.Project);
        var command = new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), span));
        Assert.Throws<EditException>(() => command.Apply(f.Project));
        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_keeps_constant_speed_and_reverse(bool reverse)
    {
        TrimFixture f = Talk(true);
        var speed = new ClipSpeed { Rate = new Rational(2, 1), Reverse = reverse };
        f.Retime("a", speed.Clone());
        f.Retime("v", speed.Clone());
        string[] before = f.Doc().Texts();
        Time duration = f.Sequence.Duration;
        Edits.DeleteWords("seq", null, [2, 3], true).Apply(f.Project);
        RemovedTranscriptSpan span = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), span)).Apply(f.Project);
        Assert.Equal(before, f.Doc().Texts());
        Assert.Equal(duration, f.Sequence.Duration);
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        f.AssertValid();
    }
}
