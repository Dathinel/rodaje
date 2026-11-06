using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using static Rodaje.Engine.Tests.Support.Seconds;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>La opción <c>fadeShape</c> del efecto de audio: lineal y exponencial además de la potencia constante de siempre.</summary>
public sealed class AudioFadeShapeTests
{
    private const int Rate = 48_000;

    private static TimelineBuilder Builder()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Rational.Fps30, 30_000);
        return b;
    }

    private static EffectInstance Audio(string? shape, params (string Name, double Value)[] parameters)
    {
        EffectInstance effect = TimelineEffectsTests.Effect(BuiltInEffects.Audio, parameters);
        if (shape is not null)
        {
            effect.Options[BuiltInEffects.AudioOptions.FadeShape] = shape;
        }

        return effect;
    }

    private static double Worst(TimelineBuilder b, Func<long, double> expected)
    {
        float[] mix = AudioGainTests.MixOf(b, 0, 4 * Rate);
        double worst = 0;
        for (long s = 0; s < 4 * Rate; s++)
        {
            worst = Math.Max(worst, Math.Abs((mix[s * 2] / 0.5) - expected(s)));
        }

        return worst;
    }

    [Fact]
    public void Linear_fades_are_straight_in_amplitude()
    {
        var b = Builder();
        b.AddAudio(0, "dc", S(1), Time.Zero, S(3)).Effects.Add(Audio(BuiltInEffects.FadeShapes.Linear, (A.FadeIn, 0.5), (A.FadeOut, 1)));

        double worst = Worst(b, s => s < Rate ? 0
            : s < 1.5 * Rate ? (s - Rate) / (0.5 * Rate)
            : s < 3 * Rate ? 1
            : 1 - ((s - (3 * Rate)) / (double)Rate));

        Assert.True(worst <= 1e-4, $"El fundido lineal se aparta {worst:E2} de la recta.");
    }

    [Fact]
    public void Exponential_fades_are_linear_in_decibels()
    {
        var b = Builder();
        b.AddAudio(0, "dc", S(1), Time.Zero, S(3)).Effects.Add(Audio(BuiltInEffects.FadeShapes.Exponential, (A.FadeIn, 1)));

        static double Curve(double x) => x <= 0 ? 0 : x >= 1 ? 1 : (Math.Pow(1000, x) - 1) / 999;
        double worst = Worst(b, s => s < Rate ? 0 : s < 2 * Rate ? Curve((s - Rate) / (double)Rate) : 1);

        Assert.True(worst <= 1e-4, $"El fundido exponencial se aparta {worst:E2} de la curva.");
    }

    [Fact]
    public void Without_the_option_or_with_an_unknown_value_the_fade_keeps_constant_power()
    {
        foreach (string? shape in new[] { null, "", "otra" })
        {
            var b = Builder();
            b.AddAudio(0, "dc", S(1), Time.Zero, S(3)).Effects.Add(Audio(shape, (A.FadeIn, 1)));

            double worst = Worst(b, s => s < Rate ? 0 : s < 2 * Rate ? Math.Sin((s - Rate) / (double)Rate * Math.PI / 2) : 1);

            Assert.True(worst <= 1e-4, $"Con «{shape}» el fundido se aparta {worst:E2} de la potencia constante.");
        }
    }
}
