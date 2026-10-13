using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Regresiones de la tarea de deshacer exacto, cada una con el caso mínimo del defecto.</summary>
public sealed class UndoExactnessRegressionTests
{
    /// <summary>
    /// Semillas 111 y 1019 de <see cref="UndoExactnessPropertiesTests"/>: quitar la secuencia de un clip compuesto dejaba el
    /// clip apuntando a nada; el archivo guardado así ya no se abría.
    /// </summary>
    [Fact]
    public void Quitar_la_secuencia_de_un_compuesto_se_rechaza_sin_cambiar_nada()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        SequenceEditCommand create = Edits.CreateCompound(SequenceId, ["clip-2"], "Anidada");
        stack.Execute(create);
        string nestedId = create.CreatedIds[^1];
        string before = ProjectSerializer.Serialize(project);

        EditException error = Assert.Throws<EditException>(() => stack.Execute(new RemoveSequenceCommand(nestedId)));

        Assert.Contains("compuesto", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Equal(1, stack.Count);
        Assert.Empty(ModelValidator.Validate(project));
    }

    [Fact]
    public void Quitar_una_secuencia_que_nadie_usa_se_deshace_en_su_sitio()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(new AddSequenceCommand(new Sequence { Id = "otra", Name = "Otra" }, index: 0));
        string before = ProjectSerializer.Serialize(project);

        stack.Execute(new RemoveSequenceCommand("otra"));
        Assert.Null(project.FindSequence("otra"));
        stack.Undo();

        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.Equal("otra", project.Sequences[0].Id);
    }

    /// <summary>
    /// <see cref="SequenceSnapshot.Matches"/> se decía igualdad profunda, pero no miraba la alineación, la curva ni la
    /// Bézier de las transiciones, ni el color y la nota de marcadores y regiones: una prueba que comprobara un deshacer
    /// con <c>Matches</c> daba por buena una restauración que perdiera esos datos.
    /// </summary>
    [Fact]
    public void Matches_ve_la_alineacion_la_curva_y_la_bezier_de_las_transiciones_y_el_color_y_la_nota_de_las_marcas()
    {
        var mutations = new List<Action<Sequence>>
        {
            s => s.VideoTracks[0].Transitions[0].Alignment = TransitionAlignment.Before,
            s => s.VideoTracks[0].Transitions[0].Curve = Interpolation.EaseIn,
            s => s.VideoTracks[0].Transitions[0].Ease = new BezierEase(0.1, 0.2, 0.3, 0.4),
            s => s.Markers[0].Color = "#112233",
            s => s.Markers[0].Note = "nota",
            s => s.Regions[0].Color = "#445566",
            s => s.Regions[0].Note = "nota",
        };

        foreach (Action<Sequence> mutate in mutations)
        {
            Project project = Create();
            Sequence sequence = Seq(project);
            sequence.VideoTracks[0].Transitions.Add(
                new TransitionInstance { Id = "x", Type = BuiltInTransitions.Crossfade, From = "clip-1", To = "clip-2", Duration = Time.FromSeconds(0.5) });
            sequence.Markers.Add(new Marker { Id = "m", Time = S(1), Name = "M" });
            sequence.Regions.Add(new Region { Id = "r", Start = S(1), End = S(2), Name = "R" });
            var snapshot = SequenceSnapshot.Capture(sequence);
            Assert.True(snapshot.Matches(sequence));

            mutate(sequence);

            Assert.False(snapshot.Matches(sequence));
        }
    }
}
