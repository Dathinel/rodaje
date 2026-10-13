using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Model;

/// <summary>
/// Tarea 92: todos los parámetros de todos los efectos y transiciones del registro (no solo los de escala, que cubre
/// <see cref="ScaleParameterExtremesTests"/>) con valores extremos. Cada uno tiene límites finitos (la GPU trabaja en
/// float), el motor recibe siempre un número del rango y el validador acepta los extremos del rango y rechaza lo demás.
/// </summary>
public sealed class ParameterExtremesTests
{
    private const string VideoClip = "clip-1";
    private const string AudioClip = "clip-4";

    public static readonly double[] Extremes =
    [
        0, -0.0, -1, 1, 1e-12, -1e-12, double.Epsilon, 1e6, -1e6, 1e7, -1e7, 1e300, -1e300, double.MaxValue, double.MinValue,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    ];

    /// <summary>Los fundidos del audio no tienen tope en el modelo: el motor los corta al clip (y a 24 h).</summary>
    private static bool Unbounded(ParameterDefinition p) =>
        p.Name is BuiltInEffects.AudioParameters.FadeIn or BuiltInEffects.AudioParameters.FadeOut;

    public static TheoryData<string> Effects()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition effect in EffectRegistry.All)
        {
            if (!effect.IsTransition && !effect.Hidden && effect.Parameters.Length > 0)
            {
                data.Add(effect.Type);
            }
        }

        return data;
    }

    [Fact]
    public void CadaParametroTieneLimitesFinitos_SalvoLosFundidos()
    {
        foreach (EffectDefinition effect in EffectRegistry.All)
        {
            foreach (ParameterDefinition p in effect.Parameters)
            {
                Assert.True(double.IsFinite(p.Minimum), $"{effect.Type}/{p.Name}: mínimo {p.Minimum}");
                Assert.True(Unbounded(p) || double.IsFinite(p.Maximum), $"{effect.Type}/{p.Name}: máximo {p.Maximum}");
                Assert.True(p.Minimum <= p.DefaultValue && p.DefaultValue <= p.Maximum, $"{effect.Type}/{p.Name}: el valor por defecto está fuera");
                Assert.True(p.Minimum < p.Maximum, $"{effect.Type}/{p.Name}: rango vacío");

                // Lo que el registro admite cabe en un float de la GPU, también multiplicado por un cuadro de 8K.
                Assert.True(Unbounded(p) || Math.Max(Math.Abs(p.Minimum), Math.Abs(p.Maximum)) * 8192 < float.MaxValue / 1e20,
                    $"{effect.Type}/{p.Name}: {p.Minimum} a {p.Maximum}");
            }
        }
    }

    [Fact]
    public void ElMotorRecibeSiempreUnNumeroDelRango()
    {
        foreach (EffectDefinition effect in EffectRegistry.All)
        {
            foreach (ParameterDefinition p in effect.Parameters.Where(p => !Unbounded(p)))
            {
                foreach (double value in Extremes)
                {
                    double clamped = p.Clamp(value);
                    Assert.True(double.IsFinite(clamped), $"{effect.Type}/{p.Name} con {value}: {clamped}");
                    Assert.InRange(clamped, p.Minimum, p.Maximum);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void ElValidadorAceptaLosExtremosDelRango_YRechazaLoDemas(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            double step = Math.Max(1e-6, Math.Abs(p.Maximum - p.Minimum) * 1e-6);
            double[] values =
            [
                p.Minimum, p.Maximum, p.Minimum - step, p.Maximum + step, 0, p.DefaultValue, double.NaN, double.PositiveInfinity,
                double.NegativeInfinity, double.MaxValue, double.MinValue,
            ];
            foreach (double value in values)
            {
                bool possible = double.IsFinite(value) && value >= p.Minimum && value <= p.Maximum;
                Project project = WithEffect(type, p.Name, new Parameter { Value = value });
                Assert.True(possible == !HasRangeError(project, p.Name), $"{type}/{p.Name} = {value}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Effects))]
    public void KeyframesEnLosExtremosConCualquierCurva_SeEvaluanSinNaN(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        BezierEase[] eases =
        [
            new(0, 0, 0, 0), new(1, 1, 1, 1), new(0, BezierEase.MaxY, 1, BezierEase.MinY), new(1, BezierEase.MinY, 0, BezierEase.MaxY),
            new BezierEase(double.NaN, double.NaN, double.PositiveInfinity, double.NegativeInfinity).Clamped(),
        ];
        foreach (ParameterDefinition p in definition.Parameters.Where(p => p.Animatable && !Unbounded(p)))
        {
            foreach (Interpolation interpolation in Enum.GetValues<Interpolation>())
            {
                foreach (BezierEase? ease in interpolation == Interpolation.Bezier ? eases : new BezierEase?[] { null })
                {
                    var parameter = new Parameter
                    {
                        Value = p.DefaultValue,
                        Keyframes =
                        [
                            new Keyframe(S(0), p.Minimum, interpolation) { Ease = ease },
                            new Keyframe(S(1), p.Maximum, interpolation) { Ease = ease },
                            new Keyframe(S(1) + Time.FromTicks(1), p.Minimum, interpolation) { Ease = ease },
                            new Keyframe(S(2), p.Maximum, interpolation) { Ease = ease },
                        ],
                    };
                    Project project = WithEffect(type, p.Name, parameter);
                    Assert.False(HasRangeError(project, p.Name), $"{type}/{p.Name} ({interpolation}): {string.Join(" | ", ModelValidator.Validate(project))}");
                    for (int i = -5; i <= 25; i++)
                    {
                        double value = parameter.Evaluate(Time.FromSeconds(i / 10.0));
                        Assert.True(double.IsFinite(value), $"{type}/{p.Name} ({interpolation}, {ease}) en {i / 10.0}: {value}");
                    }

                    double tick = parameter.Evaluate(S(1) + Time.FromTicks(1));
                    Assert.True(double.IsFinite(tick), $"{type}/{p.Name} ({interpolation}) en el cuadro repetido: {tick}");
                }
            }
        }
    }

    private static Project WithEffect(string type, string name, Parameter parameter)
    {
        Project project = Create();
        var effect = new EffectInstance { Id = "fx-x", Type = type };
        effect.Parameters[name] = parameter;
        string clip = (EffectRegistry.Find(type)!.Target & EffectTarget.Video) != 0 ? VideoClip : AudioClip;
        Seq(project).GetClip(clip).Clip.Effects.Add(effect);
        return project;
    }

    private static bool HasRangeError(Project project, string name) =>
        ModelValidator.Validate(project).Any(e => e.Contains("fx-x", StringComparison.Ordinal) && e.Contains($"«{name}»", StringComparison.Ordinal));
}
