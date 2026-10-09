using FotoToma.Core.Commands;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using static FotoToma.Core.Tests.Commands.CommandFixtures;

namespace FotoToma.Core.Tests.Commands;

public class ProjectCommandsTests
{
    private static MediaRef Media(string id) => new() { Id = id, Name = id, Path = $"{id}.wav", Duration = S(3), HasAudio = true };

    [Fact]
    public void AddMedia_AgregaYAlDeshacerLoQuita()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        MediaRef media = Media("media-2");
        var command = new AddMediaCommand(media);

        command.Apply(project);
        Assert.Same(media, project.Media[^1]);
        command.Revert(project);
        Assert.Equal(before, ProjectDump.Of(project));
        command.Apply(project);
        Assert.Same(media, project.FindMedia("media-2"));
        Assert.Equal("Importar medio", command.Description);
        Assert.False(command.TryMerge(command));
    }

    [Fact]
    public void AddMedia_RechazaIdRepetido()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);

        EditException error = Assert.Throws<EditException>(() => new AddMediaCommand(Media("media-1")).Apply(project));

        Assert.Contains("media-1", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void RemoveMedia_VuelveALaMismaPosicion()
    {
        Project project = Create();
        project.Media.Insert(0, Media("media-0"));
        project.Media.Add(Media("media-2"));
        string before = ProjectDump.Of(project);
        MediaRef removed = project.Media[0];
        var command = new RemoveMediaCommand("media-0");

        command.Apply(project);
        Assert.Null(project.FindMedia("media-0"));
        command.Revert(project);

        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Same(removed, project.Media[0]);
        Assert.Equal("Quitar medio", command.Description);
        Assert.False(command.TryMerge(command));
    }

    [Fact]
    public void RemoveMedia_AlDeshacerConListaMasCortaVaAlFinal()
    {
        Project project = Create();
        project.Media.Add(Media("media-2"));
        var command = new RemoveMediaCommand("media-2");
        command.Apply(project);
        project.Media.Clear();

        command.Revert(project);

        Assert.Equal("media-2", Assert.Single(project.Media).Id);
    }

    [Fact]
    public void RemoveMedia_RechazaMedioEnUsoOInexistente()
    {
        Project project = Create();
        project.Sequences.Insert(0, new Sequence { Id = "vacia" });
        string before = ProjectDump.Of(project);

        EditException inUse = Assert.Throws<EditException>(() => new RemoveMediaCommand("media-1").Apply(project));
        Assert.Contains("«Principal»", inUse.Message, StringComparison.Ordinal);
        Assert.Throws<EditException>(() => new RemoveMediaCommand("no-existe").Apply(project));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void AddSequence_AlFinalOEnPosicion()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var last = new Sequence { Id = "seq-2" };
        var first = new Sequence { Id = "seq-0" };
        var addLast = new AddSequenceCommand(last);
        var addFirst = new AddSequenceCommand(first, 0);

        addLast.Apply(project);
        addFirst.Apply(project);
        Assert.Equal(["seq-0", SequenceId, "seq-2"], project.Sequences.Select(s => s.Id));

        addFirst.Revert(project);
        addLast.Revert(project);
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Equal("Nueva secuencia", addLast.Description);
        Assert.False(addLast.TryMerge(addFirst));
    }

    [Fact]
    public void AddSequence_RechazaIdRepetidoYPosicionInvalida()
    {
        Project project = Create();
        Assert.Throws<EditException>(() => new AddSequenceCommand(new Sequence { Id = SequenceId }).Apply(project));
        Assert.Throws<EditException>(() => new AddSequenceCommand(new Sequence { Id = "seq-2" }, 2).Apply(project));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddSequenceCommand(new Sequence { Id = "seq-2" }, -1));
        Assert.Single(project.Sequences);

        new AddSequenceCommand(new Sequence { Id = "seq-2" }, 1).Apply(project);
        Assert.Equal("seq-2", project.Sequences[1].Id);
    }

    [Fact]
    public void RemoveSequence_VuelveALaMismaPosicionComoElMismoObjeto()
    {
        Project project = Create();
        project.Sequences.Insert(0, new Sequence { Id = "seq-0" });
        project.Sequences.Add(new Sequence { Id = "seq-2" });
        string before = ProjectDump.Of(project);
        Sequence removed = project.Sequences[1];
        var command = new RemoveSequenceCommand(SequenceId);

        command.Apply(project);
        Assert.Null(project.FindSequence(SequenceId));
        command.Revert(project);

        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Same(removed, project.Sequences[1]);
        Assert.Equal("Eliminar secuencia", command.Description);
        Assert.False(command.TryMerge(command));
    }

    [Fact]
    public void RemoveSequence_AlDeshacerConListaMasCortaVaAlFinalYFallaSiNoExiste()
    {
        Project project = Create();
        project.Sequences.Add(new Sequence { Id = "seq-2" });
        var command = new RemoveSequenceCommand("seq-2");
        command.Apply(project);
        project.Sequences.Clear();
        command.Revert(project);
        Assert.Equal("seq-2", Assert.Single(project.Sequences).Id);

        Assert.Throws<EditException>(() => new RemoveSequenceCommand("no-existe").Apply(project));
    }

    [Fact]
    public void RenameProject_CambiaYRestaura()
    {
        Project project = Create();
        var command = new RenameProjectCommand("Nuevo");

        command.Apply(project);
        Assert.Equal("Nuevo", project.Name);
        command.Revert(project);
        Assert.Equal("Prueba", project.Name);
        Assert.Equal("Renombrar proyecto", command.Description);
        Assert.False(command.TryMerge(command));
    }

    [Fact]
    public void RenameProject_RechazaNombreVacio()
    {
        Project project = Create();
        Assert.Throws<EditException>(() => new RenameProjectCommand("  ").Apply(project));
        Assert.Equal("Prueba", project.Name);
    }

    [Fact]
    public void RevertSinAplicarFalla()
    {
        Project project = Create();
        Assert.Throws<InvalidOperationException>(() => new RemoveMediaCommand("media-1").Revert(project));
        Assert.Throws<InvalidOperationException>(() => new RemoveSequenceCommand(SequenceId).Revert(project));
        Assert.Throws<InvalidOperationException>(() => new RenameProjectCommand("x").Revert(project));
    }

    [Fact]
    public void ValidanArgumentosNulos()
    {
        var commands = new List<ICommand>
        {
            new AddMediaCommand(Media("m")),
            new RemoveMediaCommand("m"),
            new AddSequenceCommand(new Sequence()),
            new RemoveSequenceCommand("s"),
            new RenameProjectCommand("n"),
        };

        foreach (ICommand command in commands)
        {
            Assert.Throws<ArgumentNullException>(() => command.Apply(null!));
            Assert.Throws<ArgumentNullException>(() => command.Revert(null!));
        }

        Assert.Throws<ArgumentNullException>(() => new AddMediaCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new RemoveMediaCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new AddSequenceCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new RemoveSequenceCommand(null!));
        Assert.Throws<ArgumentNullException>(() => new RenameProjectCommand(null!));
    }
}
