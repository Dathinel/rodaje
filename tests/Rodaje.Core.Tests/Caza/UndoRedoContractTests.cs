using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Timing;
using Xunit;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Caza;

/// <summary>Promesas de «Deshacer y rehacer (<c>Commands/</c>)» en <c>docs/arquitectura/nucleo.md</c>.</summary>
public class UndoRedoContractTests
{
    // «La interfaz pide un comando a Edits (…: Edits.Split, Edits.Move, Edits.SetSpeed...) y lo pasa por UndoStack.Execute.»
    [Fact]
    public void Execute_applies_an_Edits_command_and_raises_Changed()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        int changed = 0;
        stack.Changed += (_, _) => changed++;

        stack.Execute(Edits.Split(SequenceId, "clip-2", S(12)));
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(20)));
        Assert.Equal(S(20), Seq(project).GetClip("clip-2").Clip.Start);
        stack.Execute(Edits.SetSpeed(SequenceId, "clip-1", new Rational(2, 1)));

        Assert.Equal(3, stack.Count);
        Assert.Equal(3, changed);
        Assert.Equal(new Rational(2, 1), Seq(project).GetClip("clip-1").Clip.Speed?.Rate);
    }

    // Diagrama: «U->>U: vacía rehacer, intenta fundir, guarda».
    [Fact]
    public void Execute_clears_redo()
    {
        var stack = new UndoStack(Create());
        stack.Execute(Edits.AddMarker(SequenceId, S(1)));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(Edits.AddMarker(SequenceId, S(2)));

        Assert.False(stack.CanRedo);
        Assert.Equal(1, stack.Count);
    }

    // «Deshacer restaura la foto de antes; rehacer restaura la de después sin repetir la operación, así que los ids
    // creados (CreatedIds) no cambian.»
    [Fact]
    public void Undo_restores_before_and_redo_restores_after_without_rerunning()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        int runs = 0;
        var command = new SequenceEditCommand(
            "Cortar clip",
            SequenceId,
            ctx =>
            {
                runs++;
                return TimelineEdits.Split(ctx, "clip-2", S(12));
            });

        stack.Execute(command);
        string after = ProjectDump.Of(project);
        string[] created = [.. command.CreatedIds];
        Assert.NotEmpty(created);

        stack.Undo();
        Assert.Equal(original, ProjectDump.Of(project));

        stack.Redo();
        Assert.Equal(after, ProjectDump.Of(project));
        Assert.Equal(1, runs);
        Assert.Equal(created, command.CreatedIds);
        Assert.NotNull(Seq(project).FindClip(created[0]));
    }

    // «Si la operación falla a mitad, se restaura la foto de antes: una edición fallida nunca deja el proyecto a medias.»
    [Fact]
    public void Failed_operation_leaves_project_and_stack_unchanged()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        var failing = new SequenceEditCommand(
            "A medias",
            SequenceId,
            ctx =>
            {
                ctx.Sequence.GetClip("clip-1").Clip.Start = S(40);
                ctx.Sequence.Markers.Add(new Marker { Id = "m-x", Time = S(1) });
                ctx.Sequence.MasterGainDb = -6;
                throw new InvalidOperationException("falla a mitad");
            });

        Assert.Throws<InvalidOperationException>(() => stack.Execute(failing));

        Assert.Equal(original, ProjectDump.Of(project));
        Assert.Empty(Seq(project).Markers);
        Assert.Equal(0, Seq(project).MasterGainDb);
        Assert.Equal(0, stack.Count);
        Assert.True(stack.IsClean);
    }

    // Diagrama: «pistas bloqueadas intactas».
    [Fact]
    public void Edit_that_changes_a_locked_track_is_rejected_and_rolled_back()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        var command = new SequenceEditCommand(
            "Mover en pista bloqueada",
            SequenceId,
            ctx => ctx.Sequence.GetClip("clip-3").Clip.Start = S(30));

        Assert.Throws<EditException>(() => stack.Execute(command));

        Assert.Equal(original, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
    }

    // «Las pistas vuelven a ser los mismos objetos, pero los clips son copias nuevas, así que fuera del núcleo un clip se
    // refiere por su id.»
    [Fact]
    public void Undo_keeps_track_objects_but_clips_are_new_copies()
    {
        Project project = Create();
        Track v1 = Seq(project).VideoTracks[0];
        Clip clip2 = Seq(project).GetClip("clip-2").Clip;
        var stack = new UndoStack(project);

        stack.Execute(Edits.Move(SequenceId, "clip-2", S(20)));
        stack.Undo();

        Assert.Same(v1, Seq(project).VideoTracks[0]);
        (Track track, Clip restored) = Seq(project).GetClip("clip-2");
        Assert.Same(v1, track);
        Assert.NotSame(clip2, restored);
        Assert.Equal(S(10), restored.Start);
    }

    // «Los arrastres llevan una clave (trim:clip-3:out, move:clip-3...); comandos seguidos con la misma clave se funden en
    // un paso, y BreakMerge al soltar el ratón corta la fusión.»
    [Fact]
    public void Drag_commands_carry_merge_keys()
    {
        Assert.Equal("trim:clip-3:out", Edits.Trim(SequenceId, "clip-3", TrimEdge.Out, S(-1)).MergeKey);
        Assert.Equal("move:clip-3", Edits.Move(SequenceId, "clip-3", S(1)).MergeKey);
    }

    [Fact]
    public void Same_key_commands_merge_into_one_step_until_BreakMerge()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        stack.Execute(Edits.Move(SequenceId, "clip-2", S(11)));
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(12)));
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(13)));
        Assert.Equal(1, stack.Count);

        stack.BreakMerge();
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(14)));
        Assert.Equal(2, stack.Count);

        stack.Undo();
        Assert.Equal(S(13), Seq(project).GetClip("clip-2").Clip.Start);
        stack.Undo();
        Assert.Equal(original, ProjectDump.Of(project));
    }

    // «Dentro de un grupo (un gesto de la interfaz) también se funden, así un arrastre largo guarda un paso y no uno por
    // cada movimiento del ratón.»
    [Fact]
    public void Same_key_commands_merge_inside_a_group()
    {
        var stack = new UndoStack(Create());

        stack.BeginGroup("Arrastre");
        for (int i = 1; i <= 5; i++)
        {
            stack.Execute(Edits.Move(SequenceId, "clip-2", S(10 + i)));
        }

        stack.EndGroup();

        Assert.Equal(1, stack.Count);
        var group = Assert.IsType<CompositeCommand>(stack.UndoCommands[^1]);
        Assert.Single(group.Commands);
    }

    // «BeginGroup/EndGroup dejan varios comandos como un único CompositeCommand, que se aplica todo o nada; CancelGroup
    // revierte lo hecho.»
    [Fact]
    public void Group_becomes_one_CompositeCommand_step()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        stack.BeginGroup("Gesto");
        stack.Execute(Edits.AddMarker(SequenceId, S(1)));
        stack.Execute(Edits.Split(SequenceId, "clip-2", S(12)));
        stack.EndGroup();
        string after = ProjectDump.Of(project);

        Assert.Equal(1, stack.Count);
        Assert.IsType<CompositeCommand>(stack.UndoCommands[^1]);
        Assert.Equal("Gesto", stack.UndoDescription);

        stack.Undo();
        Assert.Equal(original, ProjectDump.Of(project));
        stack.Redo();
        Assert.Equal(after, ProjectDump.Of(project));
    }

    [Fact]
    public void CompositeCommand_applies_all_or_nothing()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        var composite = new CompositeCommand(
            "Dos pasos",
            [
                Edits.AddMarker(SequenceId, S(1)),
                Edits.Move(SequenceId, "clip-2", S(20)),
                new SequenceEditCommand("Falla", SequenceId, _ => throw new EditException("no")),
            ]);

        Assert.Throws<EditException>(() => stack.Execute(composite));

        Assert.Equal(original, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void CancelGroup_reverts_what_was_done()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.AddMarker(SequenceId, S(1)));
        string beforeGesture = ProjectDump.Of(project);

        stack.BeginGroup("Gesto");
        stack.BeginGroup("Interno");
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(20)));
        stack.Execute(Edits.Split(SequenceId, "clip-1", S(2)));
        stack.CancelGroup();

        Assert.False(stack.IsInGroup);
        Assert.Equal(beforeGesture, ProjectDump.Of(project));
        Assert.Equal(1, stack.Count);
    }

    // «MarkClean/IsClean dicen si el proyecto está como en el último guardado; nunca se funde un comando sobre ese punto.»
    [Fact]
    public void IsClean_follows_the_last_MarkClean()
    {
        var stack = new UndoStack(Create());
        Assert.True(stack.IsClean);

        stack.Execute(Edits.AddMarker(SequenceId, S(1)));
        Assert.False(stack.IsClean);

        stack.MarkClean();
        Assert.True(stack.IsClean);

        stack.Execute(Edits.AddMarker(SequenceId, S(2)));
        Assert.False(stack.IsClean);

        stack.Undo();
        Assert.True(stack.IsClean);

        stack.Undo();
        Assert.False(stack.IsClean);
    }

    [Fact]
    public void A_command_never_merges_over_the_clean_point()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(11)));
        stack.MarkClean();

        stack.Execute(Edits.Move(SequenceId, "clip-2", S(12)));

        Assert.Equal(2, stack.Count);
        stack.Undo();
        Assert.True(stack.IsClean);
        Assert.Equal(S(11), Seq(project).GetClip("clip-2").Clip.Start);
    }

    // «El historial guarda 500 pasos por omisión.»
    [Fact]
    public void History_keeps_500_steps_by_default()
    {
        var stack = new UndoStack(Create());
        Assert.Equal(500, stack.Limit);

        for (int i = 0; i < 501; i++)
        {
            stack.Execute(new RenameProjectCommand($"Nombre {i}"));
        }

        Assert.Equal(500, stack.Count);
    }

    // «ProjectCommands cubre medios y secuencias (no deja quitar un medio en uso).»
    [Fact]
    public void RemoveMediaCommand_rejects_media_in_use()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        Assert.Throws<EditException>(() => stack.Execute(new RemoveMediaCommand("media-1")));

        Assert.Equal(original, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void ProjectCommands_cover_media_and_sequences_with_undo()
    {
        Project project = Create();
        string original = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        var media = new MediaRef { Id = "media-2", Name = "otra.mp4", Path = "otra.mp4", Duration = S(5), HasAudio = true, SampleRate = 48000, AudioChannels = 2 };
        var sequence = new Sequence { Id = "seq-2", Name = "Otra" };

        stack.Execute(new AddMediaCommand(media));
        stack.Execute(new AddSequenceCommand(sequence));
        stack.Execute(new RemoveMediaCommand("media-2"));
        stack.Execute(new RemoveSequenceCommand("seq-2"));
        Assert.Null(project.FindSequence("seq-2"));
        Assert.DoesNotContain(project.Media, m => m.Id == "media-2");

        while (stack.Undo())
        {
        }

        Assert.Equal(original, ProjectDump.Of(project));
    }
}
