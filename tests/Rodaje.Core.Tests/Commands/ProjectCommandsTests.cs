using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

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
    public void AddMedia_RechazaMedioQueDejaElProyectoInvalido()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);

        MediaRef zero = Media("media-2");
        zero.Duration = S(0);
        EditException error = Assert.Throws<EditException>(() => new AddMediaCommand(zero).Apply(project));
        Assert.Contains("No se puede importar el medio", error.Message, StringComparison.Ordinal);
        Assert.Contains("duración no positiva", error.Message, StringComparison.Ordinal);

        // Un identificador que ya usa otro objeto (aquí, la secuencia) también se rechaza.
        EditException clash = Assert.Throws<EditException>(() => new AddMediaCommand(Media(SequenceId)).Apply(project));
        Assert.Contains("repetido", clash.Message, StringComparison.Ordinal);

        Assert.Throws<EditException>(() => new AddMediaCommand(Media(" ")).Apply(project));

        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void AddMedia_EnUnProyectoYaInvalido_SoloCuentanLosProblemasNuevos()
    {
        Project project = Create();
        project.Media[0].Duration = S(0); // problema previo, ajeno al comando
        int previous = ModelValidator.Validate(project).Count;
        Assert.True(previous > 0);

        new AddMediaCommand(Media("media-2")).Apply(project);
        Assert.NotNull(project.FindMedia("media-2"));
        Assert.Equal(previous, ModelValidator.Validate(project).Count);

        // Repetir exactamente un problema que ya existía cuenta como nuevo (se cuentan repeticiones).
        MediaRef sameProblem = Media("media-3");
        sameProblem.Duration = S(0);
        Assert.Throws<EditException>(() => new AddMediaCommand(sameProblem).Apply(project));
        Assert.Null(project.FindMedia("media-3"));
    }

    [Fact]
    public void AddMedia_ConLaPila_ValidoSeDeshaceEInvalidoNoEntra()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        stack.Execute(new AddMediaCommand(Media("media-2")));
        Assert.Throws<EditException>(() => stack.Execute(new AddMediaCommand(Media("media-2"))));
        Assert.Equal(1, stack.Count);

        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.True(stack.Redo());
        Assert.Empty(ModelValidator.Validate(project));
    }

    public static TheoryData<string, Func<Sequence>, string> InvalidSequences => new()
    {
        {
            "pista con id existente",
            () => new Sequence { Id = "seq-2", VideoTracks = [new Track { Id = "track-v1", Kind = TrackKind.Video, Name = "V" }] },
            "«track-v1» está repetido"
        },
        {
            "clip con id existente",
            () => new Sequence
            {
                Id = "seq-2",
                VideoTracks = [new Track { Id = "t-nueva", Kind = TrackKind.Video, Name = "V", Clips = [NewClip("clip-1", 0)] }],
            },
            "«clip-1» está repetido"
        },
        {
            "clip con medio inexistente",
            () =>
            {
                Clip clip = NewClip("c-nuevo", 0);
                clip.MediaId = "no-existe";
                return new Sequence
                {
                    Id = "seq-2",
                    VideoTracks = [new Track { Id = "t-nueva", Kind = TrackKind.Video, Name = "V", Clips = [clip] }],
                };
            },
            "que no existe"
        },
        {
            "clips solapados",
            () => new Sequence
            {
                Id = "seq-2",
                VideoTracks = [new Track { Id = "t-nueva", Kind = TrackKind.Video, Name = "V", Clips = [NewClip("c-a", 0, 2), NewClip("c-b", 1, 2)] }],
            },
            "se solapa"
        },
        {
            "dimensiones no positivas",
            () => new Sequence { Id = "seq-2", Width = 0 },
            "dimensiones"
        },
        {
            "sin identificador",
            () => new Sequence { Id = "" },
            "sin identificador"
        },
    };

    [Theory]
    [MemberData(nameof(InvalidSequences))]
    public void AddSequence_RechazaSecuenciaQueDejaElProyectoInvalido(string caseName, Func<Sequence> make, string expected)
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        EditException error = Assert.Throws<EditException>(() => stack.Execute(new AddSequenceCommand(make(), 0)));

        Assert.Contains("No se puede agregar la secuencia", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Contains(expected, StringComparison.Ordinal), $"{caseName}: {error.Message}");
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Equal(0, stack.Count);
    }

    [Fact]
    public void AddSequence_ValidaConClipsDeMediosExistentes_Entra()
    {
        Project project = Create();
        var sequence = new Sequence
        {
            Id = "seq-2",
            VideoTracks = [new Track { Id = "t-nueva", Kind = TrackKind.Video, Name = "V", Clips = [NewClip("c-a", 0, 2), NewClip("c-b", 2, 2)] }],
        };

        new AddSequenceCommand(sequence).Apply(project);

        Assert.Same(sequence, project.Sequences[^1]);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void FirstNewProblem_CuentaRepeticiones()
    {
        Assert.Null(ProjectGuard.FirstNewProblem(["a", "b"], ["b", "a"]));
        Assert.Null(ProjectGuard.FirstNewProblem(["a", "b"], ["a"]));
        Assert.Equal("c", ProjectGuard.FirstNewProblem(["a"], ["a", "c"]));
        Assert.Equal("a", ProjectGuard.FirstNewProblem(["a"], ["a", "a"]));
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
