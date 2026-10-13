using System.Numerics;
using Rodaje.Audio.Processing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Tarea 92: cada parámetro de cada efecto de video, transición y efecto de audio del registro con valores extremos
/// (mínimo, máximo, fuera del rango, 0, NaN, ±∞, enormes), fijo o con keyframes de todas las interpolaciones, en cuadros
/// repetidos o desordenados y con curvas Bézier degeneradas. Lo que el motor entrega (la transformación, la colocación de
/// la capa, los valores que van a la GPU, los ajustes del audio) es siempre un número dentro del rango.
/// </summary>
[Collection(ExtremeValuesGroup.Name)]
public sealed class ExtremeParameterPlanTests
{
    public static readonly double[] Extremes =
    [
        0, -0.0, -1, 1, 1e-12, -1e-12, double.Epsilon, 1e6, -1e6, 1e30, -1e30, 1e300, -1e300, double.MaxValue, double.MinValue,
        double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    ];

    /// <summary>Curvas Bézier imposibles o al límite: puntos en las esquinas, fuera del cuadro, NaN e infinitos.</summary>
    public static readonly BezierEase[] Eases =
    [
        new(0, 0, 0, 0), new(1, 1, 1, 1), new(0, 1, 1, 0), new(1, 0, 0, 1), new(0, BezierEase.MaxY, 1, BezierEase.MinY),
        new(0.5, 1e300, 0.5, -1e300), new(-5, 0, 7, 1), new(double.NaN, double.NaN, double.NaN, double.NaN),
        new(double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity),
        new(1e-300, 0, 1 - 1e-16, 1),
    ];

    private static readonly long[] Instants =
        [-Time.TicksPerSecond, 0, 1, Time.TicksPerSecond / 3, Time.TicksPerSecond / 2, Time.TicksPerSecond - 1, Time.TicksPerSecond,
            3 * Time.TicksPerSecond / 2, 2 * Time.TicksPerSecond, long.MaxValue / 4];

    /// <summary>Los efectos de video que el motor aplica (los de la GPU, sin transiciones ni mezcla).</summary>
    public static TheoryData<string> VideoEffects()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition d in EffectRegistry.All)
        {
            if (!d.IsTransition && !d.Hidden && (d.Target & EffectTarget.Video) != 0 && d.Parameters.Length > 0
                && d.Stage is EffectStage.Transform or EffectStage.PerPixel or EffectStage.Spatial)
            {
                data.Add(d.Type);
            }
        }

        return data;
    }

    public static TheoryData<string> Transitions()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition d in EffectRegistry.Transitions)
        {
            data.Add(d.Type);
        }

        return data;
    }

    public static TheoryData<string> AudioEffects()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition d in EffectRegistry.All)
        {
            if (!d.IsTransition && !d.Hidden && d.Stage == EffectStage.Audio && d.Type != BuiltInEffects.Audio)
            {
                data.Add(d.Type);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VideoEffects))]
    public void CadaParametroFijoConValoresExtremos_DaNumerosDelRango(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (double value in Extremes)
            {
                var effect = new EffectInstance { Id = "fx", Type = type };
                effect.Parameters[p.Name] = new Parameter { Value = value };
                AssertPlan(definition, effect, $"{type}/{p.Name} = {value}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(VideoEffects))]
    public void CadaParametroConKeyframesExtremos_DaNumerosDelRango(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters.Where(p => p.Animatable))
        {
            foreach (double value in Extremes)
            {
                foreach (Interpolation interpolation in Enum.GetValues<Interpolation>())
                {
                    foreach (BezierEase? ease in interpolation == Interpolation.Bezier ? Eases : new BezierEase?[] { null })
                    {
                        double other = double.IsFinite(value) && value > 0 ? -value : Math.Abs(value);
                        var effect = new EffectInstance { Id = "fx", Type = type };
                        effect.Parameters[p.Name] = new Parameter
                        {
                            Keyframes =
                            [
                                new Keyframe(Time.Zero, other, interpolation) { Ease = ease },
                                new Keyframe(Time.FromSeconds(1), value, interpolation) { Ease = ease },
                                new Keyframe(Time.FromSeconds(2), other, interpolation) { Ease = ease },
                            ],
                        };
                        AssertPlan(definition, effect, $"{type}/{p.Name}: {other} → {value} ({interpolation}, {ease})");
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(VideoEffects))]
    public void KeyframesEnElMismoCuadroODesordenados_NoDanNaN(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters.Where(p => p.Animatable))
        {
            double low = double.IsFinite(p.Minimum) ? p.Minimum : -1e6;
            double high = double.IsFinite(p.Maximum) ? p.Maximum : 1e6;
            List<Keyframe>[] sets =
            [
                [new(Time.FromSeconds(1), low), new(Time.FromSeconds(1), high), new(Time.FromSeconds(1), low)],
                [new(Time.Zero, low, Interpolation.Elastic), new(Time.FromTicks(1), high, Interpolation.Bounce), new(Time.FromTicks(2), low)],
                [new(Time.FromSeconds(2), high, Interpolation.Elastic), new(Time.Zero, low), new(Time.FromSeconds(1), high)],
                [new(Time.FromSeconds(1), double.NaN), new(Time.FromSeconds(1), double.PositiveInfinity, Interpolation.Bezier)],
            ];
            foreach (List<Keyframe> keys in sets)
            {
                var effect = new EffectInstance { Id = "fx", Type = type };
                effect.Parameters[p.Name] = new Parameter { Keyframes = keys };
                AssertPlan(definition, effect, $"{type}/{p.Name} con keyframes repetidos o desordenados");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void CadaParametroDeUnaTransicionConValoresExtremos_DaNumerosDelRango(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (double value in Extremes)
            {
                var transition = new TransitionInstance { Id = "t", Type = type, From = "a", To = "b", Duration = Time.FromSeconds(1) };
                transition.Parameters[p.Name] = value;
                TransitionPlan plan = TransitionPlan.From(transition, Time.Zero, Time.FromSeconds(1), 1920, 1080, new(30, 1), null);
                string what = $"{type}/{p.Name} = {value}";
                AssertValues(EffectRegistry.Find(plan.Code)!, plan.Values, what);
                foreach (long at in Instants)
                {
                    Time t = Time.FromTicks(Math.Min(at, 2 * Time.TicksPerSecond));
                    float progress = plan.Progress(t);
                    Assert.True(float.IsFinite(progress), $"{what}: avance {progress}");
                    LayerTransition layer = plan.Layer(t, progress, null!);
                    Assert.True(float.IsFinite(layer.Progress), what);
                }
            }
        }
    }

    [Fact]
    public void LaCurvaDeUnaTransicionConBezierDegenerada_DaUnAvanceDeCeroAUno()
    {
        foreach (BezierEase ease in Eases)
        {
            var transition = new TransitionInstance
            {
                Id = "t", Type = BuiltInTransitions.Crossfade, From = "a", To = "b", Duration = Time.FromSeconds(1),
                Curve = Interpolation.Bezier, Ease = ease,
            };
            TransitionPlan plan = TransitionPlan.From(transition, Time.Zero, Time.FromSeconds(1), 1920, 1080, new(30, 1), null);
            for (int i = -2; i <= 32; i++)
            {
                float progress = plan.Progress(Time.FromSeconds(i / 30.0));
                Assert.True(float.IsFinite(progress), $"{ease} en {i}: {progress}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AudioEffects))]
    public void CadaParametroDeAudioConValoresExtremos_SuenaSinNaN(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (double value in Extremes)
            {
                var effect = new EffectInstance { Id = "fx", Type = type };
                foreach (ParameterDefinition other in definition.Parameters)
                {
                    // Todo encendido (bandas, limitador, sincronía) para que cada parámetro cuente.
                    effect.Parameters[other.Name] = new Parameter { Value = other.Maximum <= 3 && other.Minimum >= 0 ? other.Maximum : other.DefaultValue };
                }

                effect.Parameters[p.Name] = new Parameter { Value = value, Keyframes = p.Animatable ? [new(Time.Zero, value)] : [] };
                string what = $"{type}/{p.Name} = {value}";
                if (TimelinePlan.AudioEffects([effect]) is not { } settings)
                {
                    continue;
                }

                var chain = new AudioEffectChain(settings);
                if (TimelinePlan.Automation([effect], Time.Zero) is { } automation)
                {
                    chain.Automate(automation.Threshold.Evaluate(0), automation.Ratio.Evaluate(0), automation.Mix.Evaluate(0), 0);
                }

                var random = new Random(92);
                for (int block = 0; block < 4; block++)
                {
                    var io = new float[2048];
                    for (int i = 0; i < io.Length; i++)
                    {
                        io[i] = 0.5f * ((float)random.NextDouble() * 2 - 1);
                    }

                    chain.Process(io);
                    int bad = Array.FindIndex(io, s => !float.IsFinite(s));
                    Assert.True(bad < 0, $"{what}: muestra {bad} del bloque {block}");
                }
            }
        }
    }

    [Fact]
    public void ElFaderConValoresExtremos_DaGananciasFinitas()
    {
        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Audio)!;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (double value in Extremes)
            {
                var effect = new EffectInstance { Id = "fx", Type = BuiltInEffects.Audio };
                effect.Parameters[p.Name] = new Parameter { Value = value, Keyframes = p.Animatable ? [new(Time.Zero, value), new(Time.FromSeconds(1), -value)] : [] };
                if (TimelinePlan.Fader([effect], Time.Zero) is not { } fader)
                {
                    continue;
                }

                foreach (long sample in new long[] { 0, 1, 24_000, 48_000, 96_000 })
                {
                    double db = fader.GainDb(sample), pan = fader.PanAt(sample), balance = fader.BalanceAt(sample);
                    Assert.True(double.IsFinite(db) && double.IsFinite(pan) && double.IsFinite(balance),
                        $"{p.Name} = {value} en {sample}: {db} dB, paneo {pan}, balance {balance}");
                    Assert.InRange(db, p.Name == BuiltInEffects.AudioParameters.GainDb ? EffectRegistry.SilenceDb : 0, 24);
                    Assert.InRange(pan, -1, 1);
                    Assert.InRange(balance, -1, 1);
                }
            }
        }
    }

    private static void AssertPlan(EffectDefinition definition, EffectInstance effect, string what)
    {
        EffectPlan? plan = EffectPlan.From([effect]);
        if (plan is null)
        {
            return;
        }

        var effects = new LayerEffect[plan.EffectCount];
        foreach (long at in Instants)
        {
            Time time = Time.FromTicks(at);
            LayerTransform transform = EffectPlan.Transform(plan, time);
            AssertTransform(transform, $"{what} en {at}");
            plan.Evaluate(time, effects, null);
            foreach (LayerEffect e in effects)
            {
                AssertValues(definition, e.Values, $"{what} en {at}");
            }
        }

        Assert.True(double.IsFinite(EffectPlan.MaxScale(plan)), $"{what}: escala máxima {EffectPlan.MaxScale(plan)}");
    }

    private static void AssertValues(EffectDefinition definition, EffectValues values, string what)
    {
        foreach (ParameterDefinition p in definition.Parameters)
        {
            float value = values[p.Slot];
            Assert.True(float.IsFinite(value), $"{what}: «{p.Name}» llega a la GPU como {value}");
            Assert.True(value >= (float)p.Minimum && value <= (float)p.Maximum, $"{what}: «{p.Name}» = {value} fuera de {p.Minimum} a {p.Maximum}");
        }
    }

    private static void AssertTransform(LayerTransform t, string what)
    {
        Assert.True(double.IsFinite(t.X) && double.IsFinite(t.Y) && double.IsFinite(t.RotationDegrees), $"{what}: {t}");
        Assert.True(double.IsFinite(t.Scale) && t.Scale >= 0 && t.Opacity is >= 0 and <= 1, $"{what}: {t}");
        if (t.Shape is { } s)
        {
            Assert.True(double.IsFinite(s.AnchorX) && double.IsFinite(s.AnchorY) && double.IsFinite(s.ScaleX) && double.IsFinite(s.ScaleY)
                && double.IsFinite(s.SkewX) && double.IsFinite(s.SkewY), $"{what}: {s}");
        }

        if (t.Crop is { } c)
        {
            Assert.True(double.IsFinite(c.Left) && double.IsFinite(c.Top) && double.IsFinite(c.Right) && double.IsFinite(c.Bottom), $"{what}: {c}");
        }

        if (t.Pin is { } pin)
        {
            Assert.True(double.IsFinite(pin.TopLeftX) && double.IsFinite(pin.TopLeftY) && double.IsFinite(pin.TopRightX) && double.IsFinite(pin.TopRightY)
                && double.IsFinite(pin.BottomRightX) && double.IsFinite(pin.BottomRightY) && double.IsFinite(pin.BottomLeftX)
                && double.IsFinite(pin.BottomLeftY), $"{what}: {pin}");
        }

        // Lo que se dibuja: o no se dibuja, o la colocación es finita (la GPU trabaja en float).
        foreach ((int width, int height) in new[] { (1920, 1080), (1, 1), (8192, 64) })
        {
            if (LayerLayout.Place(1920, 1080, width, height, t) is { } placement)
            {
                Matrix3x2 m = placement.LocalToSequence;
                Assert.True(float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22)
                    && float.IsFinite(m.M31) && float.IsFinite(m.M32), $"{what}: colocación {m}");
                foreach (Vector2 corner in new[] { placement.TopLeft, placement.TopRight, placement.BottomLeft, placement.BottomRight })
                {
                    Assert.True(float.IsFinite(corner.X) && float.IsFinite(corner.Y), $"{what}: esquina {corner}");
                }
            }
        }
    }
}
