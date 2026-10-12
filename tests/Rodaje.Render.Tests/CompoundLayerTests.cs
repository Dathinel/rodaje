using Rodaje.Core.Effects;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using T = Rodaje.Core.Model.BuiltInTransitions;

namespace Rodaje.Render.Tests;

/// <summary>
/// Una capa compuesta (un clip compuesto con efectos propios) se compone en un intermedio y se dibuja como una capa más:
/// se compara con la referencia de componer los clips a mano.
/// </summary>
public sealed class CompoundLayerTests
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;
    private static readonly LayerTransform Half = LayerTransform.Identity with { Opacity = 0.5 };

    [GpuFact]
    public void Color_and_half_opacity_on_a_compound_match_them_on_its_only_clip()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] expected = Render(compositor, Layers.From(gpu, Bands(), Bt709, new LayerLook([Look()]), Half));
        byte[] actual = Render(compositor, Compound([Layers.From(gpu, Bands(), Bt709)], Half, [Look()]));

        AssertWithin(expected, actual, 1);
    }

    [GpuFact]
    public void Half_opacity_on_a_compound_of_two_layers_is_half_of_their_composition()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] inner = Render(compositor, Layers.From(gpu, Bands(), Bt709), Small(gpu));
        byte[] actual = Render(compositor, Compound([Layers.From(gpu, Bands(), Bt709), Small(gpu)], Half, []));

        AssertHalf(inner, actual);
    }

    [GpuFact]
    public void A_compound_inside_a_compound_multiplies_the_opacities()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] inner = Render(compositor, Layers.From(gpu, Bands(), Bt709), Small(gpu));
        LayerFrame twice = Compound([Compound([Layers.From(gpu, Bands(), Bt709), Small(gpu)], Half, [])], LayerTransform.Identity with { Opacity = 0.5 }, []);
        byte[] actual = Render(compositor, twice);

        for (int i = 0; i < inner.Length; i += 4)
        {
            Assert.InRange(actual[i], (inner[i] / 4.0) - 1, (inner[i] / 4.0) + 1);
        }
    }

    [GpuTheory]
    [InlineData(T.Crossfade, true)]
    [InlineData(T.Crossfade, false)]
    [InlineData(T.Wipe, true)]
    public void A_transition_with_a_compound_matches_the_same_transition_with_its_clip(string type, bool compoundLeaves)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        int code = EffectRegistry.Find(type)!.Code;
        EffectValues values = Fx.Transition(type);
        LayerFrame Clip(LayerTransition? transition = null) => Layers.From(gpu, Bands(), Bt709, new LayerLook([Look()], Transition: transition), Half);
        LayerFrame Group(LayerTransition? transition = null) =>
            Compound([Layers.From(gpu, Bands(), Bt709)], Half, [Look()], transition);

        byte[] expected = compoundLeaves
            ? Render(compositor, Clip(new LayerTransition(code, 0.4f, values, Small(gpu))))
            : Render(compositor, Layers.From(gpu, Bars(), Bt709, new LayerLook(Transition: new LayerTransition(code, 0.4f, values, Clip()))));
        byte[] actual = compoundLeaves
            ? Render(compositor, Group(new LayerTransition(code, 0.4f, values, Small(gpu))))
            : Render(compositor, Layers.From(gpu, Bars(), Bt709, new LayerLook(Transition: new LayerTransition(code, 0.4f, values, Group()))));

        AssertWithin(expected, actual, 1);
    }

    [GpuFact]
    public void An_empty_compound_draws_nothing_and_a_full_pool_skips_it()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] black = Render(compositor);
        Assert.Equal(black, Render(compositor, Compound([], Half, [])));

        using var tiny = new Compositor(gpu, Width, Height, CompositeFormat.Rgba16Float, null, poolCapBytes: 1024);
        Assert.True(tiny.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Width, Height, Compound([Layers.From(gpu, Bands(), Bt709)], Half, []));
        CompositeResult result = tiny.Render(frame);
        Assert.Equal(1, result.LayersSkipped);
        Assert.True(result.EffectsSkipped >= 1);
    }

    private static LayerFrame Compound(IReadOnlyList<LayerFrame> layers, LayerTransform transform, IReadOnlyList<LayerEffect> effects,
        LayerTransition? transition = null) =>
        new(0, "compuesto", layers, transform) { Effects = effects, Transition = transition };

    private static LayerEffect Look() => Fx.Color((C.Exposure, 0.4), (C.Saturation, -0.3));

    private static YuvImage Bands()
    {
        var image = new YuvImage(Width, Height, false);
        for (int band = 0; band < 8; band++)
        {
            image.Fill(band * 32, 0, 32, Height, ReferenceColor.Encode(30 + (band * 28), 200 - (band * 20), 60, Bt709, false));
        }

        return image;
    }

    private static YuvImage Bars()
    {
        var image = new YuvImage(Width, Height, false);
        for (int bar = 0; bar < 4; bar++)
        {
            image.Fill(0, bar * 32, Width, 32, ReferenceColor.Encode(40 + (bar * 50), 120, 220 - (bar * 50), Bt709, false));
        }

        return image;
    }

    /// <summary>Una imagen más chica centrada: deja ver lo de abajo.</summary>
    private static LayerFrame Small(GpuDevice gpu)
    {
        var image = new YuvImage(128, 128, false);
        image.Fill(0, 0, 128, 128, ReferenceColor.Encode(220, 40, 130, Bt709, false));
        return Layers.From(gpu, image, Bt709, track: 1);
    }

    private static byte[] Render(Compositor compositor, params LayerFrame[] layers)
    {
        using CompositionFrame frame = Layers.Frame(Width, Height, layers);
        CompositeResult result = compositor.Render(frame);
        Assert.Equal(0, result.EffectsSkipped);
        return compositor.ReadbackRgba();
    }

    private static void AssertWithin(byte[] expected, byte[] actual, int codes)
    {
        int worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.True(worst <= codes, $"Diferencia máxima {worst} códigos.");
    }

    /// <summary>Sobre negro opaco, la mitad de opacidad es la mitad de cada código de color.</summary>
    private static void AssertHalf(byte[] inner, byte[] actual)
    {
        for (int i = 0; i < inner.Length; i++)
        {
            if (i % 4 != 3)
            {
                Assert.InRange(actual[i], (inner[i] / 2.0) - 1, (inner[i] / 2.0) + 1);
            }
        }
    }
}
