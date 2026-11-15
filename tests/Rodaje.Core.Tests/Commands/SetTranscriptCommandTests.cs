using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Poner, reemplazar y quitar la transcripción de un medio, con deshacer (D-061).</summary>
public sealed class SetTranscriptCommandTests
{
    private static Project TwoMedia() => new()
    {
        Id = "p",
        Name = "p",
        Media =
        [
            new MediaRef { Id = "m1", Name = "uno", Duration = Time.FromSeconds(10), HasAudio = true },
            new MediaRef { Id = "m2", Name = "dos", Duration = Time.FromSeconds(10), HasAudio = true },
        ],
    };

    private static MediaTranscript Of(string mediaId, string text) => new()
    {
        Id = "t-" + mediaId + text,
        MediaId = mediaId,
        Language = "es",
        Model = "prueba",
        MediaDuration = Time.FromSeconds(10),
        Words = [new SpokenWord { Start = Time.Zero, End = Time.FromSeconds(1), Text = text, Confidence = 1 }],
    };

    [Fact]
    public void Set_replace_in_place_and_remove_each_undo_to_the_exact_project()
    {
        Project project = TwoMedia();
        var undo = new UndoStack(project);
        undo.Execute(new SetTranscriptCommand("m1", Of("m1", "uno")));
        undo.Execute(new SetTranscriptCommand("m2", Of("m2", "dos")));
        string before = ProjectSerializer.Serialize(project);

        undo.Execute(new SetTranscriptCommand("m1", Of("m1", "otra")));
        Assert.Equal(["otra", "dos"], project.Transcripts.Select(t => t.Words[0].Text));
        undo.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));

        var remove = new SetTranscriptCommand("m1", null);
        undo.Execute(remove);
        Assert.Equal("Quitar transcripción", remove.Description);
        Assert.Equal(["m2"], project.Transcripts.Select(t => t.MediaId));
        undo.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
    }

    [Fact]
    public void An_invalid_transcript_is_rejected_without_changes()
    {
        Project project = TwoMedia();
        MediaTranscript bad = Of("m1", "uno");
        bad.Words[0].End = Time.FromSeconds(11);

        Assert.Throws<EditException>(() => new SetTranscriptCommand("m1", bad).Apply(project));
        Assert.Empty(project.Transcripts);
        Assert.Throws<ArgumentException>(() => new SetTranscriptCommand("m2", Of("m1", "uno")));
    }

    [Fact]
    public void The_command_keeps_its_own_copy()
    {
        Project project = TwoMedia();
        MediaTranscript transcript = Of("m1", "uno");
        var command = new SetTranscriptCommand("m1", transcript);

        transcript.Words[0].Text = "cambiada";
        command.Apply(project);

        Assert.Equal("uno", project.Transcripts[0].Words[0].Text);
    }
}
