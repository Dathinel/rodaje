using FotoToma.Core.Commands;
using FotoToma.Core.Model;
using static FotoToma.Core.Tests.Commands.CommandFixtures;

namespace FotoToma.Core.Tests.Commands;

public class CompositeCommandTests
{
    [Fact]
    public void AplicaEnOrdenYRevierteAlReves()
    {
        Project project = Create();
        var log = new CommandLog();
        var composite = new CompositeCommand("Varios", [new FakeCommand("a", log), new FakeCommand("b", log), new FakeCommand("c", log)]);

        composite.Apply(project);
        Assert.Equal("Prueba+a+b+c", project.Name);
        composite.Revert(project);

        Assert.Equal("Prueba", project.Name);
        Assert.Equal(["apply a", "apply b", "apply c", "revert c", "revert b", "revert a"], log.Entries);
        Assert.Equal("Varios", composite.Description);
        Assert.Equal(3, composite.Commands.Count);
    }

    [Fact]
    public void SiUnoFallaReviertelosAplicadosYRelanza()
    {
        Project project = Create();
        var log = new CommandLog();
        var failing = new FakeCommand("c", log) { ThrowOnApply = true };
        var composite = new CompositeCommand("Varios", [new FakeCommand("a", log), new FakeCommand("b", log), failing, new FakeCommand("d", log)]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => composite.Apply(project));

        Assert.Equal("falla c", error.Message);
        Assert.Equal("Prueba", project.Name);
        Assert.Equal(["apply a", "apply b", "revert b", "revert a"], log.Entries);
    }

    [Fact]
    public void SiElPrimeroFallaNoRevierteNada()
    {
        Project project = Create();
        var log = new CommandLog();
        var composite = new CompositeCommand("x", [new FakeCommand("a", log) { ThrowOnApply = true }]);

        Assert.Throws<InvalidOperationException>(() => composite.Apply(project));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void RollbackConEdicionesRealesDejaElProyectoIntacto()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var composite = new CompositeCommand("Mover y fallar",
        [
            new SequenceEditCommand("Mover", SequenceId, ctx => ctx.Sequence.GetClip("clip-1").Clip.Start = S(6)),
            new RenameProjectCommand("Otro"),
            new SequenceEditCommand("Tocar bloqueada", SequenceId, ctx => ctx.Sequence.GetClip("clip-3").Clip.Start = S(50)),
        ]);

        Assert.Throws<Editing.EditException>(() => composite.Apply(project));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void FromApplied_SoloNecesitaRevertYLuegoRehace()
    {
        Project project = Create();
        var a = new FakeCommand("a");
        var b = new FakeCommand("b");
        a.Apply(project);
        b.Apply(project);

        CompositeCommand composite = CompositeCommand.FromApplied("Grupo", [a, b]);
        composite.Revert(project);
        Assert.Equal("Prueba", project.Name);
        composite.Apply(project);

        Assert.Equal("Prueba+a+b", project.Name);
        Assert.Equal(2, a.ApplyCount);
    }

    [Fact]
    public void NuncaFunde()
    {
        var composite = new CompositeCommand("x", []);
        Assert.False(composite.TryMerge(new CompositeCommand("y", [])));
        Assert.False(composite.TryMerge(new FakeCommand("z") { MergeKey = "k" }));
    }

    [Fact]
    public void ValidaArgumentos()
    {
        Assert.Throws<ArgumentNullException>(() => new CompositeCommand(null!, []));
        Assert.Throws<ArgumentNullException>(() => new CompositeCommand("x", null!));
        Assert.Throws<ArgumentException>(() => new CompositeCommand("x", [new FakeCommand("a"), null!]));
        var composite = new CompositeCommand("x", []);
        Assert.Throws<ArgumentNullException>(() => composite.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => composite.Revert(null!));
    }
}
