using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

public class UndoStackTests
{
    private static (UndoStack Stack, Project Project) NewStack(int limit = 500)
    {
        Project project = Create();
        return (new UndoStack(project, limit), project);
    }

    [Fact]
    public void Nueva_EstaVaciaYLimpia()
    {
        (UndoStack stack, Project project) = NewStack();

        Assert.Same(project, stack.Project);
        Assert.Equal(500, stack.Limit);
        Assert.Equal(0, stack.Count);
        Assert.Equal(0, stack.RedoCount);
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Null(stack.UndoDescription);
        Assert.Null(stack.RedoDescription);
        Assert.True(stack.IsClean);
        Assert.False(stack.IsInGroup);
        Assert.False(stack.Undo());
        Assert.False(stack.Redo());
    }

    [Fact]
    public void Constructor_ValidaArgumentos()
    {
        Assert.Throws<ArgumentNullException>(() => new UndoStack(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UndoStack(Create(), 0));
        Assert.Throws<ArgumentNullException>(() => new UndoStack(Create()).Execute(null!));
        Assert.Throws<ArgumentNullException>(() => new UndoStack(Create()).BeginGroup(null!));
    }

    [Fact]
    public void ExecuteUndoRedo_ConEdicionesReales()
    {
        (UndoStack stack, Project project) = NewStack();
        Sequence seq = Seq(project);
        Track v1 = seq.VideoTracks[0];
        string s0 = ProjectDump.Of(project);
        var ids = new SequentialIdGenerator();

        stack.Execute(new SequenceEditCommand("Mover clip", SequenceId, ctx => ctx.Sequence.GetClip("clip-1").Clip.Start = S(6)));
        string s1 = ProjectDump.Of(project);
        var add = new SequenceEditCommand("Agregar clip", SequenceId, ctx =>
        {
            string id = ctx.Ids.NewId("nuevo");
            ctx.Sequence.GetTrack("track-v1").Clips.Add(NewClip(id, 40));
            return [id];
        }, ids);
        stack.Execute(add);
        string s2 = ProjectDump.Of(project);

        Assert.Equal(2, stack.Count);
        Assert.Equal("Agregar clip", stack.UndoDescription);
        Assert.True(stack.Undo());
        Assert.Equal(s1, ProjectDump.Of(project));
        Assert.Equal("Agregar clip", stack.RedoDescription);
        Assert.Equal("Mover clip", stack.UndoDescription);
        Assert.True(stack.Undo());
        Assert.Equal(s0, ProjectDump.Of(project));
        Assert.False(stack.CanUndo);
        Assert.Equal(2, stack.RedoCount);

        Assert.True(stack.Redo());
        Assert.True(stack.Redo());
        Assert.Equal(s2, ProjectDump.Of(project));
        Assert.Equal(["nuevo-1"], add.CreatedIds);
        Assert.NotNull(seq.FindClip("nuevo-1"));
        Assert.Same(v1, seq.VideoTracks[0]);
    }

    [Fact]
    public void Execute_SiFallaNoRegistraNada()
    {
        (UndoStack stack, Project project) = NewStack();
        stack.Execute(new FakeCommand("a"));
        stack.Undo();
        int changes = 0;
        stack.Changed += (_, _) => changes++;
        string before = ProjectDump.Of(project);

        Assert.Throws<EditException>(() => stack.Execute(
            new SequenceEditCommand("Bloqueada", SequenceId, ctx => ctx.Sequence.GetClip("clip-3").Clip.Start = S(50))));

        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
        Assert.Equal(1, stack.RedoCount);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Execute_BorraLoQueSePodiaRehacer()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a"));
        stack.Execute(new FakeCommand("b"));
        stack.Undo();

        stack.Execute(new FakeCommand("c"));

        Assert.False(stack.CanRedo);
        Assert.Equal("c", stack.UndoDescription);
        Assert.Equal("Prueba+a+c", stack.Project.Name);
    }

    [Fact]
    public void Merge_FundeArrastreEnUnSoloPaso()
    {
        (UndoStack stack, Project project) = NewStack();
        string before = ProjectDump.Of(project);

        for (int i = 1; i <= 5; i++)
        {
            double start = 5 + i;
            stack.Execute(new SequenceEditCommand("Mover clip", SequenceId, ctx => ctx.Sequence.GetClip("clip-1").Clip.Start = S(start), mergeKey: "arrastre"));
        }

        Assert.Equal(1, stack.Count);
        Assert.Equal(S(10), Seq(project).GetClip("clip-1").Clip.Start);
        stack.Undo();
        Assert.Equal(before, ProjectDump.Of(project));
        stack.Redo();
        Assert.Equal(S(10), Seq(project).GetClip("clip-1").Clip.Start);
    }

    [Fact]
    public void Merge_NoFundeSobreElPuntoLimpio()
    {
        (UndoStack stack, _) = NewStack();
        var first = new FakeCommand("a") { MergeKey = "k" };
        stack.Execute(first);
        stack.MarkClean();

        stack.Execute(new FakeCommand("b") { MergeKey = "k" });

        Assert.Equal(2, stack.Count);
        Assert.Empty(first.Merged);
        Assert.False(stack.IsClean);
        stack.Undo();
        Assert.True(stack.IsClean);
    }

    [Fact]
    public void Merge_NoFundeComandosDistintos()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a") { MergeKey = "k" });
        stack.Execute(new FakeCommand("b") { MergeKey = "otra" });
        stack.Execute(new FakeCommand("c") { MergeKey = "otra" });

        Assert.Equal(2, stack.Count);
        stack.Undo();
        Assert.Equal("Prueba+a", stack.Project.Name);
    }

    [Fact]
    public void Group_QuedaComoUnSoloPaso()
    {
        (UndoStack stack, Project project) = NewStack();
        var log = new CommandLog();

        stack.BeginGroup("Pegar");
        Assert.True(stack.IsInGroup);
        stack.Execute(new FakeCommand("a", log));
        Assert.Equal("Prueba+a", project.Name);
        stack.Execute(new FakeCommand("b", log));
        Assert.Equal(0, stack.Count);
        Assert.False(stack.CanUndo);
        stack.EndGroup();

        Assert.Equal(1, stack.Count);
        Assert.Equal("Pegar", stack.UndoDescription);
        Assert.True(stack.Undo());
        Assert.Equal("Prueba", project.Name);
        Assert.True(stack.Redo());
        Assert.Equal("Prueba+a+b", project.Name);
        Assert.Equal(["apply a", "apply b", "revert b", "revert a", "apply a", "apply b"], log.Entries);
    }

    [Fact]
    public void Group_FundeDentroPeroNoConElAnterior()
    {
        (UndoStack stack, _) = NewStack();
        var outside = new FakeCommand("x") { MergeKey = "k" };
        stack.Execute(outside);
        stack.BeginGroup("G");
        var first = new FakeCommand("a") { MergeKey = "k" };
        stack.Execute(first);
        stack.Execute(new FakeCommand("b") { MergeKey = "k" });
        stack.EndGroup();

        Assert.Empty(outside.Merged);
        Assert.Equal("b", Assert.Single(first.Merged).Description);
        Assert.Equal(2, stack.Count);
        Assert.Equal("Prueba+x+a+b", stack.Project.Name);
        stack.Undo();
        Assert.Equal("Prueba+x", stack.Project.Name);
    }

    [Fact]
    public void Group_AnidadoSoloCierraConElExterno()
    {
        (UndoStack stack, Project project) = NewStack();

        stack.BeginGroup("Externo");
        stack.Execute(new FakeCommand("a"));
        stack.BeginGroup("Interno");
        stack.Execute(new FakeCommand("b"));
        stack.EndGroup();
        Assert.True(stack.IsInGroup);
        Assert.Equal(0, stack.Count);
        stack.Execute(new FakeCommand("c"));
        stack.EndGroup();

        Assert.False(stack.IsInGroup);
        Assert.Equal(1, stack.Count);
        Assert.Equal("Externo", stack.UndoDescription);
        stack.Undo();
        Assert.Equal("Prueba", project.Name);
    }

    [Fact]
    public void Group_VacioNoDejaNada()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a"));
        stack.Undo();
        stack.BeginGroup("Nada");
        stack.EndGroup();

        Assert.Equal(0, stack.Count);
        Assert.True(stack.CanRedo);
        Assert.True(stack.IsClean);
    }

    [Fact]
    public void Group_SiUnComandoFallaElGrupoSigueAbiertoConLosDemas()
    {
        (UndoStack stack, Project project) = NewStack();
        stack.BeginGroup("G");
        stack.Execute(new FakeCommand("a"));
        Assert.Throws<InvalidOperationException>(() => stack.Execute(new FakeCommand("b") { ThrowOnApply = true }));
        stack.EndGroup();

        Assert.Equal(1, stack.Count);
        stack.Undo();
        Assert.Equal("Prueba", project.Name);
    }

    [Fact]
    public void CancelGroup_RevierteYDescarta()
    {
        (UndoStack stack, Project project) = NewStack();
        var log = new CommandLog();
        stack.Execute(new FakeCommand("x", log));
        string before = ProjectDump.Of(project);

        stack.BeginGroup("G");
        stack.Execute(new FakeCommand("a", log));
        stack.BeginGroup("Interno");
        stack.Execute(new FakeCommand("b", log));
        Assert.False(stack.IsClean);
        stack.CancelGroup();

        Assert.False(stack.IsInGroup);
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Equal(1, stack.Count);
        Assert.Equal("x", stack.UndoDescription);
        Assert.Equal(["apply x", "apply a", "apply b", "revert b", "revert a"], log.Entries);
    }

    [Fact]
    public void Group_OperacionesNoPermitidas()
    {
        (UndoStack stack, _) = NewStack();
        Assert.Throws<InvalidOperationException>(stack.EndGroup);
        Assert.Throws<InvalidOperationException>(stack.CancelGroup);

        stack.Execute(new FakeCommand("a"));
        stack.Undo();
        stack.BeginGroup("G");
        Assert.False(stack.CanRedo);
        Assert.Throws<InvalidOperationException>(() => stack.Undo());
        Assert.Throws<InvalidOperationException>(() => stack.Redo());
        Assert.Throws<InvalidOperationException>(stack.MarkClean);
    }

    [Fact]
    public void Limite_DescartaLosMasAntiguos()
    {
        (UndoStack stack, Project project) = NewStack(limit: 3);
        foreach (string name in new[] { "a", "b", "c", "d", "e" })
        {
            stack.Execute(new FakeCommand(name));
        }

        Assert.Equal(3, stack.Count);
        while (stack.Undo())
        {
        }

        Assert.Equal("Prueba+a+b", project.Name);
    }

    [Fact]
    public void Limite_TambienAplicaAGrupos()
    {
        (UndoStack stack, _) = NewStack(limit: 1);
        stack.Execute(new FakeCommand("a"));
        stack.BeginGroup("G");
        stack.Execute(new FakeCommand("b"));
        stack.EndGroup();

        Assert.Equal(1, stack.Count);
        Assert.Equal("G", stack.UndoDescription);
    }

    [Fact]
    public void Limpio_SigueLaPosicion()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a"));
        Assert.False(stack.IsClean);
        stack.MarkClean();
        Assert.True(stack.IsClean);

        stack.Execute(new FakeCommand("b"));
        Assert.False(stack.IsClean);
        stack.Undo();
        Assert.True(stack.IsClean);
        stack.Undo();
        Assert.False(stack.IsClean);
        stack.Redo();
        Assert.True(stack.IsClean);
    }

    [Fact]
    public void Limpio_SePierdeSiSeBorraElRehacerDondeEstaba()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a"));
        stack.MarkClean();
        stack.Undo();
        stack.Execute(new FakeCommand("b"));
        Assert.False(stack.IsClean);
        stack.Undo();
        Assert.False(stack.IsClean);

        stack.MarkClean();
        Assert.True(stack.IsClean);
    }

    [Fact]
    public void Limpio_SePierdeSiElLimiteDescartaSuPaso()
    {
        (UndoStack stack, _) = NewStack(limit: 2);
        stack.Execute(new FakeCommand("a"));
        stack.MarkClean();
        stack.Execute(new FakeCommand("b"));
        stack.Execute(new FakeCommand("c"));
        Assert.Equal(2, stack.Count);
        stack.Undo();
        Assert.False(stack.IsClean);
        stack.Undo();
        Assert.True(stack.IsClean);

        stack.Redo();
        stack.Redo();
        stack.Execute(new FakeCommand("d"));
        stack.Execute(new FakeCommand("e"));
        while (stack.Undo())
        {
            Assert.False(stack.IsClean);
        }

        Assert.False(stack.IsClean);
    }

    [Fact]
    public void Limpio_EnLaPosicionCeroSePierdeAlDescartarElPrimero()
    {
        (UndoStack stack, _) = NewStack(limit: 1);
        stack.Execute(new FakeCommand("a"));
        stack.Execute(new FakeCommand("b"));
        stack.Undo();

        Assert.Equal(0, stack.Count);
        Assert.False(stack.IsClean);
    }

    [Fact]
    public void Changed_SeDisparaEnCadaCambio()
    {
        (UndoStack stack, _) = NewStack();
        int changes = 0;
        stack.Changed += (sender, args) =>
        {
            Assert.Same(stack, sender);
            Assert.Same(EventArgs.Empty, args);
            changes++;
        };

        stack.Execute(new FakeCommand("a"));
        Assert.Equal(1, changes);
        stack.Undo();
        Assert.Equal(2, changes);
        stack.Redo();
        Assert.Equal(3, changes);
        stack.MarkClean();
        Assert.Equal(4, changes);
        stack.BeginGroup("G");
        Assert.Equal(5, changes);
        stack.Execute(new FakeCommand("b"));
        Assert.Equal(6, changes);
        stack.EndGroup();
        Assert.Equal(7, changes);
        stack.BeginGroup("H");
        stack.CancelGroup();
        Assert.Equal(9, changes);

        stack.Undo();
        stack.Undo();
        Assert.Equal(11, changes);
        Assert.False(stack.Undo());
        Assert.False(stack.Redo() && stack.Redo() && stack.Redo());
        Assert.Equal(13, changes);
    }

    [Fact]
    public void UndoYRedo_SiElComandoFallaNoCambianLasPilas()
    {
        (UndoStack stack, _) = NewStack();
        var command = new FakeCommand("a");
        stack.Execute(command);

        command.ThrowOnRevert = true;
        Assert.Throws<InvalidOperationException>(() => stack.Undo());
        Assert.Equal(1, stack.Count);
        Assert.Equal(0, stack.RedoCount);

        command.ThrowOnRevert = false;
        stack.Undo();
        command.ThrowOnApply = true;
        Assert.Throws<InvalidOperationException>(() => stack.Redo());
        Assert.Equal(0, stack.Count);
        Assert.Equal(1, stack.RedoCount);
    }

    [Fact]
    public void ComandosDeProyectoYDeEdicionMezclados()
    {
        (UndoStack stack, Project project) = NewStack();
        string s0 = ProjectDump.Of(project);
        var media = new MediaRef { Id = "media-2", Name = "m2", Path = "m2.mp4", Duration = S(10), HasVideo = true };

        stack.Execute(new AddMediaCommand(media));
        stack.Execute(new SequenceEditCommand("Agregar", SequenceId, ctx =>
            ctx.Sequence.GetTrack("track-v1").Clips.Add(new Clip { Id = "c-m2", MediaId = "media-2", SourceOut = S(2), Start = S(20) })));
        stack.Execute(new RenameProjectCommand("Final"));
        Assert.Throws<EditException>(() => stack.Execute(new RemoveMediaCommand("media-2")));
        string s3 = ProjectDump.Of(project);
        Assert.Empty(ModelValidator.Validate(project));

        while (stack.Undo())
        {
        }

        Assert.Equal(s0, ProjectDump.Of(project));
        while (stack.Redo())
        {
        }

        Assert.Equal(s3, ProjectDump.Of(project));
    }
    [Fact]
    public void BreakMerge_separa_dos_gestos_con_la_misma_clave()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a1") { MergeKey = "k" });
        stack.Execute(new FakeCommand("a2") { MergeKey = "k" });
        Assert.Equal(1, stack.Count);

        stack.BreakMerge();
        stack.Execute(new FakeCommand("b1") { MergeKey = "k" });
        stack.Execute(new FakeCommand("b2") { MergeKey = "k" });
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void Deshacer_corta_la_fusion_con_el_paso_anterior()
    {
        (UndoStack stack, _) = NewStack();
        stack.Execute(new FakeCommand("a") { MergeKey = "k" });
        stack.Execute(new FakeCommand("b") { MergeKey = "k" });
        stack.Undo();
        stack.Redo();
        stack.Execute(new FakeCommand("c") { MergeKey = "k" });
        Assert.Equal(2, stack.Count);
    }
}
