using System.Reflection;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Xunit;

namespace Rodaje.Playback.Tests;

public class FrameRecipeTests
{
    private static readonly RecipeFormat Format = new(Rational.Fps30, SequenceColorSpace.Sdr709, "cadena-1");

    private static readonly int ColorCode = EffectRegistry.Find(BuiltInEffects.Color)!.Code;

    private static readonly int DenoiseCode = EffectRegistry.Find(BuiltInEffects.Denoise)!.Code;

    private static readonly int CrossfadeCode = EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code;

    private static readonly LayerTransform BaseTransform = new(1, 2, 1.5, 10, 0.9)
    {
        HdrReferenceNits = 203,
        HdrExpandNits = 400,
        Stabilization = new LayerStabilization(1, 0.01, 0.02, 0.03),
        Crop = new CropRect(0.1, 0.2, 0.3, 0.4),
        Shape = new LayerShape(1, 2, 1.1, 0.9, 3, 4, false, false),
        Pin = new LayerCornerPin(1, 2, 3, 4, 5, 6, 7, 8),
    };

    /// <summary>Todo lo que distingue una composición de prueba: cada mutación cambia uno de estos valores.</summary>
    private sealed record Spec
    {
        public int Width { get; init; } = 1920;

        public int Height { get; init; } = 1080;

        public bool ExtraLayer { get; init; }

        public string Kind { get; init; } = "video";

        public int Track { get; init; } = 1;

        public string ClipId { get; init; } = "clip";

        public string Media { get; init; } = "medio-a";

        public long Index { get; init; } = 10;

        public long? NextIndex { get; init; }

        public float Fraction { get; init; } = 0.5f;

        public FrameSampling Sampling { get; init; } = FrameSampling.Nearest;

        public long Pts { get; init; } = 100;

        public LayerTransform Transform { get; init; } = BaseTransform;

        public Rational Sar { get; init; } = new(1, 1);

        public int Rotation { get; init; }

        public int DisplayWidth { get; init; }

        public int DisplayHeight { get; init; }

        public bool Proxy { get; init; }

        public int EffectCode { get; init; } = ColorCode;

        public float EffectValue { get; init; } = 0.5f;

        public string? EffectResource { get; init; } = "looks@1.0.0/calido";

        public bool NoEffects { get; init; }

        public BlendMode Blend { get; init; } = BlendMode.Normal;

        public bool HasTransition { get; init; } = true;

        public int TransitionCode { get; init; } = CrossfadeCode;

        public float Progress { get; init; } = 0.3f;

        public float TransitionValue { get; init; } = 0.25f;

        public (float From, float To)? Shutter { get; init; } = (0.2f, 0.4f);

        public string? TransitionResource { get; init; } = "transiciones@1.0.0/luma";

        public long IncomingIndex { get; init; } = 5;

        public bool Motion { get; init; } = true;

        public double MotionX { get; init; } = 3;

        public TextMotion Text { get; init; } = new(TextPhase.In, 0.5, 1, 0.2);

        public string Title { get; init; } = "Hola";

        public string TitleClipId { get; init; } = "titulo";

        public Time At { get; init; } = Time.FromSeconds(1);
    }

    private sealed class Sources : IRecipeSources
    {
        public Dictionary<DecodedFrame, string> Media { get; } = new(ReferenceEqualityComparer.Instance);

        public HashSet<string> MissingResources { get; } = [];

        public bool MissingFonts { get; set; }

        public bool MissingMedia { get; set; }

        public RecipeSource? Source(LayerFrame layer) =>
            MissingMedia || !Media.TryGetValue(layer.Frame, out string? media) ? null : new RecipeSource(media, "yuv420p8 bt709 tv 1920x1080");

        public string? Resource(ResourceKey key) => MissingResources.Contains(key.Reference) ? null : "v:" + key.Reference;

        public string? Fonts(GeneratedContent content) => MissingFonts ? null : "fuentes-1";
    }

    /// <summary>Por cada propiedad incluida, una mutación que la cambia (de una composición a otra).</summary>
    private static readonly Dictionary<string, Func<Spec, (Spec Before, Spec After)>> IncludedMutations = new()
    {
        ["CompositionFrame.Width"] = s => (s, s with { Width = 1280 }),
        ["CompositionFrame.Height"] = s => (s, s with { Height = 720 }),
        ["CompositionFrame.Layers"] = s => (s, s with { ExtraLayer = true }),
        ["LayerFrame.Nested"] = s => (s, s with { Kind = "nested" }),
        ["LayerFrame.Generated"] = s => (s, s with { Kind = "title" }),
        ["LayerFrame.TrackIndex"] = s => (s, s with { Track = 2 }),
        ["LayerFrame.Frame"] = s => (s, s with { Media = "medio-b" }),
        ["LayerFrame.Next"] = s => (s with { NextIndex = 11, Sampling = FrameSampling.Blend }, s with { NextIndex = 12, Sampling = FrameSampling.Blend }),
        ["LayerFrame.Fraction"] = s => (s with { NextIndex = 11, Sampling = FrameSampling.Blend }, s with { NextIndex = 11, Sampling = FrameSampling.Blend, Fraction = 0.25f }),
        ["LayerFrame.Sampling"] = s => (s with { NextIndex = 11, Sampling = FrameSampling.Blend }, s with { NextIndex = 11, Sampling = FrameSampling.OpticalFlow }),
        ["LayerFrame.Transform"] = s => (s, s with { Transform = LayerTransform.Identity }),
        ["LayerFrame.SampleAspectRatio"] = s => (s, s with { Sar = new Rational(4, 3) }),
        ["LayerFrame.Rotation"] = s => (s, s with { Rotation = 90 }),
        ["LayerFrame.DisplayWidth"] = s => (s, s with { DisplayWidth = 3840 }),
        ["LayerFrame.DisplayHeight"] = s => (s, s with { DisplayHeight = 2160 }),
        ["LayerFrame.IsProxy"] = s => (s, s with { Proxy = true }),
        ["LayerFrame.Effects"] = s => (s, s with { NoEffects = true }),
        ["LayerFrame.Blend"] = s => (s, s with { Blend = BlendMode.Screen }),
        ["LayerFrame.Transition"] = s => (s, s with { HasTransition = false }),
        ["LayerFrame.MotionSamples"] = s => (s, s with { Motion = false }),
        ["LayerFrame.TextMotion"] = s => (s, s with { Text = TextMotion.Rest }),
        ["LayerTransform.X"] = s => (s, s with { Transform = s.Transform with { X = 9 } }),
        ["LayerTransform.Y"] = s => (s, s with { Transform = s.Transform with { Y = 9 } }),
        ["LayerTransform.Scale"] = s => (s, s with { Transform = s.Transform with { Scale = 2 } }),
        ["LayerTransform.RotationDegrees"] = s => (s, s with { Transform = s.Transform with { RotationDegrees = 45 } }),
        ["LayerTransform.Opacity"] = s => (s, s with { Transform = s.Transform with { Opacity = 0.5 } }),
        ["LayerTransform.HdrReferenceNits"] = s => (s, s with { Transform = s.Transform with { HdrReferenceNits = 100 } }),
        ["LayerTransform.HdrExpandNits"] = s => (s, s with { Transform = s.Transform with { HdrExpandNits = 0 } }),
        ["LayerTransform.Stabilization"] = s => (s, s with { Transform = s.Transform with { Stabilization = null } }),
        ["LayerTransform.Crop"] = s => (s, s with { Transform = s.Transform with { Crop = null } }),
        ["LayerTransform.Shape"] = s => (s, s with { Transform = s.Transform with { Shape = null } }),
        ["LayerTransform.Pin"] = s => (s, s with { Transform = s.Transform with { Pin = null } }),
        ["LayerShape.AnchorX"] = s => Shape(s, x => x with { AnchorX = 9 }),
        ["LayerShape.AnchorY"] = s => Shape(s, x => x with { AnchorY = 9 }),
        ["LayerShape.ScaleX"] = s => Shape(s, x => x with { ScaleX = 9 }),
        ["LayerShape.ScaleY"] = s => Shape(s, x => x with { ScaleY = 9 }),
        ["LayerShape.SkewX"] = s => Shape(s, x => x with { SkewX = 9 }),
        ["LayerShape.SkewY"] = s => Shape(s, x => x with { SkewY = 9 }),
        ["LayerShape.FlipX"] = s => Shape(s, x => x with { FlipX = true }),
        ["LayerShape.FlipY"] = s => Shape(s, x => x with { FlipY = true }),
        ["LayerCornerPin.TopLeftX"] = s => Pin(s, x => x with { TopLeftX = 99 }),
        ["LayerCornerPin.TopLeftY"] = s => Pin(s, x => x with { TopLeftY = 99 }),
        ["LayerCornerPin.TopRightX"] = s => Pin(s, x => x with { TopRightX = 99 }),
        ["LayerCornerPin.TopRightY"] = s => Pin(s, x => x with { TopRightY = 99 }),
        ["LayerCornerPin.BottomRightX"] = s => Pin(s, x => x with { BottomRightX = 99 }),
        ["LayerCornerPin.BottomRightY"] = s => Pin(s, x => x with { BottomRightY = 99 }),
        ["LayerCornerPin.BottomLeftX"] = s => Pin(s, x => x with { BottomLeftX = 99 }),
        ["LayerCornerPin.BottomLeftY"] = s => Pin(s, x => x with { BottomLeftY = 99 }),
        ["LayerStabilization.A"] = s => Stab(s, x => x with { A = 0.5 }),
        ["LayerStabilization.B"] = s => Stab(s, x => x with { B = 0.5 }),
        ["LayerStabilization.X"] = s => Stab(s, x => x with { X = 0.5 }),
        ["LayerStabilization.Y"] = s => Stab(s, x => x with { Y = 0.5 }),
        ["CropRect.Left"] = s => Crop(s, x => x with { Left = 0 }),
        ["CropRect.Top"] = s => Crop(s, x => x with { Top = 0 }),
        ["CropRect.Right"] = s => Crop(s, x => x with { Right = 0 }),
        ["CropRect.Bottom"] = s => Crop(s, x => x with { Bottom = 0 }),
        ["LayerEffect.Code"] = s => (s, s with { EffectCode = EffectRegistry.Find(BuiltInEffects.Vignette)!.Code }),
        ["LayerEffect.Values"] = s => (s, s with { EffectValue = 0.75f }),
        ["LayerEffect.Resource"] = s => (s, s with { EffectResource = null }),
        ["LayerTransition.Code"] = s => (s, s with { TransitionCode = EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code }),
        ["LayerTransition.Progress"] = s => (s, s with { Progress = 0.6f }),
        ["LayerTransition.Values"] = s => (s, s with { TransitionValue = 0.5f }),
        ["LayerTransition.Incoming"] = s => (s, s with { IncomingIndex = 6 }),
        ["LayerTransition.Shutter"] = s => (s, s with { Shutter = null }),
        ["LayerTransition.Resource"] = s => (s, s with { TransitionResource = null }),
        ["TextMotion.Phase"] = s => (s, s with { Text = s.Text with { Phase = TextPhase.Out } }),
        ["TextMotion.Progress"] = s => (s, s with { Text = s.Text with { Progress = 0.9 } }),
        ["TextMotion.Word"] = s => (s, s with { Text = s.Text with { Word = 2 } }),
        ["TextMotion.WordProgress"] = s => (s, s with { Text = s.Text with { WordProgress = 0.9 } }),
        ["GeneratedContent.Key"] = s => (s with { Kind = "title" }, s with { Kind = "title", Title = "Adiós" }),
        ["DecodedFrame.Index"] = s => (s, s with { Index = 11 }),
        ["ResourceKey.Reference"] = s => (s, s with { EffectResource = "looks@1.0.0/frio" }),
    };

    /// <summary>Por cada propiedad excluida que se puede cambiar sola, una mutación que no debe cambiar la huella.</summary>
    private static readonly Dictionary<string, Func<Spec, (Spec Before, Spec After)>> ExcludedMutations = new()
    {
        ["CompositionFrame.TimelineTime"] = s => (s, s with { At = Time.FromSeconds(42) }),
        ["LayerFrame.ClipId"] = s => (s, s with { ClipId = "otro" }),
        ["DecodedFrame.Pts"] = s => (s, s with { Pts = 999 }),
        ["GeneratedContent.ClipId"] = s => (s with { Kind = "title" }, s with { Kind = "title", TitleClipId = "otro" }),
    };

    [Fact]
    public void Cada_parametro_de_un_efecto_y_de_una_transicion_cambia_la_huella()
    {
        RenderCacheKey? KeyWith(int effectSlot, int transitionSlot)
        {
            var sources = new Sources();
            var frame = new DecodedFrame(3, Time.Zero, Time.FromTicks(1), null, 0);
            var incoming = new DecodedFrame(4, Time.Zero, Time.FromTicks(1), null, 0);
            sources.Media[frame] = "medio";
            sources.Media[incoming] = "medio-entra";
            var effect = default(EffectValues);
            var mix = default(EffectValues);
            if (effectSlot >= 0)
            {
                effect[effectSlot] = 0.5f;
            }

            if (transitionSlot >= 0)
            {
                mix[transitionSlot] = 0.5f;
            }

            var layer = new LayerFrame(0, "c", frame, BaseTransform)
            {
                Effects = [new LayerEffect(ColorCode, effect, null)],
                Transition = new LayerTransition(CrossfadeCode, 0.5f, mix, new LayerFrame(0, "entra", incoming, LayerTransform.Identity)),
            };
            using var composition = new CompositionFrame(Time.Zero, 1920, 1080, [layer]);
            return FrameRecipe.Compute(composition, Format, sources);
        }

        var keys = new HashSet<RenderCacheKey> { KeyWith(-1, -1)!.Value };
        for (int slot = 0; slot < EffectDefinition.MaxParameters; slot++)
        {
            Assert.True(keys.Add(KeyWith(slot, -1)!.Value), $"El parámetro {slot} del efecto no entra en la huella.");
            Assert.True(keys.Add(KeyWith(-1, slot)!.Value), $"El parámetro {slot} de la transición no entra en la huella.");
        }
    }

    [Fact]
    public void Cada_propiedad_de_la_composicion_esta_clasificada_una_sola_vez()
    {
        Assert.Equal(FrameRecipe.Included.Keys.OrderBy(t => t.Name), FrameRecipe.Excluded.Keys.OrderBy(t => t.Name));
        foreach ((Type type, string[] included) in FrameRecipe.Included)
        {
            string[] excluded = FrameRecipe.Excluded[type];
            string[] properties = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Where(n => n != "EqualityContract")];
            Assert.Empty(included.Intersect(excluded));
            Assert.Equal(properties.Order(), included.Concat(excluded).Order());
        }
    }

    [Fact]
    public void Los_tipos_que_cuelgan_de_la_composicion_estan_todos_en_la_lista()
    {
        var reachable = new HashSet<Type>();
        Visit(typeof(CompositionFrame), reachable);
        Assert.Equal(reachable.OrderBy(t => t.Name), FrameRecipe.Included.Keys.OrderBy(t => t.Name));
    }

    [Fact]
    public void Cada_propiedad_incluida_tiene_su_mutacion_y_cambia_la_huella()
    {
        string[] all = [.. FrameRecipe.Included.SelectMany(p => p.Value.Select(name => $"{p.Key.Name}.{name}"))];
        Assert.Equal(all.Order(), IncludedMutations.Keys.Order());
        foreach ((string name, Func<Spec, (Spec Before, Spec After)> mutate) in IncludedMutations)
        {
            (Spec before, Spec after) = mutate(new Spec());
            Assert.True(Key(before) != Key(after), name);
        }
    }

    [Fact]
    public void Las_propiedades_excluidas_no_cambian_la_huella()
    {
        foreach ((string name, Func<Spec, (Spec Before, Spec After)> mutate) in ExcludedMutations)
        {
            (Spec before, Spec after) = mutate(new Spec());
            RenderCacheKey? a = Key(before);
            Assert.NotNull(a);
            Assert.True(a == Key(after), name);
        }
    }

    [Fact]
    public void La_huella_es_estable_entre_procesos()
    {
        // Valor fijo: si cambia, la receta cambió de forma o de significado y FrameRecipe.Version debe subir.
        Assert.Equal("80fd9b0d7d9f452e7214d587b1031753", Key(new Spec())?.ToString());
        Assert.Equal(Key(new Spec()), Key(new Spec()));
    }

    [Theory]
    [InlineData(FrameSampling.Blend)]
    [InlineData(FrameSampling.OpticalFlow)]
    public void Interpolation_changes_the_recipe_but_nearest_keeps_the_original(FrameSampling sampling)
    {
        var sources = new Sources();
        var frame = new DecodedFrame(10, Time.Zero, Time.FromTicks(1), null, 0);
        var next = new DecodedFrame(11, Time.Zero, Time.FromTicks(1), null, 0);
        sources.Media[frame] = "medio";
        sources.Media[next] = "medio";
        Assert.True(frame.TryRetain());
        using (var plain = new CompositionFrame(Time.Zero, 1920, 1080, [new LayerFrame(0, "c", frame, LayerTransform.Identity)]))
        using (var sampled = new CompositionFrame(Time.Zero, 1920, 1080,
            [new LayerFrame(0, "c", frame, LayerTransform.Identity) { Next = next, Fraction = 0.5f, Sampling = sampling }]))
        {
            RenderCacheKey? expected = FrameRecipe.Compute(plain, Format, sources);
            Assert.NotNull(expected);
            RenderCacheKey? interpolated = FrameRecipe.Compute(sampled, Format, sources);
            Assert.NotNull(interpolated);
            Assert.NotEqual(expected, interpolated);
            Assert.True(frame.TryRetain());
            Assert.True(next.TryRetain());
            using var nearest = new CompositionFrame(Time.Zero, 1920, 1080,
                [new LayerFrame(0, "c", frame, LayerTransform.Identity) { Next = next, Fraction = 0.25f, Sampling = FrameSampling.Nearest }]);
            Assert.Equal(expected, FrameRecipe.Compute(nearest, Format, sources));
        }

        Assert.True(frame.IsReleased);
        Assert.True(next.IsReleased);
    }

    [Fact]
    public void Interpolation_includes_the_content_identity_of_B()
    {
        var sources = new Sources();
        var frame = new DecodedFrame(10, Time.Zero, Time.FromTicks(1), null, 0);
        var next = new DecodedFrame(11, Time.Zero, Time.FromTicks(1), null, 0);
        sources.Media[frame] = "medio";
        sources.Media[next] = "medio";
        using var composition = new CompositionFrame(Time.Zero, 1920, 1080,
            [new LayerFrame(0, "c", frame, LayerTransform.Identity) { Next = next, Fraction = 0.5f, Sampling = FrameSampling.Blend }]);
        RenderCacheKey? expected = FrameRecipe.Compute(composition, Format, sources);
        Assert.NotNull(expected);
        sources.Media[next] = "otro-medio";
        Assert.NotEqual(expected, FrameRecipe.Compute(composition, Format, sources));
        sources.Media.Remove(next);
        Assert.Null(FrameRecipe.Compute(composition, Format, sources));
    }

    [Fact]
    public void Sin_receta_si_falta_algo_o_es_un_respaldo()
    {
        Assert.Null(Key(new Spec { Proxy = true }));
        Assert.Null(Key(new Spec(), s => s.MissingMedia = true));
        Assert.Null(Key(new Spec(), s => s.MissingResources.Add("looks@1.0.0/calido")));
        Assert.Null(Key(new Spec(), s => s.MissingResources.Add("transiciones@1.0.0/luma")));
        Assert.Null(Key(new Spec { Kind = "title" }, s => s.MissingFonts = true));
        Assert.Null(Key(new Spec { EffectCode = DenoiseCode, EffectValue = 0.5f, EffectResource = null }));
        Assert.NotNull(Key(new Spec { EffectCode = DenoiseCode, EffectValue = 0, EffectResource = null }));
        Assert.NotNull(Key(new Spec { Kind = "title" }));
    }

    private static (Spec, Spec) Shape(Spec s, Func<LayerShape, LayerShape> change) =>
        (s, s with { Transform = s.Transform with { Shape = change(s.Transform.Shape!.Value) } });

    private static (Spec, Spec) Pin(Spec s, Func<LayerCornerPin, LayerCornerPin> change) =>
        (s, s with { Transform = s.Transform with { Pin = change(s.Transform.Pin!.Value) } });

    private static (Spec, Spec) Stab(Spec s, Func<LayerStabilization, LayerStabilization> change) =>
        (s, s with { Transform = s.Transform with { Stabilization = change(s.Transform.Stabilization!.Value) } });

    private static (Spec, Spec) Crop(Spec s, Func<CropRect, CropRect> change) =>
        (s, s with { Transform = s.Transform with { Crop = change(s.Transform.Crop!.Value) } });

    private static void Visit(Type type, HashSet<Type> reachable)
    {
        Type target = Nullable.GetUnderlyingType(type) ?? type;
        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            target = target.GetGenericArguments()[0];
        }

        if (target.Namespace is not ("Rodaje.Playback.Composition" or "Rodaje.Playback" or "Rodaje.Core.Effects") || target.IsEnum
            || target == typeof(EffectValues) || !reachable.Add(target))
        {
            return;
        }

        foreach (PropertyInfo property in target.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (FrameRecipe.Included.TryGetValue(target, out string[]? included) && included.Contains(property.Name))
            {
                Visit(property.PropertyType, reachable);
            }
        }
    }

    private static RenderCacheKey? Key(Spec spec, Action<Sources>? configure = null)
    {
        var sources = new Sources();
        configure?.Invoke(sources);
        using CompositionFrame frame = Make(spec, sources);
        return FrameRecipe.Compute(frame, Format, sources);
    }

    private static CompositionFrame Make(Spec s, Sources sources)
    {
        List<LayerFrame> layers = [Layer(s, sources)];
        if (s.ExtraLayer)
        {
            layers.Add(Video(s with { HasTransition = false, Index = 1 }, sources));
        }

        return new CompositionFrame(s.At, s.Width, s.Height, layers);
    }

    private static LayerFrame Layer(Spec s, Sources sources) => s.Kind switch
    {
        "nested" => Decorate(s, sources, t => new LayerFrame(s.Track, s.ClipId, [Video(s with { HasTransition = false }, sources)], t)),
        "title" => Decorate(s, sources, t => new LayerFrame(s.Track, s.ClipId,
            new GeneratedContent("text", s.Title, new TextStyle(), 0, s.Width, s.Height, 1) { ClipId = s.TitleClipId }, t)),
        _ => Video(s, sources),
    };

    private static LayerFrame Video(Spec s, Sources sources)
    {
        var decoded = new DecodedFrame(s.Index, Time.FromTicks(s.Pts), Time.FromTicks(1), null, 0);
        sources.Media[decoded] = s.Media;
        return Decorate(s, sources, t => new LayerFrame(s.Track, s.ClipId, decoded, t, s.Sar, s.Rotation));
    }

    /// <summary>La capa con todo lo de la especificación: la de <paramref name="bare"/> da el tipo y se copia con sus extras.</summary>
    private static LayerFrame Decorate(Spec s, Sources sources, Func<LayerTransform, LayerFrame> bare)
    {
        var values = default(EffectValues);
        values[0] = s.EffectValue;
        values[1] = s.EffectValue;
        var transitionValues = default(EffectValues);
        transitionValues[0] = s.TransitionValue;
        LayerTransition? transition = null;
        if (s.HasTransition)
        {
            var incoming = new DecodedFrame(s.IncomingIndex, Time.Zero, Time.FromTicks(1), null, 0);
            sources.Media[incoming] = "medio-entra";
            transition = new LayerTransition(s.TransitionCode, s.Progress, transitionValues, new LayerFrame(s.Track, "entra", incoming, LayerTransform.Identity))
            {
                Shutter = s.Shutter,
                Resource = s.TransitionResource is { } r ? new ResourceKey(r) : null,
            };
        }

        LayerFrame layer = bare(s.Transform);
        LayerEffect[] effects = s.NoEffects ? [] : [new LayerEffect(s.EffectCode, values, s.EffectResource is { } e ? new ResourceKey(e) : null)];
        LayerTransform[]? motion = s.Motion ? [s.Transform, s.Transform with { X = s.Transform.X + s.MotionX }] : null;
        LayerFrame result = layer.Nested is { } nested ? new LayerFrame(s.Track, s.ClipId, nested, s.Transform) { Effects = effects, Blend = s.Blend, Transition = transition, MotionSamples = motion, TextMotion = s.Text, DisplayWidth = s.DisplayWidth, DisplayHeight = s.DisplayHeight, IsProxy = s.Proxy }
            : layer.Generated is { } title ? new LayerFrame(s.Track, s.ClipId, title, s.Transform) { Effects = effects, Blend = s.Blend, Transition = transition, MotionSamples = motion, TextMotion = s.Text, DisplayWidth = s.DisplayWidth, DisplayHeight = s.DisplayHeight, IsProxy = s.Proxy }
            : new LayerFrame(s.Track, s.ClipId, layer.Frame, s.Transform, s.Sar, s.Rotation) { Effects = effects, Blend = s.Blend, Transition = transition, MotionSamples = motion, TextMotion = s.Text, DisplayWidth = s.DisplayWidth, DisplayHeight = s.DisplayHeight, IsProxy = s.Proxy,
                Next = s.NextIndex is { } next ? new DecodedFrame(next, Time.Zero, Time.FromTicks(1), null, 0) : null, Fraction = s.Fraction, Sampling = s.Sampling };
        if (result.Next is { } following) sources.Media[following] = s.Media;
        return result;
    }
}
