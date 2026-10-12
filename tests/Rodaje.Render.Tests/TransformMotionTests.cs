using System.Numerics;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Ancla, escala por eje, inclinación, volteo y desenfoque de movimiento contra una referencia propia en la CPU: la
/// cobertura de cada muestra calculada píxel a píxel con la misma regla del borde y promediada. En la GPU de verdad y en
/// WARP (el rasterizador de software, como en las máquinas sin GPU).
/// </summary>
public sealed class TransformMotionTests
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<bool> Devices => new() { false, true };

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void Horizontal_motion_blur_is_the_average_of_its_samples(bool warp)
    {
        LayerTransform[] samples = [.. Enumerable.Range(0, 8).Select(k => Small with { X = -40 + (k * 80 / 7.0) })];
        AssertMatchesReference(warp, samples, Small);
    }

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void Spin_with_changing_opacity_is_the_average_of_its_samples(bool warp)
    {
        LayerTransform[] samples = [.. Enumerable.Range(0, 16).Select(k => Small with { RotationDegrees = k * 6, Opacity = 0.5 + (k / 30.0), Y = k })];
        AssertMatchesReference(warp, samples, Small);
    }

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void A_skewed_stretched_layer_with_an_anchor_covers_what_the_reference_says(bool warp)
    {
        LayerTransform shaped = Small with
        {
            RotationDegrees = 20,
            Shape = new LayerShape(30, -10, 1.6, 0.7, 25, -10, false, false),
        };
        AssertMatchesReference(warp, [shaped], shaped);
    }

    [GpuFact]
    public void Flipping_mirrors_the_image_around_its_center()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] flipped = Render(compositor, Layers.From(gpu, Bands(false), Bt709, LayerTransform.Identity with
        {
            Shape = LayerShape.None with { FlipX = true },
        }));
        byte[] mirrored = Render(compositor, Layers.From(gpu, Bands(true), Bt709));
        AssertWithin(mirrored, flipped, 1);

        // Volteado en los dos ejes y con el ancla corrida: gira 180° alrededor del centro de la imagen, no del ancla.
        byte[] both = Render(compositor, Layers.From(gpu, Bands(false), Bt709, LayerTransform.Identity with
        {
            Shape = LayerShape.None with { FlipX = true, FlipY = true, AnchorX = 50, AnchorY = 20 },
        }));
        byte[] turned = Render(compositor, Layers.From(gpu, Bands(false), Bt709, LayerTransform.Identity with { RotationDegrees = 180 }));
        AssertWithin(turned, both, 1);
    }

    [GpuFact]
    public void Preview_and_export_compose_the_same_blurred_layer()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        LayerFrame Layer() => Layers.From(gpu, Solid(64, 32), Bt709, new LayerLook(Blend: BlendMode.Screen), Small);
        LayerTransform[] samples = [.. Enumerable.Range(0, 12).Select(k => Small with { X = k * 5, RotationDegrees = k })];

        byte[] preview;
        using (CompositionFrame frame = Layers.Frame(Width, Height, Background(gpu), WithSamples(Layer(), samples)))
        {
            CompositeResult result = compositor.Render(frame);
            Assert.Equal(0, result.EffectsSkipped);
            preview = compositor.ReadbackRgba();
        }

        using (CompositionFrame frame = Layers.Frame(Width, Height, Background(gpu), WithSamples(Layer(), samples)))
        {
            compositor.RenderStrict(frame);
        }

        Assert.Equal(preview, compositor.ReadbackRgba());
    }

    private static readonly LayerTransform Small = LayerTransform.Identity with { Scale = 0.3 };

    private static void AssertMatchesReference(bool warp, LayerTransform[] samples, LayerTransform still)
    {
        using GpuDevice gpu = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);

        // El color de la capa lo da la GPU en un píxel cubierto del todo: la referencia mide la cobertura, no la conversión.
        byte[] opaque = Render(compositor, Layers.From(gpu, Solid(64, 32), Bt709));
        (int r, int g, int b) = (opaque[0], opaque[1], opaque[2]);

        LayerFrame layer = samples.Length > 1
            ? WithSamples(Layers.From(gpu, Solid(64, 32), Bt709, still), samples)
            : Layers.From(gpu, Solid(64, 32), Bt709, still);
        byte[] actual = Render(compositor, layer);
        double[] alpha = ReferenceAlpha(64, 32, samples);
        int worst = 0;
        double covered = 0;
        for (int i = 0; i < alpha.Length; i++)
        {
            covered += alpha[i];
            worst = Math.Max(worst, Math.Abs(actual[i * 4] - (int)Math.Round(r * alpha[i])));
            worst = Math.Max(worst, Math.Abs(actual[(i * 4) + 1] - (int)Math.Round(g * alpha[i])));
            worst = Math.Max(worst, Math.Abs(actual[(i * 4) + 2] - (int)Math.Round(b * alpha[i])));
        }

        Assert.True(covered > 500, "La capa no se ve.");
        Assert.True(worst <= 2, $"Diferencia máxima {worst} códigos ({gpu.AdapterName}).");
    }

    /// <summary>La cobertura de cada muestra con la regla del shader (distancia al lado en píxeles de destino + ½), promediada.</summary>
    private static double[] ReferenceAlpha(int imageWidth, int imageHeight, LayerTransform[] samples)
    {
        var alpha = new double[Width * Height];
        foreach (LayerTransform sample in samples)
        {
            LayerPlacement placement = LayerLayout.Place(Width, Height, imageWidth, imageHeight, sample)!.Value;
            Assert.True(Matrix3x2.Invert(placement.LocalToSequence, out Matrix3x2 inverse));
            double edgeX = placement.EdgeScaleX;
            double edgeY = placement.EdgeScaleY;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Vector2 q = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), inverse);
                    double insideX = Math.Min(q.X, placement.LocalWidth - q.X) * edgeX;
                    double insideY = Math.Min(q.Y, placement.LocalHeight - q.Y) * edgeY;
                    alpha[(y * Width) + x] += Math.Clamp(Math.Min(insideX, insideY) + 0.5, 0, 1) * Math.Min(sample.Opacity, 1) / samples.Length;
                }
            }
        }

        return alpha;
    }

    private static LayerFrame WithSamples(LayerFrame layer, LayerTransform[] samples) =>
        new(layer.TrackIndex, layer.ClipId, layer.Frame, layer.Transform, layer.SampleAspectRatio, layer.Rotation)
        {
            Effects = layer.Effects,
            Blend = layer.Blend,
            MotionSamples = samples,
        };

    private static YuvImage Solid(int width, int height) =>
        YuvImage.Solid(width, height, false, ReferenceColor.Encode(200, 120, 40, Bt709, false));

    private static LayerFrame Background(GpuDevice gpu) =>
        Layers.From(gpu, YuvImage.Solid(Width, Height, false, ReferenceColor.Encode(40, 60, 120, Bt709, false)), Bt709, track: 0);

    private static YuvImage Bands(bool mirrored)
    {
        var image = new YuvImage(Width, Height, false);
        for (int band = 0; band < 8; band++)
        {
            int index = mirrored ? 7 - band : band;
            image.Fill(band * 32, 0, 32, Height / 2, ReferenceColor.Encode(30 + (index * 28), 200 - (index * 20), 60, Bt709, false));
            image.Fill(band * 32, Height / 2, 32, Height / 2, ReferenceColor.Encode(220 - (index * 20), 40 + (index * 25), 160, Bt709, false));
        }

        return image;
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
}
