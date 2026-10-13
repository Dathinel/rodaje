using System.Globalization;
using System.Text;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Offline;

/// <summary>Proyectos y ayudas comunes de las pruebas del motor fuera de línea.</summary>
internal static class OfflineProjects
{
    public const string Lut = "looks@1.0.0/calido";

    public const string PresetReference = "transiciones@1.0.0/giro";

    public static OfflineTimeline Open(TimelineBuilder builder, FakeTimelineBackend backend, OfflineTimelineOptions? options = null,
        Func<string, bool>? mediaExists = null) =>
        new(builder.Project, builder.Sequence.Id, null, (options ?? new OfflineTimelineOptions()) with { ProjectDirectory = @"C:\proyecto" },
            backend, mediaExists);

    /// <summary>
    /// Tres pistas con 2×, 0,5×, 1001/1000×, «Bala», congelado y reversa; cuatro transiciones (fundido, barrido, empuje y
    /// una de paquete), color con keyframes y LUT, transformación animada y mezcla.
    /// </summary>
    public static TimelineBuilder Full(Rational rate, FakeTimelineBackend backend, FakeEffectContent content)
    {
        var b = new TimelineBuilder(3, 0, rate);
        b.AddMedia("a", Rational.Fps30, 6000, backend: backend);
        b.AddMedia("b", Rational.Ntsc24, 6000, backend: backend);
        b.AddMedia("c", Rational.Fps60, 9000, backend: backend);

        Clip fast = SpeedClips.WithRate(b.AddVideo(0, "a", Time.Zero, S(2), S(2), "rapido"), new Rational(2, 1), S(2), S(6));
        Clip slow = SpeedClips.WithRate(b.AddVideo(0, "b", fast.End, S(10), S(1), "lento"), new Rational(1, 2), S(10), S(11));
        Clip frozen = SpeedClips.Frozen(b.AddVideo(0, "a", slow.End, S(20), S(1), "congelado"), S(20), S(1.5));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "rapido", "lento", S(0.5));
        TransitionInstance wipe = TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Wipe, "lento", "congelado", S(0.4));
        wipe.Alignment = TransitionAlignment.Before;
        wipe.Curve = Interpolation.EaseInOut;

        Clip ntsc = SpeedClips.WithRate(b.AddVideo(1, "c", S(0.5), S(1), S(2), "ntsc"), new Rational(1001, 1000), S(1), S(3));
        Clip bullet = SpeedClips.WithCurve(b.AddVideo(1, "b", ntsc.End, S(30), S(3), "bala"), SpeedPreset.Bullet, S(2.5), S(30), S(33));
        EffectInstance color = TimelineEffectsTests.Effect(BuiltInEffects.Color);
        color.Parameters[BuiltInEffects.ColorParameters.Exposure] = new Parameter
        {
            Keyframes = [new Keyframe(S(1), -0.5), new Keyframe(S(2.5), 1, Interpolation.EaseInOut)],
        };
        color.Options[BuiltInEffects.ColorOptions.Lut] = Lut;
        ntsc.Effects.Add(color);
        bullet.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Vignette, (BuiltInEffects.VignetteParameters.Amount, 0.6)));
        TransitionInstance slide = TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Slide, "ntsc", "bala", S(0.6), track: 1);
        slide.Alignment = TransitionAlignment.After;
        slide.Curve = Interpolation.Bezier;
        slide.Ease = new BezierEase(0.2, 0.8, 0.4, 1);

        Clip moving = b.AddVideo(2, "c", S(1), S(40), S(1.5), "mueve");
        EffectInstance transform = TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5));
        transform.Parameters[BuiltInEffects.TransformParameters.X] = new Parameter
        {
            Keyframes =
            [
                new Keyframe(S(40), -300, Interpolation.Bezier) { Ease = new BezierEase(0.2, 1.3, 0.4, 1), SpatialOut = 100 },
                new Keyframe(S(41.5), 300) { SpatialIn = -50 },
            ],
        };
        transform.Parameters[BuiltInEffects.TransformParameters.Y] = new Parameter
        {
            Keyframes = [new Keyframe(S(40), 0) { SpatialOut = -200 }, new Keyframe(S(41.5), 0) { SpatialIn = -200 }],
        };
        transform.Parameters[BuiltInEffects.TransformParameters.SkewX] = new Parameter { Value = 10 };
        transform.Parameters[BuiltInEffects.TransformParameters.AnchorX] = new Parameter { Value = 40 };
        transform.Parameters[BuiltInEffects.TransformParameters.ShutterAngle] = new Parameter { Value = 180 };
        transform.Parameters[BuiltInEffects.TransformParameters.MotionSamples] = new Parameter { Value = 6 };
        transform.Options[BuiltInEffects.TransformOptions.FlipX] = "on";
        moving.Effects.Add(transform);
        EffectInstance blend = TimelineEffectsTests.Effect(BuiltInEffects.Blend);
        blend.Options[BuiltInEffects.BlendOptions.Mode] = "screen";
        moving.Effects.Add(blend);
        Clip reversed = Reversed(b.AddVideo(2, "a", moving.End, S(50), S(1.5), "reversa"), S(50), S(51.5));
        TransitionInstance preset = TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Preset, "mueve", "reversa", S(0.5), track: 2);
        preset.Options[BuiltInTransitions.PresetOptions.Preset] = PresetReference;

        content.ReadyLuts.Add(Lut);
        content.Transitions[PresetReference] = new TransitionCurves(
            EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, 1920, 1080,
            [new TransitionCurve(BuiltInEffects.TransformParameters.Rotation, [new CurveKey(0, 0, Interpolation.Linear), new CurveKey(1, 90, Interpolation.Linear)])],
            [new TransitionCurve(BuiltInEffects.TransformParameters.Scale, [new CurveKey(0, 0.5, Interpolation.Linear), new CurveKey(1, 1, Interpolation.Linear)])]);
        Assert.Empty(ModelValidator.Validate(b.Project));
        _ = reversed;
        return b;
    }

    /// <summary>Recorre <c>[sourceIn, sourceOut)</c> hacia atrás a 1×.</summary>
    public static Clip Reversed(Clip clip, Time sourceIn, Time sourceOut)
    {
        clip.Speed = new ClipSpeed { Reverse = true };
        ClipTimeMap.Fit(clip.Start, sourceIn, sourceOut, clip.Speed).ApplyTo(clip);
        return clip;
    }

    /// <summary>Todo lo que distingue una composición: capas, cuadros, transformación, efectos, mezcla y transición.</summary>
    public static string Describe(CompositionFrame frame)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"t={frame.TimelineTime.Ticks} {frame.Width}x{frame.Height}");
        DescribeLayers(frame.Layers, text);
        return text.ToString();
    }

    private static void DescribeLayers(IReadOnlyList<LayerFrame> layers, StringBuilder text)
    {
        foreach (LayerFrame layer in layers)
        {
            text.Append(" | ");
            Describe(layer, text);
            if (layer.Transition is { } transition)
            {
                text.Append(CultureInfo.InvariantCulture, $" ~{transition.Code}@{transition.Progress:R}[{Values(transition.Values)}]");
                if (transition.Shutter is { } shutter)
                {
                    text.Append(CultureInfo.InvariantCulture, $"({shutter.From:R}..{shutter.To:R})");
                }

                text.Append(" <- ");
                Describe(transition.Incoming, text);
            }
        }
    }

    private static void Describe(LayerFrame layer, StringBuilder text)
    {
        if (layer.Nested is { } nested)
        {
            text.Append(" {");
            DescribeLayers(nested, text);
            text.Append(" }");
        }

        string source = layer.Generated is { } title ? "titulo:" + title.Key
            : layer.Frame.Payload is FakeFramePayload fake ? Path.GetFileNameWithoutExtension(fake.Path) : "?";
        text.Append(CultureInfo.InvariantCulture,
            $"{layer.TrackIndex}:{layer.ClipId}:{source}#{layer.Frame.Index} {layer.Transform} sar={layer.SampleAspectRatio} rot={layer.Rotation} " +
            $"{layer.Blend} proxy={layer.IsProxy} disp={layer.DisplayWidth}x{layer.DisplayHeight}");
        foreach (LayerEffect effect in layer.Effects)
        {
            text.Append(CultureInfo.InvariantCulture, $" fx{effect.Code}[{Values(effect.Values)}]{effect.Resource?.Reference}");
        }

        if (layer.MotionSamples is { } samples)
        {
            text.Append(" obturador[").AppendJoin(';', samples).Append(']');
        }

        if (layer.TextMotion != TextMotion.Rest)
        {
            text.Append(CultureInfo.InvariantCulture, $" texto[{layer.TextMotion}]");
        }
    }

    private static string Values(EffectValues values)
    {
        var parts = new string[EffectDefinitionSlots];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = values[i].ToString("R", CultureInfo.InvariantCulture);
        }

        return string.Join(',', parts);
    }

    private const int EffectDefinitionSlots = Rodaje.Core.Effects.EffectDefinition.MaxParameters;
}
