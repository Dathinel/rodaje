using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Core.Timing.Rational;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Alineación, curva del avance, obturador de la mezcla y forma de los fundidos de audio en el plan.</summary>
public sealed class TransitionTimingEngineTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    private static (TimelineBuilder Builder, FakeTimelineBackend Backend, TransitionInstance Transition) TwoClips(string type, double seconds)
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(10), S(2), "sale");
        b.AddVideo(0, "b", S(2), S(5), S(2), "entra");
        return (b, backend, TimelineEffectsTests.AddTransition(b, type, "sale", "entra", S(seconds)));
    }

    private static (string Clip, string? Incoming, float Progress) At(TimelineSession session, Time time)
    {
        session.Seek(time);
        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        return (layer.ClipId, layer.Transition?.Incoming.ClipId, layer.Transition?.Progress ?? -1);
    }

    [Fact]
    public void Aligned_before_the_cut_it_ends_where_the_outgoing_clip_ends()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, TransitionInstance t) = TwoClips(BuiltInTransitions.Crossfade, 1);
        t.Alignment = TransitionAlignment.Before;
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        Assert.Equal(("sale", (string?)null, -1f), At(session, S(0.9)));
        Assert.Equal(("sale", "entra", 0f), At(session, S(1)));
        Assert.Equal(("sale", "entra", 0.5f), At(session, S(1.5)));
        Assert.Equal(("entra", (string?)null, -1f), At(session, S(2)));
    }

    [Fact]
    public void Aligned_after_the_cut_it_starts_where_the_incoming_clip_starts()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, TransitionInstance t) = TwoClips(BuiltInTransitions.Crossfade, 1);
        t.Alignment = TransitionAlignment.After;
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        Assert.Equal(("sale", (string?)null, -1f), At(session, S(1.9)));
        Assert.Equal(("sale", "entra", 0f), At(session, S(2)));
        Assert.Equal(("sale", "entra", 0.5f), At(session, S(2.5)));
        Assert.Equal(("entra", (string?)null, -1f), At(session, S(3)));
    }

    [Fact]
    public void The_curve_shapes_the_progress()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, TransitionInstance t) = TwoClips(BuiltInTransitions.Wipe, 1);
        t.Curve = Interpolation.EaseIn;
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        Assert.Equal(0.125f, At(session, S(2)).Progress);
        Assert.Equal((float)(0.8 * 0.8 * 0.8), At(session, S(2.3)).Progress, 6);
    }

    [Fact]
    public void Motion_blur_opens_the_shutter_around_the_instant_and_closes_it_at_the_ends()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, _) = TwoClips(BuiltInTransitions.Slide, 1);
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        (float From, float To)? ShutterAt(Time time)
        {
            session.Seek(time);
            using CompositionFrame frame = session.Tick();
            return frame.Layers[0].Transition!.Value.Shutter;
        }

        // 0,5 del cuadro (180°) de 1/30 s en una transición de 1 s: ±1/120 del avance.
        (float From, float To) middle = ShutterAt(S(2))!.Value;
        Assert.Equal(0.5 - (1.0 / 120), middle.From, 6);
        Assert.Equal(0.5 + (1.0 / 120), middle.To, 6);
        Assert.Null(ShutterAt(S(1.5)));
    }

    [Fact]
    public void A_luma_image_travels_as_the_mix_resource()
    {
        (TimelineBuilder b, FakeTimelineBackend backend, TransitionInstance t) = TwoClips(BuiltInTransitions.Luma, 1);
        t.Options[BuiltInTransitions.LumaOptions.Image] = "file:degradados/reloj.png";
        t.Options[BuiltInTransitions.LumaOptions.Gradient] = "radial";
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(2));
        using CompositionFrame frame = session.Tick();
        LayerTransition mix = frame.Layers[0].Transition!.Value;
        Assert.Equal(new ResourceKey("file:degradados/reloj.png"), mix.Resource);
        Assert.Equal(1f, mix.Values[6]);
    }

    [Theory]
    [InlineData(BuiltInTransitions.AudioPower)]
    [InlineData(BuiltInTransitions.AudioGain)]
    [InlineData(BuiltInTransitions.AudioExponential)]
    [InlineData(BuiltInTransitions.Crossfade)]
    public void Each_audio_fade_keeps_its_sum(string type)
    {
        for (int i = 0; i <= 1000; i++)
        {
            long sample = i;
            var fadeIn = new Fade(0, 1000, false) { Shape = Fade.ShapeOf(type) };
            var fadeOut = new Fade(0, 1000, true) { Shape = Fade.ShapeOf(type) };
            double a = fadeOut.Factor(sample);
            double b = fadeIn.Factor(sample);
            double t = i / 1000.0;
            switch (Fade.ShapeOf(type))
            {
                case FadeShape.Power:
                    Assert.Equal(1, (a * a) + (b * b), 12);
                    break;
                case FadeShape.Gain:
                    Assert.Equal(1, a + b, 12);
                    break;
                default:
                    Assert.Equal((Math.Pow(1000, t) - 1) / 999, b, 12);
                    Assert.Equal((Math.Pow(1000, 1 - t) - 1) / 999, a, 12);
                    break;
            }
        }
    }

    [Fact]
    public void Exponential_fade_is_linear_in_decibels_away_from_silence()
    {
        // Entre −40 y 0 dB, la pendiente en dB es casi constante: 60 dB en toda la transición.
        double db1 = 20 * Math.Log10(Fade.Exponential(0.8));
        double db2 = 20 * Math.Log10(Fade.Exponential(0.9));
        Assert.Equal(6, db2 - db1, 0.1);
        Assert.Equal((0.0, 1.0), (Fade.Exponential(0), Fade.Exponential(1)));
    }

    /// <summary>En la mezcla real: dos señales continuas cruzadas con cada forma y con una curva suave.</summary>
    [Theory]
    [InlineData(BuiltInTransitions.AudioPower, false)]
    [InlineData(BuiltInTransitions.AudioGain, false)]
    [InlineData(BuiltInTransitions.AudioExponential, false)]
    [InlineData(BuiltInTransitions.AudioGain, true)]
    public void The_mixer_crosses_with_the_shape_and_curve_of_the_transition(string type, bool eased)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Fps30, 30_000);
        b.AddMedia("dc2", Fps30, 3000);
        b.AddAudio(0, "dc", Time.Zero, S(5), S(2), "sale");
        b.AddAudio(0, "dc2", S(2), S(5), S(2), "entra");
        var transition = new TransitionInstance
        {
            Id = "t", Type = type, From = "sale", To = "entra", Duration = S(1), Curve = eased ? Interpolation.EaseInOut : null,
        };
        b.Sequence.AudioTracks[0].Transitions.Add(transition);

        float[] mix = AudioGainTests.MixOf(b, 0, 4 * Rate, path => path.Contains("dc2") ? 0.25f : 0.5f);

        double worst = 0;
        for (long s = (3 * Rate / 2) + 7; s < 5 * Rate / 2; s += 97)
        {
            double t = (s - (1.5 * Rate)) / Rate;
            double e = transition.Eased(t);
            var shape = Fade.ShapeOf(type);
            double In(double x) => shape switch
            {
                FadeShape.Gain => x,
                FadeShape.Exponential => Fade.Exponential(x),
                _ => Math.Sin(x * Math.PI / 2),
            };
            double expected = (0.5 * In(1 - e)) + (0.25 * In(e));
            worst = Math.Max(worst, Math.Abs(expected - mix[s * 2]));
        }

        output.WriteLine($"MEDIDA error_cruce_{type}{(eased ? "_suave" : "")} = {worst:E2}");
        Assert.True(worst <= 1e-4, $"El cruce se aparta {worst:E2} de su forma.");
    }
}
