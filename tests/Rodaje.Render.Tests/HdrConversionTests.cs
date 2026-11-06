using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Export;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.Render.Tests.Export;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests;

public sealed class HdrConversionTests
{
    // Referencias calculadas en doble precisión desde los códigos YCbCr, con las ecuaciones de BT.2100/BT.709.
    [GpuTheory]
    [InlineData(HdrTransfer.Pq, 400, 512, 512, 74.764, 74.764, 74.764)]
    [InlineData(HdrTransfer.Pq, 500, 480, 550, 188.906, 95.007, 72.173)]
    [InlineData(HdrTransfer.Hlg, 400, 512, 512, 74.427, 74.427, 74.427)]
    [InlineData(HdrTransfer.Hlg, 500, 480, 550, 126.960, 92.688, 82.357)]
    public void Midtones_and_color_match_reference_vectors(HdrTransfer transfer, int y, int cb, int cr, double r, double g, double b)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = YuvImage.Solid(16, 16, true, new YuvCode(y, cb, cr));
        var pixels = new RgbaImage(Render(gpu, image, Color(transfer), 1000), 16, 16);
        pixels.AssertColor(8, 8, new Rgb(r, g, b), 1);
    }

    [GpuTheory]
    [InlineData(HdrTransfer.Pq, 100)]
    [InlineData(HdrTransfer.Pq, 1000)]
    [InlineData(HdrTransfer.Hlg, 1000)]
    [InlineData(HdrTransfer.Hlg, 2000)]
    public void Reference_white_is_Sdr_white_and_black_stays_black(HdrTransfer transfer, double white)
    {
        using GpuDevice gpu = GpuDevice.Create();
        double encoded = transfer == HdrTransfer.Pq ? Pq(white) : 1;
        var image = YuvImage.Solid(32, 16, true, new YuvCode(64, 512, 512));
        image.Fill(16, 0, 16, 16, new YuvCode((int)Math.Round(64 + 876 * encoded), 512, 512));
        byte[] result = Render(gpu, image, Color(transfer), white);
        var pixels = new RgbaImage(result, 32, 16);
        pixels.AssertBlack(4, 8);
        pixels.AssertColor(24, 8, new Rgb(255, 255, 255), 1);
    }

    [GpuTheory]
    [InlineData(HdrTransfer.Pq)]
    [InlineData(HdrTransfer.Hlg)]
    public void Preview_and_export_match_with_and_without_effects(HdrTransfer transfer)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(true);
        foreach (bool effects in new[] { false, true })
        {
            byte[] preview = Render(gpu, image, Color(transfer), 0, effects: effects);
            byte[] export = Render(gpu, image, Color(transfer), 0, strict: true, effects: effects);
            Assert.Equal(preview, export);
            Assert.NotEqual(Render(gpu, image, Color(HdrTransfer.None), 0, effects: effects), preview);
        }
    }

    [GpuTheory]
    [InlineData(HdrTransfer.Pq, 100)]
    [InlineData(HdrTransfer.Hlg, 1000)]
    public void Automatic_reference_matches_the_transfer_default(HdrTransfer transfer, double white)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(true);
        Assert.Equal(Render(gpu, image, Color(transfer), white), Render(gpu, image, Color(transfer), 0));
        Assert.NotEqual(Render(gpu, image, Color(transfer), white * 2), Render(gpu, image, Color(transfer), white));
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sdr_bytes_ignore_hdr_controls_and_primaries(bool tenBit)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(tenBit);
        byte[] original = Render(gpu, image, VideoColor.Bt709Limited, 0);
        Assert.Equal(original, Render(gpu, image, VideoColor.Bt709Limited with { Bt2020Primaries = true }, 800));
        Assert.Equal(original, Render(gpu, image, VideoColor.Bt709Limited, 5000, strict: true));
        foreach (YuvMatrix matrix in Enum.GetValues<YuvMatrix>())
        {
            foreach (bool full in new[] { false, true })
            {
                var color = new VideoColor(matrix, full);
                Assert.Equal(RenderShader(gpu, image, color, LegacySdrShaders.Layer), RenderShader(gpu, image, color, RenderShaders.Layer));
            }
        }
    }

    [GpuTheory]
    [InlineData(HdrTransfer.Pq)]
    [InlineData(HdrTransfer.Hlg)]
    public void Bt2020_primaries_change_colored_pixels(HdrTransfer transfer)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage image = Scene(true);
        VideoColor color = Color(transfer);
        Assert.NotEqual(Render(gpu, image, color, 1000), Render(gpu, image, color with { Bt2020Primaries = false }, 1000));
    }

    private static VideoColor Color(HdrTransfer transfer) => new(YuvMatrix.Bt2020, false)
    {
        Transfer = transfer,
        Bt2020Primaries = true,
    };

    private static unsafe byte[] RenderShader(GpuDevice gpu, YuvImage image, VideoColor color, byte[] shaderBytes)
    {
        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        using var target = new OffscreenTarget(gpu, image.Width, image.Height);
        using var pipeline = new QuadPipeline(device, context);
        using var views = new PlaneViewCache(device);
        using LayerFrame layer = Layers.From(gpu, image, color);
        var payload = (D3D11TexturePayload)layer.Frame.Payload!;
        ID3D11PixelShader* shader;
        fixed (byte* bytes = shaderBytes)
        {
            RenderException.Check(device->CreatePixelShader(bytes, (nuint)shaderBytes.Length, null, &shader), "CreatePixelShader(regresión SDR)");
        }
        gpu.LockContext();
        PlaneViews planes = views.Get(payload, out bool owned);
        try
        {
            var placement = new LayerPlacement(Matrix3x2.Identity, image.Width, image.Height, 1);
            QuadConstants quad = LayerRenderer.LayerQuad(payload, placement, new SourceGeometry(new Rational(1, 1), 0), 1, image.Width, image.Height, 1);
            pipeline.Bind((ID3D11RenderTargetView*)target.RenderTargetView, image.Width, image.Height, shader, null);
            ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2] { planes.Luma, planes.Chroma };
            context->PSSetShaderResources(0, 2, inputs);
            pipeline.Draw(quad);
        }
        finally
        {
            pipeline.Unbind();
            if (owned)
            {
                planes.Release();
            }
            shader->Release();
            gpu.UnlockContext();
        }
        return target.Read().Pixels;
    }

    private static double Pq(double nits)
    {
        double p = Math.Pow(nits / 10000, 2610.0 / 16384);
        return Math.Pow((3424.0 / 4096 + 2413.0 / 128 * p) / (1 + 2392.0 / 128 * p), 2523.0 / 32);
    }

    private static YuvImage Scene(bool tenBit)
    {
        var image = new YuvImage(64, 32, tenBit);
        int scale = tenBit ? 4 : 1;
        var random = new Random(52);
        for (int y = 0; y < 32; y += 8)
        {
            for (int x = 0; x < 64; x += 8)
            {
                image.Fill(x, y, 8, 8, new YuvCode(random.Next(16, 236) * scale,
                    random.Next(96, 160) * scale, random.Next(96, 160) * scale));
            }
        }
        return image;
    }

    private static byte[] Render(GpuDevice gpu, YuvImage image, VideoColor color, double white, bool strict = false, bool effects = false)
    {
        using var compositor = new Compositor(gpu, image.Width, image.Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        LayerLook look = effects ? new LayerLook([Fx.Color((BuiltInEffects.ColorParameters.Contrast, 0.15)),
            Fx.Make(BuiltInEffects.Blur, null, (BuiltInEffects.BlurParameters.Radius, 2))]) : new LayerLook();
        using CompositionFrame frame = Layers.Frame(image.Width, image.Height,
            Layers.From(gpu, image, color, look, LayerTransform.Identity with { HdrReferenceNits = white }));
        if (strict)
        {
            compositor.RenderStrict(frame);
            var format = new ExportVideoFormat(image.Width, image.Height, Rational.Fps30, ExportPixelFormat.Rgba, ExportColor.Srgb);
            using var converter = new ExportFrameConverter(gpu, image.Width, image.Height, format) { Dither = false };
            converter.Submit(compositor.Texture, 0);
            byte[] exported = Array.ConvertAll(ExportCheck.ReadOne(converter, 0)[0], value => (byte)value);
            Assert.Equal(compositor.ReadbackRgba(), exported);
            return exported;
        }
        else
        {
            Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
        }
        return compositor.ReadbackRgba();
    }
}
