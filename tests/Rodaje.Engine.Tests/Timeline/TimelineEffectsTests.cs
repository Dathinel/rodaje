using System.Diagnostics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelineEffectsTests(ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);

    [Fact]
    public void Image_effects_reach_the_layer_in_stack_order_with_their_values_and_the_blend_mode()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", S(10), S(2), S(4));
        EffectInstance color = Effect(BuiltInEffects.Color);
        color.Parameters[C.Exposure] = new Parameter { Keyframes = [new Keyframe(S(2), 0), new Keyframe(S(4), 2)] };
        color.Parameters[C.Contrast] = new Parameter { Value = 7 };
        clip.Effects.Add(color);
        clip.Effects.Add(Effect(BuiltInEffects.Vignette));
        clip.Effects.Add(new EffectInstance { Type = "paquete.desconocido" });
        EffectInstance blur = Effect(BuiltInEffects.Blur);
        blur.Enabled = false;
        clip.Effects.Add(blur);
        clip.Effects.Add(Effect(BuiltInEffects.Sharpen, (BuiltInEffects.SharpenParameters.Amount, 2)));
        EffectInstance blend = Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = "screen";
        clip.Effects.Add(blend);
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(11));
        using CompositionFrame frame = session.Tick();

        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.Equal(new[] { 2, 3, 5 }, layer.Effects.Select(e => e.Code));
        EffectValues values = layer.Effects[0].Values;
        Assert.Equal(1f, values[0], 5);
        Assert.Equal(1f, values[1]);
        Assert.Equal(1f, values[7]);
        Assert.Null(layer.Effects[0].Resource);
        Assert.Equal(0.5f, layer.Effects[1].Values[0]);
        Assert.Equal(2f, layer.Effects[2].Values[0]);
        Assert.Equal(BlendMode.Screen, layer.Blend);
        Assert.Equal(LayerTransform.Identity, layer.Transform);
        Assert.False(layer.IsPlain);
    }

    [Fact]
    public void A_clip_without_image_effects_keeps_the_fast_path()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        clip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5)));
        clip.Effects.Add(Effect(BuiltInEffects.Audio, (BuiltInEffects.AudioParameters.GainDb, -6)));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        using CompositionFrame frame = session.Tick();

        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.True(layer.IsPlain);
        Assert.Equal(0.5, layer.Transform.Scale);
    }

    [Fact]
    public void A_lut_travels_as_a_resource_only_once_it_is_loaded()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        EffectInstance color = Effect(BuiltInEffects.Color);
        color.Options[BuiltInEffects.ColorOptions.Lut] = "looks@1.0.0/calido";
        clip.Effects.Add(color);
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Content = content });

        using (CompositionFrame loading = session.Tick())
        {
            Assert.Null(loading.Layers[0].Effects[0].Resource);
        }

        content.ReadyLuts.Add("looks@1.0.0/calido");
        using CompositionFrame ready = session.Tick();

        Assert.Equal(new ResourceKey("looks@1.0.0/calido"), ready.Layers[0].Effects[0].Resource);
        Assert.Equal(2, content.LutRequests);
    }

    [Fact]
    public void A_crossfade_shows_the_outgoing_clip_with_its_leftover_material_and_the_incoming_one_brought_forward()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(10), S(2), "sale");
        b.AddVideo(0, "b", S(2), S(5), S(2), "entra");
        AddTransition(b, BuiltInTransitions.Crossfade, "sale", "entra", S(1));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        (string Clip, long Index, string? Incoming, long IncomingIndex, float Progress) At(Time time)
        {
            session.Seek(time);
            using CompositionFrame frame = session.Tick();
            LayerFrame layer = Assert.Single(frame.Layers);
            return (layer.ClipId, layer.Frame.Index, layer.Transition?.Incoming.ClipId, layer.Transition?.Incoming.Frame.Index ?? -1,
                layer.Transition?.Progress ?? -1);
        }

        Assert.Equal(("sale", 344L, null, -1L, -1f), At(F(44, Fps30)));
        Assert.Equal(("sale", 345L, "entra", 135L, 0f), At(S(1.5)));
        Assert.Equal(("sale", 354L, "entra", 144L, 0.3f), At(S(1.8)));
        Assert.Equal(("sale", 366L, "entra", 156L, 0.7f), At(S(2.2)));
        Assert.Equal(("entra", 165L, null, -1L, -1f), At(S(2.5)));
        session.Seek(S(2));
        using CompositionFrame frame = session.Tick();
        LayerTransition transition = frame.Layers[0].Transition!.Value;
        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, transition.Code);
        Assert.Equal(0.5f, transition.Progress);
    }

    [Fact]
    public void Without_leftover_material_each_side_freezes_its_edge_frame()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 60, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "sale");
        b.AddVideo(0, "b", S(2), Time.Zero, S(2), "entra");
        AddTransition(b, BuiltInTransitions.Crossfade, "sale", "entra", S(1));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(1.6));
        using (CompositionFrame before = session.Tick())
        {
            Assert.Equal((48L, 0L), (before.Layers[0].Frame.Index, before.Layers[0].Transition!.Value.Incoming.Frame.Index));
        }

        session.Seek(S(2.4));
        using CompositionFrame after = session.Tick();
        Assert.Equal((59L, 12L), (after.Layers[0].Frame.Index, after.Layers[0].Transition!.Value.Incoming.Frame.Index));
    }

    [Fact]
    public void Transition_parameters_and_choices_travel_in_registry_order()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(10), S(2), "a");
        b.AddVideo(0, "m", S(2), S(20), S(2), "b");
        b.AddVideo(1, "m", Time.Zero, S(30), S(2), "c");
        b.AddVideo(1, "m", S(2), S(40), S(2), "d");
        TransitionInstance dip = AddTransition(b, BuiltInTransitions.Dip, "a", "b", S(1));
        b.Sequence.VideoTracks[0].Clips[0].Effects.Add(Blend("multiply"));
        b.Sequence.VideoTracks[0].Clips[0].Effects.Add(Blend("screen"));
        dip.Parameters[BuiltInTransitions.DipParameters.Green] = 0.5;
        dip.Parameters[BuiltInTransitions.DipParameters.Blue] = 9;
        TransitionInstance push = AddTransition(b, BuiltInTransitions.Push, "c", "d", S(1), track: 1);
        push.Options[BuiltInTransitions.PushOptions.Direction] = "up";
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(2));
        using CompositionFrame frame = session.Tick();

        LayerTransition first = frame.Layers[0].Transition!.Value;
        LayerTransition second = frame.Layers[1].Transition!.Value;
        Assert.Equal((102, 0f, 0.5f, 1f), (first.Code, first.Values[0], first.Values[1], first.Values[2]));
        Assert.Equal((104, 2f), (second.Code, second.Values[0]));
        Assert.Equal(BlendMode.Multiply, frame.Layers[0].Blend);
    }

    [Fact]
    public void A_package_transition_applies_its_curves_and_falls_back_to_a_crossfade_when_missing()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        content.Transitions["paquete@1.0.0/zoom"] = new TransitionCurves(
            EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code, 960, 540,
            [new TransitionCurve("x", [new CurveKey(0, 0, Interpolation.Linear), new CurveKey(1, -100, Interpolation.Linear)])],
            [new TransitionCurve("scale", [new CurveKey(0, 2, Interpolation.Linear), new CurveKey(1, 1, Interpolation.Linear)])])
        {
            MixParameters = new Dictionary<string, double> { [BuiltInTransitions.WipeParameters.Angle] = 90 },
        };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, S(10), S(2), "a");
        Clip entering = b.AddVideo(0, "m", S(2), S(20), S(2), "b");
        entering.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5)));
        b.AddVideo(1, "m", Time.Zero, S(30), S(2), "c");
        b.AddVideo(1, "m", S(2), S(40), S(2), "d");
        AddTransition(b, BuiltInTransitions.Preset, "a", "b", S(1)).Options[BuiltInTransitions.PresetOptions.Preset] = "paquete@1.0.0/zoom";
        AddTransition(b, BuiltInTransitions.Preset, "c", "d", S(1), track: 1).Options[BuiltInTransitions.PresetOptions.Preset] = "otro@1.0.0/falta";
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Content = content });

        session.Seek(S(2));
        using CompositionFrame frame = session.Tick();

        LayerFrame resolved = frame.Layers[0];
        Assert.Equal(103, resolved.Transition!.Value.Code);
        Assert.Equal((90f, 0.05f), (resolved.Transition.Value.Values[0], resolved.Transition.Value.Values[1]));
        Assert.Equal(-100, resolved.Transform.X, 6);
        Assert.Equal(0.75, resolved.Transition.Value.Incoming.Transform.Scale, 6);
        Assert.Equal(101, frame.Layers[1].Transition!.Value.Code);
        Assert.Equal(1, session.Stats.MissingContent);
    }

    [Fact]
    public void Playing_through_transitions_brings_the_incoming_clip_in_on_time()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddMedia("c", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(10), S(2), "a");
        b.AddVideo(0, "b", S(2), S(10), S(2), "b");
        b.AddVideo(0, "c", S(4), S(10), S(2), "c");
        AddTransition(b, BuiltInTransitions.Crossfade, "a", "b", S(0.5));
        AddTransition(b, BuiltInTransitions.Wipe, "b", "c", S(1));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        Settle(session, backend);
        session.Play();

        int transitions = 0;
        for (Time played = Time.Zero; played < S(5.5); played += Refresh60)
        {
            backend.Clock.Advance(Refresh60);
            using (CompositionFrame frame = session.Tick())
            {
                transitions += frame.Layers.Count(l => l.Transition is not null);
            }

            backend.RunDecoders();
        }

        TimelineStats stats = session.Stats;
        Assert.Equal(2, stats.TransitionPreRollHits);
        Assert.Equal(0, stats.TransitionPreRollMisses);
        Assert.Equal(0, stats.TransitionLateRefreshes);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.InRange(transitions, 88, 92);
    }

    /// <summary>
    /// Con 8 capas, cuatro efectos por capa no suman ni un byte a lo que ya reserva cada <c>Tick</c> sin efectos
    /// (~2,8 KB por las clases <c>LayerFrame</c>/<c>CompositionFrame</c> del contrato), y el <c>Tick</c> sigue en ≤0,3 ms.
    /// </summary>
    [Fact]
    public void Effects_add_no_bytes_to_what_each_tick_already_reserves_and_stay_under_the_budget()
    {
        const int Layers = 8;
        (long plainBytes, _) = MeasureTicks(Layers, effects: false);
        (long effectBytes, double medianMs) = MeasureTicks(Layers, effects: true);
        output.WriteLine($"MEDIDA tick_bytes_sin_efectos = {plainBytes}");
        output.WriteLine($"MEDIDA tick_bytes_con_efectos = {effectBytes}");
        output.WriteLine($"MEDIDA tick_mediana_ms_8x4 = {medianMs:F3}");

        Assert.True(plainBytes == effectBytes,
            $"Los efectos suman {effectBytes - plainBytes} B por Tick a los {plainBytes} B que ya reserva el contrato sin efectos.");
        Assert.True(medianMs <= 0.3, $"Tick con 8 capas y 4 efectos: {medianMs:F3} ms de mediana.");
    }

    [Fact]
    public void Effect_evaluation_itself_reserves_nothing()
    {
        var clip = new Clip();
        clip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.X, 4)));
        clip.Effects.Add(Animated(BuiltInEffects.Color, C.Exposure));
        clip.Effects.Add(Animated(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Amount));
        clip.Effects.Add(Animated(BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius));
        clip.Effects.Add(Animated(BuiltInEffects.Sharpen, BuiltInEffects.SharpenParameters.Amount));
        EffectPlan plan = EffectPlan.From(clip.Effects)!;
        var list = new EffectList();
        var content = new FakeEffectContent();
        Evaluate(plan, list, content, 100);

        long before = GC.GetAllocatedBytesForCurrentThread();
        double sum = Evaluate(plan, list, content, 10_000);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.True(sum > 0);
    }

    internal static EffectInstance Effect(string type, params (string Name, double Value)[] parameters)
    {
        var effect = new EffectInstance { Id = type, Type = type };
        foreach ((string name, double value) in parameters)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }

    internal static TransitionInstance AddTransition(TimelineBuilder b, string type, string from, string to, Time duration, int track = 0)
    {
        var transition = new TransitionInstance { Id = $"t-{from}", Type = type, From = from, To = to, Duration = duration };
        b.Sequence.VideoTracks[track].Transitions.Add(transition);
        return transition;
    }

    private static EffectInstance Blend(string mode)
    {
        EffectInstance blend = Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = mode;
        return blend;
    }

    private static EffectInstance Animated(string type, string parameter)
    {
        EffectInstance effect = Effect(type);
        effect.Parameters[parameter] = new Parameter
        {
            Keyframes = [new Keyframe(Time.Zero, 0), new Keyframe(S(1), 0.2, Interpolation.EaseInOut), new Keyframe(S(5), 0.9)],
        };
        return effect;
    }

    private static double Evaluate(EffectPlan plan, EffectList list, FakeEffectContent content, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            Time at = Time.FromTicks(i * 7_056_000L);
            plan.Evaluate(at, list.Reset(plan.EffectCount), content);
            sum += EffectPlan.Transform(plan, at).X + list[1].Values[0];
        }

        return sum;
    }

    /// <summary>Bytes por <c>Tick</c> en régimen estable (todo decodificado) y mediana del tiempo de <c>Tick</c>.</summary>
    private static (long Bytes, double MedianMs) MeasureTicks(int layers, bool effects)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(layers, 0);
        for (int i = 0; i < layers; i++)
        {
            b.AddMedia($"m{i}", Fps30, 30_000, backend: backend);
            Clip clip = b.AddVideo(i, $"m{i}", Time.Zero, S(i), S(600));
            if (effects)
            {
                clip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.9)));
                clip.Effects.Add(Animated(BuiltInEffects.Color, C.Exposure));
                clip.Effects.Add(Animated(BuiltInEffects.Vignette, BuiltInEffects.VignetteParameters.Amount));
                clip.Effects.Add(Animated(BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Radius));
                EffectInstance blend = Effect(BuiltInEffects.Blend);
                blend.Options[BuiltInEffects.BlendOptions.Mode] = "overlay";
                clip.Effects.Add(blend);
            }
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend);

        // En pausa, con el cuadro ya en pantalla: lo que reserva cada Tick es lo mismo con y sin efectos.
        for (int i = 0; i < 50; i++)
        {
            session.Tick().Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Ticks = 200;
        for (int i = 0; i < Ticks; i++)
        {
            session.Tick().Dispose();
        }

        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;

        // Reproduciendo a 60 Hz; los decodificadores entregan entre refrescos, fuera de la medida.
        session.Play();
        var times = new List<double>();
        for (int i = 0; i < 600; i++)
        {
            time.Advance(TimeSpan.FromTicks((long)(Refresh60.ToSeconds() * TimeSpan.TicksPerSecond)));
            long start = Stopwatch.GetTimestamp();
            session.Tick().Dispose();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            backend.RunDecoders();
        }

        times.Sort();
        return (bytes, times[times.Count / 2]);
    }

    private static void Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 10 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }
}
