using System.Text.RegularExpressions;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Model;

/// <summary>
/// Tarea 91 («la app se cierra al elegir escala»): cada parámetro de escala o tamaño del registro con valores imposibles
/// (0, negativos, NaN, infinitos, enormes, diminutos). El modelo rechaza lo imposible al validar, la edición lo rechaza
/// con <see cref="EditException"/> (la única que la interfaz atrapa) o lo lleva al rango, y el motor recibe siempre un
/// valor del rango (<see cref="ParameterDefinition.Clamp"/>).
/// </summary>
public partial class ScaleParameterExtremesTests
{
    private const string VideoClip = "clip-1";
    private const string AudioClip = "clip-4";

    public static readonly double[] Extremes =
    [
        0, -0.0, -1, -1e-12, 1e-12, double.Epsilon, -double.Epsilon, 1e300, -1e300, double.MaxValue, double.MinValue,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 400, 10_000,
    ];

    [GeneratedRegex("scale|size|width|height|zoom", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScaleLike();

    /// <summary>Los parámetros de escala o tamaño de todos los efectos y transiciones del registro.</summary>
    public static IEnumerable<(EffectDefinition Effect, ParameterDefinition Parameter)> ScaleParameters() =>
        EffectRegistry.All.SelectMany(e => e.Parameters.Where(p => ScaleLike().IsMatch(p.Name)).Select(p => (e, p)));

    public static TheoryData<string, string> EffectScaleParameters()
    {
        var data = new TheoryData<string, string>();
        foreach ((EffectDefinition effect, ParameterDefinition parameter) in ScaleParameters())
        {
            if (!effect.IsTransition && !effect.Hidden)
            {
                data.Add(effect.Type, parameter.Name);
            }
        }

        return data;
    }

    [Fact]
    public void HayParametrosDeEscala_YLosDeLaTransformacionEstan()
    {
        var names = ScaleParameters().Select(x => (x.Effect.Type, x.Parameter.Name)).ToHashSet();
        Assert.Contains((BuiltInEffects.Transform, BuiltInEffects.TransformParameters.Scale), names);
        Assert.Contains((BuiltInEffects.Transform, BuiltInEffects.TransformParameters.ScaleX), names);
        Assert.Contains((BuiltInEffects.Transform, BuiltInEffects.TransformParameters.ScaleY), names);
        Assert.Contains((BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Width), names);
        Assert.Contains((BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Height), names);
        Assert.True(names.Count >= 10, $"Solo {names.Count} parámetros de escala o tamaño.");
    }

    [Fact]
    public void LaEscalaNoBajaDeCero_NiLosEjes()
    {
        // 0 sí vale (una animación que crece desde nada); lo negativo no: el volteo es una opción aparte.
        foreach (string name in new[] { BuiltInEffects.TransformParameters.Scale, BuiltInEffects.TransformParameters.ScaleX, BuiltInEffects.TransformParameters.ScaleY })
        {
            ParameterDefinition p = EffectRegistry.Find(BuiltInEffects.Transform)!.FindParameter(name)!;
            Assert.Equal(0, p.Minimum);
            Assert.True(double.IsFinite(p.Maximum));
        }
    }

    [Fact]
    public void ElMotorRecibeSiempreUnValorDelRango()
    {
        foreach ((EffectDefinition effect, ParameterDefinition p) in ScaleParameters())
        {
            foreach (double value in Extremes)
            {
                double clamped = p.Clamp(value);
                Assert.True(double.IsFinite(clamped) || (double.IsInfinity(p.Minimum) || double.IsInfinity(p.Maximum)),
                    $"{effect.Type}/{p.Name} con {value}: {clamped}");
                Assert.False(double.IsNaN(clamped), $"{effect.Type}/{p.Name} con {value}");
                Assert.InRange(clamped, p.Minimum, p.Maximum);
            }
        }
    }

    [Theory]
    [MemberData(nameof(EffectScaleParameters))]
    public void ElValidadorRechazaLoImposible(string type, string name)
    {
        ParameterDefinition p = EffectRegistry.Find(type)!.FindParameter(name)!;
        foreach (double value in Extremes)
        {
            bool possible = double.IsFinite(value) && value >= p.Minimum && value <= p.Maximum;

            Project fixed1 = WithEffect(type, new Parameter { Value = value }, name);
            Assert.True(possible == !HasRangeError(fixed1, name), $"{type}/{name} = {value}: {string.Join(" | ", ModelValidator.Validate(fixed1))}");

            if (p.Animatable)
            {
                var animated = new Parameter
                {
                    Value = p.DefaultValue,
                    Keyframes = [new Keyframe(S(0), p.DefaultValue), new Keyframe(S(1), value, Interpolation.Elastic)],
                };
                Project keyed = WithEffect(type, animated, name);
                Assert.True(possible == !HasRangeError(keyed, name), $"{type}/{name} con keyframe {value}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(EffectScaleParameters))]
    public void EditarConValoresExtremos_RechazaOLlevaAlRango(string type, string name)
    {
        ParameterDefinition p = EffectRegistry.Find(type)!.FindParameter(name)!;
        foreach (double value in Extremes)
        {
            Project project = WithEffect(type, null, name);
            var stack = new UndoStack(project);
            string clipId = ClipFor(type);
            var commands = new List<ICommand> { Edits.SetParameter(SequenceId, clipId, "fx-x", name, value, S(1)) };
            if (p.Animatable)
            {
                commands.Add(Edits.SetKeyframe(SequenceId, clipId, "fx-x", name, S(1), value, Interpolation.Bounce));
            }

            foreach (ICommand command in commands)
            {
                try
                {
                    stack.Execute(command);
                }
                catch (EditException)
                {
                    Assert.False(double.IsFinite(value), $"{type}/{name}: {value} es un número y debería llevarse al rango");
                    continue;
                }

                Parameter written = Seq(project).GetClip(clipId).Clip.Effects.Single(e => e.Id == "fx-x").Parameters[name];
                Assert.InRange(written.Value, p.Minimum, p.Maximum);
                Assert.All(written.Keyframes, k => Assert.InRange(k.Value, p.Minimum, p.Maximum));
                Assert.DoesNotContain(ModelValidator.Validate(project), e => e.Contains("fx-x", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void EscalaCeroOConKeyframesElasticos_SeEvaluaSinNaN()
    {
        // Lo que la gráfica y el deslizador leen: un keyframe en 0 y una curva elástica o con rebote no dan NaN.
        foreach (Interpolation interpolation in Enum.GetValues<Interpolation>())
        {
            var parameter = new Parameter
            {
                Value = 1,
                Keyframes = [new Keyframe(S(0), 0, interpolation), new Keyframe(S(1), 100), new Keyframe(S(2), 0)],
            };
            for (int i = -10; i <= 30; i++)
            {
                double value = parameter.Evaluate(Time.FromSeconds(i / 10.0));
                Assert.True(double.IsFinite(value), $"{interpolation} en {i / 10.0}: {value}");
            }
        }
    }

    private static string ClipFor(string type) =>
        (EffectRegistry.Find(type)!.Target & EffectTarget.Video) != 0 ? VideoClip : AudioClip;

    private static Project WithEffect(string type, Parameter? parameter, string name)
    {
        Project project = Create();
        var effect = new EffectInstance { Id = "fx-x", Type = type };
        if (parameter is not null)
        {
            effect.Parameters[name] = parameter;
        }

        Seq(project).GetClip(ClipFor(type)).Clip.Effects.Add(effect);
        return project;
    }

    private static bool HasRangeError(Project project, string name) =>
        ModelValidator.Validate(project).Any(e => e.Contains("fx-x", StringComparison.Ordinal) && e.Contains($"«{name}»", StringComparison.Ordinal));
}
