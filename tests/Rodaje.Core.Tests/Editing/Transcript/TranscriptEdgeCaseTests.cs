using System.Diagnostics;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>Casos límite de la revisión de bloque (D-061): velocidad y reversa, mover al final, palabras largas.</summary>
public sealed class TranscriptEdgeCaseTests
{
    private static TrimFixture Linked() => new TrimFixture()
        .Add("v1", "v", "m2", 0, 200, 0, link: "l")
        .Add("a1", "a", "m2", 0, 200, 0, link: "l")
        .WithTranscript();

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(2, 1, false)]
    public void Delete_in_retimed_linked_clip_removes_the_word_keeps_sync_and_undoes(int num, int den, bool reverse)
    {
        TrimFixture f = Linked();
        f.Retime("a", new ClipSpeed { Rate = new Rational(num, den), Reverse = reverse });
        f.Retime("v", new ClipSpeed { Rate = new Rational(num, den), Reverse = reverse });
        string original = ProjectSerializer.Serialize(f.Project);
        string[] before = f.Doc().Texts();
        var stack = new UndoStack(f.Project);

        int target = Array.IndexOf(before, "w7");
        stack.Execute(Edits.DeleteWords("seq", null, [target], true));

        Assert.Equal(before.Where(t => t != "w7"), f.Doc().Texts());
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        f.AssertValid();
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Move_to_the_end_and_back_keeps_text_duration_and_links()
    {
        TrimFixture f = Linked();
        string[] before = f.Doc().Texts();
        Time duration = f.Sequence.Duration;
        var stack = new UndoStack(f.Project);

        stack.Execute(Edits.MoveWords("seq", null, 3, 2, before.Length));
        string[] moved = f.Doc().Texts();
        Assert.Equal([.. before.Take(3), .. before.Skip(5), "w3", "w4"], moved);
        Assert.Equal(duration, f.Sequence.Duration);
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);

        stack.Execute(Edits.MoveWords("seq", null, moved.Length - 2, 2, 3));
        Assert.Equal(before, f.Doc().Texts());
        Assert.Equal(duration, f.Sequence.Duration);
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        f.AssertValid();
    }

    [Fact]
    public void Recover_in_a_reversed_clip_brings_the_words_back()
    {
        TrimFixture f = new TrimFixture().Add("a1", "a", "m2", 0, 200, 0).WithTranscript();
        f.Retime("a", new ClipSpeed { Reverse = true });
        string[] before = f.Doc().Texts();
        Time duration = f.Sequence.Duration;
        var stack = new UndoStack(f.Project);
        int w = Array.IndexOf(before, "w10");
        stack.Execute(Edits.DeleteWords("seq", null, [w, w + 1], true));

        RemovedTranscriptSpan span = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        stack.Execute(new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), span)));

        Assert.Equal(before, f.Doc().Texts());
        Assert.Equal(duration, f.Sequence.Duration);
        f.AssertValid();
    }

    [Fact]
    public void A_long_word_whose_center_is_in_the_clip_is_shown()
    {
        var f = new TrimFixture();
        f.Project.GetMedia("m2").Duration = F(5000);
        f.Add("a1", "a", "m2", 2000, 5000, 0);
        f.Project.Transcripts.Add(new MediaTranscript
        {
            Id = "t", MediaId = "m2", Language = "es", Model = "x", MediaDuration = F(5000),
            Words = [new SpokenWord { Start = F(0), End = F(4800), Text = "largo", Confidence = 1 }],
        });
        f.AssertValid();

        Assert.Equal(["largo"], f.Doc().Texts());
    }

    [Fact]
    public void Correcting_a_word_to_nothing_then_undo_restores_it()
    {
        TrimFixture f = Linked();
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);
        stack.Execute(new EditTranscriptCommand("m2", t => TranscriptEdits.CorrectWord(t, 0, "   "), "Corregir"));
        Assert.Equal(TranscriptFixture.WordCount - 1, f.Doc().Words.Count);
        f.AssertValid();
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
        stack.Redo();
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    [Trait("Categoria", "Rendimiento")]
    public void Deleting_50_phrases_from_an_hour_in_200_linked_clips_fits_the_budget()
    {
        // Una hora a 25 fps, video y audio enlazados en 200 clips; 50 frases de 3 palabras repartidas, en un paso.
        var f = new TrimFixture();
        f.Project.Media.Add(new MediaRef { Id = "hora", Name = "hora", Duration = F(90_000), HasAudio = true, HasVideo = true });
        for (int i = 0; i < 200; i++)
        {
            f.Add("v1", "cv" + i, "hora", i * 450, (i + 1) * 450, i * 450, link: "l" + i);
            f.Add("a1", "ca" + i, "hora", i * 450, (i + 1) * 450, i * 450, link: "l" + i);
        }

        f.WithTranscript("hora", words: 9_000);
        int[] words = [.. Enumerable.Range(0, 50).SelectMany(i => new[] { i * 179 + 5, i * 179 + 6, i * 179 + 7 })];
        double best = double.MaxValue;
        var clock = new Stopwatch();
        for (int run = 0; run < 3; run++)
        {
            var stack = new UndoStack(f.Project);
            clock.Restart();
            stack.Execute(Edits.DeleteWords("seq", null, words, true));
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            Assert.Equal(9_000 - 150, f.Doc().Words.Count);
            stack.Undo();
        }

        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        bool unoptimized = typeof(TranscriptEdits).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled);
        Assert.True(best <= 150 * (unoptimized ? 3 : 1), $"Borrar 50 frases tardó {best:F1} ms");
    }

    [Fact]
    public void Recovering_after_deleting_without_ripple_fills_the_gap_instead_of_growing_the_sequence()
    {
        TrimFixture f = Linked();
        string[] before = f.Doc().Texts();
        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.DeleteWords("seq", null, [2, 3, 4], false));

        RemovedTranscriptSpan span = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        stack.Execute(new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), span)));

        Assert.Equal(before, f.Doc().Texts());
        Assert.Equal(F(200), f.Sequence.Duration);
        TranscriptEditsTests.AssertLinksInSync(f.Sequence);
        f.AssertValid();
    }

    [Fact]
    public void Recovering_keeps_the_source_offset_of_the_linked_video()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "v", "m2", 10, 110, 0, link: "l")
            .Add("a1", "a", "m2", 0, 100, 0, link: "l")
            .WithTranscript();
        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.DeleteWords("seq", null, [3], true));

        RemovedTranscriptSpan span = Assert.Single(TranscriptEdits.RemovedWords(f.Project, f.Sequence, f.Doc()));
        var recover = new SequenceEditCommand("Recuperar", "seq", ctx => TranscriptEdits.RecoverWords(ctx, f.Doc(), span));
        stack.Execute(recover);

        Clip audio = f.Sequence.AudioTracks[0].Clips.Single(c => recover.CreatedIds.Contains(c.Id));
        Clip video = f.Sequence.VideoTracks[0].Clips.Single(c => recover.CreatedIds.Contains(c.Id));
        Assert.Equal(audio.Start, video.Start);
        Assert.Equal(audio.SourceIn + F(10), video.SourceIn);
        Assert.Equal(audio.SourceOut + F(10), video.SourceOut);
    }

    [Fact]
    public void A_paragraph_start_also_splits_the_text_of_a_reversed_clip()
    {
        TrimFixture f = new TrimFixture().Add("a1", "a", "m2", 0, 200, 0).WithTranscript();
        f.Retime("a", new ClipSpeed { Reverse = true });
        Assert.Single(f.Doc().Paragraphs);

        f.Project.Transcripts[0].Words[10].ParagraphStart = true;

        TranscriptDocument doc = f.Doc();
        Assert.Equal(2, doc.Paragraphs.Count);
        Assert.Equal("w10", doc.Words[doc.Paragraphs[0].Count - 1].Text);
        Assert.Equal("w9", doc.Words[doc.Paragraphs[1].FirstWord].Text);
    }
}
