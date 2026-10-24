using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests;

public sealed class CompositorTests
{
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<YuvMatrix, bool, bool, CompositeFormat> ColorCases()
    {
        var data = new TheoryData<YuvMatrix, bool, bool, CompositeFormat>();
        foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
        {
            foreach (bool full in new[] { false, true })
            {
                foreach (bool tenBit in new[] { false, true })
                {
                    data.Add(matrix, full, tenBit, CompositeFormat.Rgba16Float);
                }
            }

            data.Add(matrix, false, false, CompositeFormat.Rgba8Unorm);
        }

        return data;
    }

    [GpuTheory]
    [MemberData(nameof(ColorCases))]
    public void Converts_each_matrix_and_range_to_the_reference_rgb(YuvMatrix matrix, bool fullRange, bool tenBit, CompositeFormat format)
    {
        var color = new VideoColor(matrix, fullRange);
        (int R, int G, int B)[] samples =
        [
            (0, 0, 0), (255, 255, 255), (255, 0, 0), (0, 255, 0), (0, 0, 255),
            (128, 128, 128), (200, 60, 30), (20, 180, 220), (90, 40, 160), (250, 230, 10),
        ];
        // Una franja de 64 px por color, en una sola textura: cada franja es uniforme lejos de sus bordes.
        var image = new YuvImage(64 * samples.Length, 64, tenBit);
        var codes = new YuvCode[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            codes[i] = ReferenceColor.Encode(samples[i].R, samples[i].G, samples[i].B, color, tenBit);
            image.Fill(i * 64, 0, 64, 64, codes[i]);
        }

        using GpuDevice gpu = GpuDevice.Create();
        RgbaImage result = Compose(gpu, image.Width, image.Height, format, Layers.From(gpu, image, color));

        for (int i = 0; i < samples.Length; i++)
        {
            Rgb expected = ReferenceColor.Decode(codes[i], color, tenBit);
            result.AssertColor((i * 64) + 32, 32, expected);
            result.AssertColor((i * 64) + 8, 60, expected);
        }
    }

    [GpuFact]
    public void Limited_range_bt709_primaries_land_on_the_nominal_values()
    {
        using GpuDevice gpu = GpuDevice.Create();
        // Códigos publicados de las barras de color BT.709 al 100 %: rojo (63, 102, 240), verde (173, 42, 26), azul (32, 240, 118).
        var image = new YuvImage(192, 64, tenBit: false)
            .Fill(0, 0, 64, 64, new YuvCode(63, 102, 240))
            .Fill(64, 0, 64, 64, new YuvCode(173, 42, 26))
            .Fill(128, 0, 64, 64, new YuvCode(32, 240, 118));
        RgbaImage result = Compose(gpu, 192, 64, CompositeFormat.Rgba16Float, Layers.From(gpu, image, Bt709));

        result.AssertColor(32, 32, new Rgb(255, 0, 0), 3);
        result.AssertColor(96, 32, new Rgb(0, 255, 0), 3);
        result.AssertColor(160, 32, new Rgb(0, 0, 255), 3);
    }

    [GpuFact]
    public void Empty_frame_is_opaque_black()
    {
        using GpuDevice gpu = GpuDevice.Create();
        RgbaImage result = Compose(gpu, 320, 180, CompositeFormat.Rgba16Float);
        for (int y = 0; y < 180; y += 7)
        {
            for (int x = 0; x < 320; x += 7)
            {
                result.AssertBlack(x, y);
            }
        }
    }

    [GpuFact]
    public void Top_layer_covers_the_bottom_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode red = ReferenceColor.Encode(220, 20, 20, Bt709, false);
        YuvCode blue = ReferenceColor.Encode(20, 20, 220, Bt709, false);
        LayerFrame bottom = Layers.From(gpu, YuvImage.Solid(1920, 1080, false, red), Bt709, track: 0);
        LayerFrame top = Layers.From(gpu, YuvImage.Solid(100, 100, false, blue), Bt709, new LayerTransform(0, 0, 100 / 1080.0, 0, 1), track: 1);

        RgbaImage result = Compose(gpu, 1920, 1080, CompositeFormat.Rgba16Float, bottom, top);

        result.AssertColor(960, 540, ReferenceColor.Decode(blue, Bt709, false));
        result.AssertColor(100, 100, ReferenceColor.Decode(red, Bt709, false));
        result.AssertColor(909, 540, ReferenceColor.Decode(red, Bt709, false));
        result.AssertColor(910, 540, ReferenceColor.Decode(blue, Bt709, false));
    }

    [GpuTheory]
    [InlineData(CompositeFormat.Rgba16Float)]
    [InlineData(CompositeFormat.Rgba8Unorm)]
    public void Half_opacity_mixes_both_layers_evenly(CompositeFormat format)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode red = ReferenceColor.Encode(200, 30, 10, Bt709, false);
        YuvCode blue = ReferenceColor.Encode(10, 60, 230, Bt709, false);
        LayerFrame bottom = Layers.From(gpu, YuvImage.Solid(64, 64, false, red), Bt709, track: 0);
        LayerFrame top = Layers.From(gpu, YuvImage.Solid(64, 64, false, blue), Bt709, LayerTransform.Identity with { Opacity = 0.5 }, track: 1);

        RgbaImage result = Compose(gpu, 64, 64, format, bottom, top);

        Rgb expected = Rgb.Mix(ReferenceColor.Decode(red, Bt709, false), ReferenceColor.Decode(blue, Bt709, false), 0.5);
        result.AssertColor(32, 32, expected);
    }

    [GpuFact]
    public void Zero_opacity_draws_nothing()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(235, 128, 128)), Bt709,
            LayerTransform.Identity with { Opacity = 0 });
        using var compositor = new Compositor(gpu, 64, 64);
        using CompositionFrame frame = Layers.Frame(64, 64, layer);

        Assert.Equal(new CompositeResult(0, 0), compositor.Render(frame));
        new RgbaImage(compositor.ReadbackRgba(), 64, 64).AssertBlack(32, 32);
    }

    [GpuFact]
    public void Translated_layer_lands_on_the_expected_pixels()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode white = new(235, 128, 128);
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(100, 100, false, white), Bt709, new LayerTransform(300, -200, 100 / 1080.0, 0, 1));

        RgbaImage result = Compose(gpu, 1920, 1080, CompositeFormat.Rgba16Float, layer);

        // Centro en (960 + 300, 540 − 200): el cuadrado ocupa x 1210..1309 e y 290..389.
        var inside = new Rgb(255, 255, 255);
        foreach ((int x, int y) in new[] { (1210, 290), (1309, 290), (1210, 389), (1309, 389), (1260, 340) })
        {
            result.AssertColor(x, y, inside);
        }

        foreach ((int x, int y) in new[] { (1209, 300), (1310, 300), (1250, 289), (1250, 390), (960, 540) })
        {
            result.AssertBlack(x, y);
        }
    }

    [GpuFact]
    public void A_smaller_compositor_renders_a_scaled_preview_of_the_sequence()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(100, 100, false, new YuvCode(235, 128, 128)), Bt709,
            new LayerTransform(300, -200, 100 / 1080.0, 0, 1));
        using var compositor = new Compositor(gpu, 960, 540);
        using CompositionFrame frame = Layers.Frame(1920, 1080, layer);
        compositor.Render(frame);
        var result = new RgbaImage(compositor.ReadbackRgba(), 960, 540);

        result.AssertColor(605, 145, new Rgb(255, 255, 255));
        result.AssertColor(654, 194, new Rgb(255, 255, 255));
        result.AssertBlack(604, 170);
        result.AssertBlack(655, 170);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rotating_a_quarter_turn_puts_the_left_half_on_top(bool fromMetadata)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode red = ReferenceColor.Encode(230, 20, 20, Bt709, false);
        YuvCode blue = ReferenceColor.Encode(20, 20, 230, Bt709, false);
        YuvImage image = new YuvImage(200, 100, false).Fill(0, 0, 100, 100, red).Fill(100, 0, 100, 100, blue);
        LayerTransform transform = fromMetadata ? LayerTransform.Identity : LayerTransform.Identity with { RotationDegrees = 90 };
        LayerFrame layer = Layers.From(gpu, image, Bt709, transform);
        SourceGeometry geometry = fromMetadata ? SourceGeometry.Square with { Rotation = 90 } : SourceGeometry.Square;

        RgbaImage result = Compose(gpu, 400, 400, CompositeFormat.Rgba16Float, geometry, layer);

        // Sin metadatos: 400×200 ajustado y girado queda de 200×400. Con metadatos: 100×200 ajustado a 200×400. Igual resultado.
        result.AssertColor(150, 100, ReferenceColor.Decode(red, Bt709, false));
        result.AssertColor(250, 20, ReferenceColor.Decode(red, Bt709, false));
        result.AssertColor(150, 300, ReferenceColor.Decode(blue, Bt709, false));
        result.AssertColor(250, 380, ReferenceColor.Decode(blue, Bt709, false));
        result.AssertBlack(50, 200);
        result.AssertBlack(350, 200);
        result.AssertBlack(99, 100);
        result.AssertBlack(300, 300);
    }

    [GpuFact]
    public void Sample_aspect_ratio_widens_the_image()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(100, 100, false, new YuvCode(235, 128, 128)), Bt709);
        var geometry = new SourceGeometry(new Rational(2, 1), 0);

        RgbaImage result = Compose(gpu, 400, 400, CompositeFormat.Rgba16Float, geometry, layer);

        // 100×100 con píxeles 2:1 se ve de 200×100: ajustado a 400×200, centrado en vertical.
        result.AssertBlack(200, 99);
        result.AssertColor(200, 100, new Rgb(255, 255, 255));
        result.AssertColor(2, 200, new Rgb(255, 255, 255));
        result.AssertColor(200, 299, new Rgb(255, 255, 255));
        result.AssertBlack(200, 300);
    }

    [GpuFact]
    public void A_4x3_source_is_pillarboxed_in_a_16x9_sequence()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(640, 480, false, new YuvCode(235, 128, 128)), Bt709);

        RgbaImage result = Compose(gpu, 1920, 1080, CompositeFormat.Rgba16Float, layer);

        var white = new Rgb(255, 255, 255);
        result.AssertBlack(239, 540);
        result.AssertColor(240, 540, white);
        result.AssertColor(1679, 540, white);
        result.AssertBlack(1680, 540);
        result.AssertColor(960, 0, white);
        result.AssertColor(960, 1079, white);
    }

    [GpuArrayFact]
    public void Draws_the_requested_slice_of_a_texture_array()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode[] codes =
        [
            ReferenceColor.Encode(250, 10, 10, Bt709, false),
            ReferenceColor.Encode(10, 250, 10, Bt709, false),
            ReferenceColor.Encode(10, 10, 250, Bt709, false),
        ];
        YuvImage[] slices = [.. codes.Select(c => YuvImage.Solid(64, 64, false, c))];

        for (int slice = 0; slice < slices.Length; slice++)
        {
            LayerFrame layer = Layers.From(gpu, slices, slice, 64, 64, Bt709, LayerTransform.Identity);
            RgbaImage result = Compose(gpu, 64, 64, CompositeFormat.Rgba16Float, layer);
            result.AssertColor(32, 32, ReferenceColor.Decode(codes[slice], Bt709, false));
        }
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_decoder_padding_never_reaches_the_image(bool tenBit)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode gray = ReferenceColor.Encode(120, 120, 120, Bt709, tenBit);
        YuvCode green = ReferenceColor.Encode(0, 255, 0, Bt709, tenBit);
        // Visible 100×100 dentro de una textura de 128×112 con el relleno en verde puro.
        YuvImage image = YuvImage.Solid(128, 112, tenBit, green).Fill(0, 0, 100, 100, gray);
        LayerFrame layer = Layers.From(gpu, [image], 0, 100, 100, Bt709, LayerTransform.Identity);

        RgbaImage result = Compose(gpu, 400, 400, CompositeFormat.Rgba16Float, layer);

        Rgb expected = ReferenceColor.Decode(gray, Bt709, tenBit);
        foreach ((int x, int y) in new[] { (399, 200), (200, 399), (399, 399), (0, 0), (398, 398) })
        {
            result.AssertColor(x, y, expected);
        }
    }

    [GpuFact]
    public void A_1080p_frame_in_a_1088_line_surface_keeps_its_last_row()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode gray = ReferenceColor.Encode(90, 90, 90, Bt709, false);
        YuvImage image = YuvImage.Solid(1920, 1088, false, new YuvCode(235, 16, 16)).Fill(0, 0, 1920, 1080, gray);
        LayerFrame layer = Layers.From(gpu, [image], 0, 1920, 1080, Bt709, LayerTransform.Identity);

        RgbaImage result = Compose(gpu, 1920, 1080, CompositeFormat.Rgba16Float, layer);

        Rgb expected = ReferenceColor.Decode(gray, Bt709, false);
        result.AssertColor(960, 1079, expected);
        result.AssertColor(0, 1079, expected);
        result.AssertColor(1919, 1079, expected);
    }

    [GpuFact]
    public void Rotated_edges_are_antialiased()
    {
        using GpuDevice gpu = GpuDevice.Create();
        LayerFrame layer = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(235, 128, 128)), Bt709,
            new LayerTransform(0, 0, 0.5, 45, 1));

        RgbaImage result = Compose(gpu, 400, 400, CompositeFormat.Rgba16Float, layer);

        // Rombo de semidiagonal 141,4 px centrado en (200, 200). El píxel (270, 129) queda a 0,3 px dentro del lado.
        result.AssertColor(200, 200, new Rgb(255, 255, 255));
        byte edge = result.At(270, 129).G;
        Assert.InRange(edge, 150, 240);
        result.AssertBlack(272, 127);
    }

    [GpuFact]
    public void Layers_without_a_usable_texture_are_skipped()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var cpuOnly = new LayerFrame(0, "sin-textura", new DecodedFrame(0, Time.Zero, Time.FromTicks(1), null, 0), LayerTransform.Identity);
        LayerFrame released = Layers.From(gpu, YuvImage.Solid(64, 64, false, new YuvCode(235, 128, 128)), Bt709);
        ((D3D11TexturePayload)released.Frame.Payload!).Dispose();
        using var compositor = new Compositor(gpu, 64, 64);
        using CompositionFrame frame = Layers.Frame(64, 64, cpuOnly, released);

        Assert.Equal(new CompositeResult(0, 2), compositor.Render(frame));
        new RgbaImage(compositor.ReadbackRgba(), 64, 64).AssertBlack(32, 32);
    }

    [GpuFact]
    public void A_texture_from_another_device_is_rejected()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using GpuDevice other = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 64, 64);
        using CompositionFrame frame = Layers.Frame(64, 64, Layers.From(other, YuvImage.Solid(64, 64, false, new YuvCode(235, 128, 128)), Bt709));

        Assert.Throws<ArgumentException>(() => compositor.Render(frame));
    }

    /// <summary>El visor de 10 bits (R10G10B10A2) y el de 8 (B8G8R8A8) reciben la misma imagen, también en WARP.</summary>
    [Fact]
    public void Presenter_draws_into_8_and_10_bit_targets_on_warp()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        YuvCode gray = ReferenceColor.Encode(150, 150, 150, Bt709, false);
        using var compositor = new Compositor(gpu, 64, 36);
        using (CompositionFrame frame = Layers.Frame(64, 36, Layers.From(gpu, YuvImage.Solid(64, 36, false, gray), Bt709)))
        {
            compositor.Render(frame);
        }

        using var presenter = new Presenter(gpu);
        Rgb expected = ReferenceColor.Decode(gray, Bt709, false);
        foreach (DXGI_FORMAT format in new[] { DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM })
        {
            using var target = new OffscreenTarget(gpu, 64, 36, format);
            Assert.Equal(new PixelRect(0, 0, 64, 36), presenter.Draw(target.RenderTargetView, 64, 36, compositor));
            target.Read().AssertColor(32, 18, expected);
        }
    }

    [GpuFact]
    public void Presenter_letterboxes_the_composition_in_the_back_buffer()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvCode gray = ReferenceColor.Encode(150, 150, 150, Bt709, false);
        using var compositor = new Compositor(gpu, 1920, 1080);
        using (CompositionFrame frame = Layers.Frame(1920, 1080, Layers.From(gpu, YuvImage.Solid(1920, 1080, false, gray), Bt709)))
        {
            compositor.Render(frame);
        }

        using var presenter = new Presenter(gpu);
        Rgb expected = ReferenceColor.Decode(gray, Bt709, false);
        foreach ((int width, int height, PixelRect rect) in new[]
        {
            (800, 800, new PixelRect(0, 175, 800, 450)),
            (2400, 1200, new PixelRect(133, 0, 2133, 1200)),
        })
        {
            using var target = new OffscreenTarget(gpu, width, height);
            Assert.Equal(rect, presenter.Draw(target.RenderTargetView, width, height, compositor));
            RgbaImage shown = target.Read();
            int midX = rect.X + (rect.Width / 2);
            int midY = rect.Y + (rect.Height / 2);
            shown.AssertColor(midX, midY, expected);
            shown.AssertColor(rect.X, rect.Y, expected);
            shown.AssertColor(rect.X + rect.Width - 1, rect.Y + rect.Height - 1, expected);
            if (rect.Y > 0)
            {
                shown.AssertBlack(midX, rect.Y - 1);
                shown.AssertBlack(midX, rect.Y + rect.Height);
            }
            else
            {
                shown.AssertBlack(rect.X - 1, midY);
                shown.AssertBlack(rect.X + rect.Width, midY);
            }
        }
    }

    [GpuFact]
    public void Gpu_timer_measures_a_composition()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 1920, 1080);
        using var timer = new GpuTimer(gpu);
        using CompositionFrame frame = Layers.Frame(1920, 1080, Layers.From(gpu, YuvImage.Solid(1920, 1080, false, new YuvCode(100, 120, 140)), Bt709));
        double? ms = null;
        for (int attempt = 0; attempt < 5 && ms is null; attempt++)
        {
            timer.Begin();
            compositor.Render(frame);
            timer.End();
            ms = timer.WaitMilliseconds(TimeSpan.FromSeconds(2));
        }

        Assert.NotNull(ms);
        // Milisegundos y no tics: un 1080p con WARP en la CI cargada llegó a 206 ms (con la cota de 100 fallaba); en tics
        // del reloj de la GPU daría millones.
        Assert.InRange(ms.Value, 0.0001, 5000);
    }

    private static RgbaImage Compose(GpuDevice gpu, int width, int height, CompositeFormat format, params LayerFrame[] layers) =>
        Compose(gpu, width, height, format, SourceGeometry.Square, layers);

    private static RgbaImage Compose(GpuDevice gpu, int width, int height, CompositeFormat format, SourceGeometry geometry, params LayerFrame[] layers)
    {
        using var compositor = new Compositor(gpu, width, height, format);
        using CompositionFrame frame = Layers.Frame(width, height, layers);
        CompositeResult result = compositor.Render(frame, _ => geometry);
        Assert.Equal(layers.Count(l => l.Transform.Opacity > 0), result.LayersDrawn);
        return new RgbaImage(compositor.ReadbackRgba(), width, height);
    }
}
