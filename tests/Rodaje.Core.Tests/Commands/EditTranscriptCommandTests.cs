using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Transcript;
using Rodaje.Core.Tests.Editing.Trimming;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Commands;

public sealed class EditTranscriptCommandTests
{
    private static TrimFixture Talk()
    {
        TrimFixture f = new TrimFixture().Add("a1", "a", "m2", 0, 100, 0)
            .Add("a1", "b", "m2", 0, 100, 100).WithTranscript();
        f.Project.Transcripts[0].Speakers.Add(new TranscriptSpeaker { Id = "speaker", Name = "Ana", Color = "#123456" });
        return f;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Every_edit_undoes_to_identical_json_and_redoes_the_saved_snapshot(int operation)
    {
        TrimFixture f = Talk();
        f.Project.Transcripts[0].Words[2].SpeakerId = "speaker";
        f.Project.Transcripts[0].Words[2].ParagraphStart = true;
        string before = ProjectSerializer.Serialize(f.Project);
        int calls = 0;
        var command = new EditTranscriptCommand("m2", t =>
        {
            calls++;
            return operation switch
            {
                0 => TranscriptEdits.CorrectWord(t, 2, "una frase"),
                1 => TranscriptEdits.CorrectWord(t, 2, " \t "),
                2 => TranscriptEdits.SplitParagraph(t, 4),
                3 => TranscriptEdits.AddSpeaker(t, new TranscriptSpeaker { Id = "new", Name = "Luis" }),
                4 => TranscriptEdits.UpdateSpeaker(t, "speaker", "María", "#123456"),
                5 => TranscriptEdits.UpdateSpeaker(t, "speaker", "Ana", "#ABCDEF"),
                6 => TranscriptEdits.AssignSpeaker(t, 3, 2, "speaker"),
                _ => TranscriptEdits.AssignSpeaker(t, 2, 1, null),
            };
        }, "Editar transcripción");
        var stack = new UndoStack(f.Project);
        stack.Execute(command);
        string after = ProjectSerializer.Serialize(f.Project);
        Assert.NotEqual(before, after);
        f.AssertValid();
        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
        stack.Redo();
        Assert.Equal(after, ProjectSerializer.Serialize(f.Project));
        Assert.Equal(1, calls);
        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Correction_splits_by_length_and_keeps_speaker_confidence_and_only_the_first_paragraph_start()
    {
        TrimFixture f = Talk();
        SpokenWord old = f.Project.Transcripts[0].Words[2];
        old.SpeakerId = "speaker";
        old.ParagraphStart = true;
        new EditTranscriptCommand("m2", t => TranscriptEdits.CorrectWord(t, 2, "a\tbbb cc"), "Corregir").Apply(f.Project);
        SpokenWord[] words = [.. f.Project.Transcripts[0].Words.Skip(2).Take(3)];
        Assert.Equal(["a", "bbb", "cc"], words.Select(w => w.Text));
        Assert.Equal([(F(20), F(21)), (F(21), F(24)), (F(24), F(26))], words.Select(w => (w.Start, w.End)));
        Assert.All(words, w => { Assert.Equal("speaker", w.SpeakerId); Assert.Equal(old.Confidence, w.Confidence); });
        Assert.Equal([true, false, false], words.Select(w => w.ParagraphStart));
    }

    [Fact]
    public void Assigning_in_one_clip_updates_every_occurrence_of_the_same_source_words()
    {
        TrimFixture f = Talk();
        var stack = new UndoStack(f.Project);
        stack.Execute(new EditTranscriptCommand("m2", t => TranscriptEdits.AssignSpeaker(t, 2, 2, "speaker"), "Asignar"));
        TranscriptDocument doc = f.Doc();
        Assert.Equal(4, doc.Words.Count(w => w.SpeakerId == "speaker"));
        Assert.Equal(["a", "b"], doc.Paragraphs.Where(p => p.SpeakerId == "speaker").Select(p => p.ClipId));
        stack.Execute(Edits.DeleteWords("seq", null, [2], true));
        stack.Undo();
        Assert.Equal(4, f.Doc().Words.Count(w => w.SpeakerId == "speaker"));
        stack.Undo();
        Assert.All(f.Doc().Words, w => Assert.Null(w.SpeakerId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Invalid_changes_and_exceptions_leave_the_project_and_history_untouched(int operation)
    {
        TrimFixture f = Talk();
        string before = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);
        var command = new EditTranscriptCommand("m2", t =>
        {
            switch (operation)
            {
                case 0: t.Speakers[0].Color = "rojo"; break;
                case 1: t.Words[0].Text = "dos palabras"; break;
                case 2: t.MediaId = "m1"; break;
                default: t.Words.Clear(); throw new EditException("Fallo de prueba.");
            }
            return t;
        }, "Inválido");
        Assert.Throws<EditException>(() => stack.Execute(command));
        Assert.Equal(before, ProjectSerializer.Serialize(f.Project));
        Assert.Equal(0, stack.Count);
    }
}
