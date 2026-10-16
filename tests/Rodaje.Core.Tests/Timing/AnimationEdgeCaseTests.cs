using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Timing;

public sealed class AnimationEdgeCaseTests
{
    [Fact]
    public void Evaluate_keyframes_retorna_extremos_y_unica_clave()
    {
        var one = new Parameter { Value = -4, Keyframes = [new Keyframe(Time.FromSeconds(2), 7)] };
        Assert.Equal(7, one.Evaluate(Time.Zero));
        Assert.Equal(7, one.Evaluate(Time.FromSeconds(2)));
        Assert.Equal(7, one.Evaluate(Time.FromSeconds(9)));

        var many = new Parameter
        {
            Keyframes = [new Keyframe(Time.FromSeconds(2), 3), new Keyframe(Time.FromSeconds(4), 9)],
        };
        Assert.Equal(3, many.Evaluate(Time.Zero));
        Assert.Equal(3, many.Evaluate(Time.FromSeconds(2)));
        Assert.Equal(9, many.Evaluate(Time.FromSeconds(4)));
        Assert.Equal(9, many.Evaluate(Time.FromSeconds(7)));
    }

    [Fact]
    public void Animacion_deshace_rehace_y_se_guarda_y_reabre()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        string before = ProjectSerializer.Serialize(project);

        stack.Execute(Edits.SetKeyframe(SequenceId, "clip-2", "fx-1", "opacity", S(11), 0.5));
        string after = ProjectSerializer.Serialize(project);
        Assert.NotEqual(before, after);

        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectSerializer.Serialize(project));

        Project reopened = ProjectSerializer.Deserialize(after);
        Parameter parameter = reopened.GetSequence(SequenceId).GetClip("clip-2").Clip.Effects
            .Single(effect => effect.Id == "fx-1").Parameters["opacity"];
        Assert.Contains(parameter.Keyframes, key => key.Time == S(11) && key.Value == 0.5);
        Assert.Equal(after, ProjectSerializer.Serialize(reopened));
    }

    [Fact]
    public void Velocidad_rechaza_curvas_nulas_o_negativas_y_cero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(-1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetimeMath.Constant(new Rational(1, -1)));

        foreach (int rate in new[] { 0, -1 })
        {
            var curve = new SpeedCurve
            {
                Span = Time.FromSeconds(1),
                Points = [new(0, 100), new(SpeedCurve.AtScale, rate)],
            };
            Assert.NotNull(RetimeMath.CurveProblem(curve));
            Assert.Throws<ArgumentException>(() => RetimeMath.FromCurve(curve));
        }
    }
}
