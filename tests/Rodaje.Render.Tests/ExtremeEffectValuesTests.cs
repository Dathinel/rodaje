using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Tarea 92, en la GPU (WARP, el rasterizador de software que corre en el runner de Windows): cada parámetro y cada
/// opción de cada efecto de imagen, transición y transformación con valores extremos (mínimo, máximo, fuera del rango, 0,
/// NaN, ±∞) llega a las constantes del shader dentro del rango, no lanza y no deja NaN ni infinitos en la imagen
/// (se lee en coma flotante, donde un NaN no se redondea a negro).
/// </summary>
public sealed class ExtremeEffectValuesTests
{
    private const int Width = 64;
    private const int Height = 48;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<string> ImageEffects()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition d in EffectRegistry.All)
        {
            if (!d.IsTransition && !d.Hidden && d.Stage is EffectStage.PerPixel or EffectStage.Spatial)
            {
                data.Add(d.Type);
            }
        }

        return data;
    }

    public static TheoryData<string> Transitions()
    {
        var data = new TheoryData<string>();
        foreach (EffectDefinition d in EffectRegistry.Transitions.Where(d => d.Stage == EffectStage.Transition))
        {
            data.Add(d.Type);
        }

        return data;
    }

    private static float[] Values(ParameterDefinition p) =>
    [
        (float)p.Minimum, (float)p.Maximum, (float)(p.Minimum - 1), (float)(p.Maximum + 1), 0, -1e30f, 1e30f, float.NaN,
        float.PositiveInfinity, float.NegativeInfinity,
    ];

    private static readonly float[] Indexes = [-1, 1e9f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];

    [GpuTheory]
    [MemberData(nameof(ImageEffects))]
    public void Cada_valor_extremo_de_un_efecto_da_una_imagen_sin_NaN(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = Ready(gpu);
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (float value in Values(p))
            {
                LayerEffect effect = Fx.Make(type);
                EffectValues values = effect.Values;
                values[p.Slot] = value;
                AssertFinite(compositor, gpu, new LayerLook([effect with { Values = values }]), $"{type}/{p.Name} = {value}");
            }
        }

        foreach (int slot in ChoiceSlots(definition))
        {
            foreach (float index in Indexes)
            {
                LayerEffect effect = Fx.Make(type);
                EffectValues values = effect.Values;
                values[slot] = index;
                AssertFinite(compositor, gpu, new LayerLook([effect with { Values = values }]), $"{type}, opción del hueco {slot} = {index}");
            }
        }

        // El hueco del tiempo (el grano) y todos los valores no numéricos a la vez.
        LayerEffect poisoned = Fx.Make(type);
        EffectValues all = poisoned.Values;
        for (int i = 0; i < definition.TimeSlot + 1 && i < 16; i++)
        {
            all[i] = float.NaN;
        }

        AssertFinite(compositor, gpu, new LayerLook([poisoned with { Values = all }]), $"{type} con todo NaN");
    }

    [GpuTheory]
    [MemberData(nameof(Transitions))]
    public void Cada_valor_extremo_de_una_transicion_da_una_imagen_sin_NaN(string type)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = Ready(gpu);
        foreach (ParameterDefinition p in definition.Parameters)
        {
            foreach (float value in Values(p))
            {
                EffectValues values = Fx.Transition(type);
                values[p.Slot] = value;
                AssertTransition(compositor, gpu, definition, values, 0.5f, null, $"{type}/{p.Name} = {value}");
            }
        }

        foreach (int slot in ChoiceSlots(definition))
        {
            foreach (float index in Indexes)
            {
                EffectValues values = Fx.Transition(type);
                values[slot] = index;
                AssertTransition(compositor, gpu, definition, values, 0.5f, null, $"{type}, opción del hueco {slot} = {index}");
            }
        }

        foreach (float progress in new[] { 0f, 1f, -1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            AssertTransition(compositor, gpu, definition, Fx.Transition(type), progress, null, $"{type} con avance {progress}");
        }

        foreach ((float from, float to) in new[] { (float.NaN, float.NaN), (-1f, 2f), (0.6f, 0.4f), (float.NegativeInfinity, float.PositiveInfinity) })
        {
            AssertTransition(compositor, gpu, definition, Fx.Transition(type), 0.5f, (from, to), $"{type} con obturador {from} a {to}");
        }
    }

    [GpuFact]
    public void Una_transformacion_con_valores_extremos_da_una_imagen_sin_NaN()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = Ready(gpu);
        double[] extremes = [0, -1, 1e-30, 1e30, -1e30, 1e300, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        foreach (double v in extremes)
        {
            LayerTransform[] transforms =
            [
                new(v, 0, 1, 0, 1), new(0, v, 1, 0, 1), new(0, 0, v, 0, 1), new(0, 0, 1, v, 1), new(0, 0, 1, 0, v),
                LayerTransform.Identity with { Shape = new LayerShape(v, v, 1, 1, 0, 0, false, false) },
                LayerTransform.Identity with { Shape = new LayerShape(0, 0, v, 1, 0, 0, true, false) },
                LayerTransform.Identity with { Shape = new LayerShape(0, 0, 1, 1, v, v, false, true) },
                LayerTransform.Identity with { Crop = new CropRect(v, v, v, v) },
                LayerTransform.Identity with { Crop = new CropRect(0, 0, v, 0) },
                LayerTransform.Identity with { Pin = new LayerCornerPin(v, 0, 0, 0, 0, v, 0, 0) },
                LayerTransform.Identity with { Stabilization = new LayerStabilization(v, 0, v, v) },
            ];
            foreach (LayerTransform transform in transforms)
            {
                float[] pixels = Render(compositor, gpu, null, transform);
                AssertFinite(pixels, $"transformación {transform}");
            }
        }
    }

    private static Compositor Ready(GpuDevice gpu)
    {
        var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(120)), "Los shaders de efectos no estuvieron listos.");
        return compositor;
    }

    /// <summary>Los huecos de las opciones de elección, que siguen a los parámetros.</summary>
    private static IEnumerable<int> ChoiceSlots(EffectDefinition definition)
    {
        int slot = definition.Parameters.Length;
        foreach (OptionDefinition option in definition.Options.Where(o => o.Kind == OptionKind.Choice))
        {
            yield return slot++;
        }
    }

    private static void AssertTransition(Compositor compositor, GpuDevice gpu, EffectDefinition definition, EffectValues values, float progress,
        (float, float)? shutter, string what)
    {
        LayerFrame incoming = Layers.From(gpu, Image(7), Bt709, track: 1);
        var transition = new LayerTransition(definition.Code, progress, values, incoming) { Shutter = shutter };
        AssertFinite(compositor, gpu, new LayerLook(Transition: transition), what);
    }

    private static void AssertFinite(Compositor compositor, GpuDevice gpu, LayerLook look, string what) =>
        AssertFinite(Render(compositor, gpu, look, LayerTransform.Identity), what);

    private static float[] Render(Compositor compositor, GpuDevice gpu, LayerLook? look, LayerTransform transform)
    {
        LayerFrame layer = look is null
            ? Layers.From(gpu, Image(3), Bt709, transform)
            : Layers.From(gpu, Image(3), Bt709, look, transform);
        using CompositionFrame frame = Layers.Frame(Width, Height, layer);
        compositor.Render(frame);
        return compositor.ReadbackRgbaFloat();
    }

    private static void AssertFinite(float[] pixels, string what)
    {
        int bad = Array.FindIndex(pixels, v => !float.IsFinite(v));
        Assert.True(bad < 0, $"{what}: el píxel ({bad / 4 % Width}, {bad / 4 / Width}) tiene {(bad < 0 ? 0 : pixels[bad])}");
    }

    /// <summary>Bloques de colores con semilla fija: bordes y tonos para los efectos que miran vecinos.</summary>
    private static YuvImage Image(int seed)
    {
        var random = new Random(seed);
        var image = new YuvImage(Width, Height, false);
        for (int y = 0; y < Height; y += 4)
        {
            for (int x = 0; x < Width; x += 4)
            {
                image.Fill(x, y, 4, 4, ReferenceColor.Encode(random.Next(256), random.Next(256), random.Next(256), Bt709, false));
            }
        }

        return image;
    }
}
