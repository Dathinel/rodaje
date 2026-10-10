using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Model;

public class ProjectCloneTests
{
    [Fact]
    public void Clone_EsUnaCopiaProfundaIgualAlOriginal()
    {
        Project project = Create();

        Project copy = project.Clone();

        Assert.Equal(ProjectSerializer.Serialize(project), ProjectSerializer.Serialize(copy));
        Assert.NotSame(project.Media[0], copy.Media[0]);
        Assert.NotSame(project.Sequences[0], copy.Sequences[0]);
        Assert.NotSame(project.Sequences[0].VideoTracks[0], copy.Sequences[0].VideoTracks[0]);
    }

    [Fact]
    public void Clone_NoSeAfectaAlEditarElOriginal()
    {
        Project project = Create();
        Project copy = project.Clone();
        string before = ProjectSerializer.Serialize(copy);

        project.Name = "Otro";
        project.Media[0].Path = "cambiado.mp4";
        Sequence sequence = project.Sequences[0];
        Clip clip = sequence.AllTracks.SelectMany(t => t.Clips).First();
        new UndoStack(project).Execute(Edits.Split(sequence.Id, clip.Id, Rodaje.Core.Timing.Time.FromTicks(clip.Start.Ticks + ((clip.End.Ticks - clip.Start.Ticks) / 2))));

        Assert.Equal(before, ProjectSerializer.Serialize(copy));
    }

    [Fact]
    public void UndoStack_ExponeLosPasosYElPuntoLimpio()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(new RenameProjectCommand("Uno"));
        stack.Execute(new RenameProjectCommand("Dos"));
        stack.MarkClean();
        stack.Undo();

        Assert.Equal(["Renombrar proyecto"], stack.UndoCommands.Select(c => c.Description));
        Assert.Single(stack.RedoCommands);
        Assert.Equal(2, stack.CleanIndex);

        stack.Execute(new RenameProjectCommand("Tres"));
        Assert.Null(stack.CleanIndex);
        Assert.Empty(stack.RedoCommands);
    }
}
