using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Export;
using Rodaje.Render.Tests.Export;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests;

/// <summary>
/// Fase C del HDR, parte 2 (D-055): en una secuencia HDR el compositor trabaja en luz lineal BT.2020 con 1,0 = 203 nits y sin
/// recortar por arriba; una secuencia SDR sigue dando los mismos bytes de antes, también en el presentador.
/// </summary>
public sealed class HdrLinearCompositorTests
{
    private static readonly VideoColor Pq2020 = new(YuvMatrix.Bt2020, false) { Transfer = HdrTransfer.Pq, Bt2020Primaries = true };

    [GpuTheory]
    [InlineData(SequenceColorSpace.HdrPq, 573)]
    [InlineData(SequenceColorSpace.HdrHlg, 721)]
    public void Sdr_white_in_an_hdr_sequence_exports_the_code_of_203_nits(SequenceColorSpace space, int expected)
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage white = YuvImage.Solid(16, 16, false, new YuvCode(235, 128, 128));
        int[] luma = ExportLuma(gpu, space, Layers.From(gpu, white, VideoColor.Bt709Limited));
        Assert.InRange(luma[0], expected - 2, expected + 2);
    }

    [GpuFact]
    public void A_1000_nit_pq_patch_survives_in_a_pq_sequence_and_clips_in_an_sdr_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        // 1000 nits en PQ ≈ 0,752 → 64 + 876 × 0,752 ≈ 723 en 10 bits limitado.
        YuvImage patch = YuvImage.Solid(16, 16, true, new YuvCode(723, 512, 512));
        int[] linear = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, patch, Pq2020));
        int[] sdr = ExportLuma(gpu, SequenceColorSpace.Sdr709, Layers.From(gpu, patch, Pq2020));
        Assert.InRange(linear[0], 720, 726);
        Assert.InRange(sdr[0], 500, 575);
    }

    [GpuFact]
    public void Layers_above_white_are_not_clipped_by_grading_in_an_hdr_sequence()
    {
        using GpuDevice gpu = GpuDevice.Create();
        YuvImage patch = YuvImage.Solid(16, 16, true, new YuvCode(723, 512, 512));
        var look = new LayerLook([Fx.Color((BuiltInEffects.ColorParameters.Contrast, 0.1))]);
        int[] luma = ExportLuma(gpu, SequenceColorSpace.HdrPq, Layers.From(gpu, patch, Pq2020, look));
        Assert.True(luma[0] > 650, $"El color recortó el parche a {luma[0]}.");
    }

    [GpuFact]
    public void Sdr_present_bytes_are_the_same_as_before_hdr()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 64, 32);
        using CompositionFrame frame = Layers.Frame(64, 32, Layers.From(gpu, Scene(), VideoColor.Bt709Limited));
        compositor.Render(frame);
        Assert.Equal(Present(gpu, compositor, LegacySdrShaders.Present), Present(gpu, compositor, null));
    }

    [GpuFact]
    public void Hdr_preview_without_an_hdr_screen_matches_the_sdr_export_of_the_same_sequence()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 64, 32) { ColorSpace = SequenceColorSpace.HdrPq };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        YuvImage scene = Scene();
        scene.Fill(0, 0, 16, 16, new YuvCode(723, 512, 512));
        using CompositionFrame frame = Layers.Frame(64, 32, Layers.From(gpu, scene, Pq2020));
        compositor.RenderStrict(frame);
        byte[] preview = compositor.ReadbackRgba();
        byte[] presented = Present(gpu, compositor, null);

        var format = new ExportVideoFormat(64, 32, Rational.Fps30, ExportPixelFormat.Rgba, ExportColor.Srgb);
        using var converter = new ExportFrameConverter(gpu, 64, 32, format, source: SequenceColorSpace.HdrPq) { Dither = false };
        converter.Submit(compositor.Texture, 0);
        byte[] exported = Array.ConvertAll(ExportCheck.ReadOne(converter, 0)[0], value => (byte)value);

        Assert.Equal(preview.Length, exported.Length);
        Assert.True(Worst(preview, exported) <= 1, $"Vista previa y exportación SDR difieren en {Worst(preview, exported)}.");
        Assert.True(Worst(presented, exported) <= 1, $"Presentador y exportación SDR difieren en {Worst(presented, exported)}.");
        // El parche de 1000 nits se recorta a blanco en SDR; el resto de la escena no.
        Assert.Equal(255, preview[0]);
        Assert.NotEqual(255, preview[(20 * 4) + 1]);
    }

    private static int Worst(byte[] a, byte[] b)
    {
        int worst = 0;
        for (int i = 0; i < a.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        }

        return worst;
    }

    /// <summary>Luma P010 de la primera fila tras componer <paramref name="layer"/> en una secuencia <paramref name="space"/> y exportarla con el color de esa secuencia.</summary>
    private static int[] ExportLuma(GpuDevice gpu, SequenceColorSpace space, LayerFrame layer)
    {
        using var compositor = new Compositor(gpu, 16, 16) { ColorSpace = space };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(16, 16, layer);
        Assert.Equal(0, compositor.RenderStrict(frame).EffectsSkipped);
        ExportColor color = space == SequenceColorSpace.HdrHlg ? ExportColor.Hlg : ExportColor.Hdr10;
        var format = new ExportVideoFormat(16, 16, Rational.Fps30, ExportPixelFormat.P010, color);
        using var converter = new ExportFrameConverter(gpu, 16, 16, format, source: space) { Dither = false };
        converter.Submit(compositor.Texture, 0);
        return ExportCheck.ReadOne(converter, 0)[0];
    }

    /// <summary>Dibuja la composición a su tamaño en un búfer BGRA8 con el presentador actual o con <paramref name="legacyShader"/>.</summary>
    private static unsafe byte[] Present(GpuDevice gpu, Compositor compositor, byte[]? legacyShader)
    {
        using var target = new OffscreenTarget(gpu, compositor.Width, compositor.Height);
        if (legacyShader is null)
        {
            using var presenter = new Presenter(gpu);
            presenter.Draw(target.RenderTargetView, compositor.Width, compositor.Height, compositor);
            return target.Read().Pixels;
        }

        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        using var pipeline = new QuadPipeline(device, context);
        ID3D11PixelShader* shader;
        fixed (byte* bytes = legacyShader)
        {
            RenderException.Check(device->CreatePixelShader(bytes, (nuint)legacyShader.Length, null, &shader), "CreatePixelShader(presentador SDR)");
        }

        gpu.LockContext();
        try
        {
            float* black = stackalloc float[] { 0f, 0f, 0f, 1f };
            context->ClearRenderTargetView((ID3D11RenderTargetView*)target.RenderTargetView, black);
            pipeline.Bind((ID3D11RenderTargetView*)target.RenderTargetView, compositor.Width, compositor.Height, shader, null);
            var view = (ID3D11ShaderResourceView*)compositor.ShaderResourceView;
            context->PSSetShaderResources(2, 1, &view);
            pipeline.Draw(Presenter.Constants(new PixelRect(0, 0, compositor.Width, compositor.Height), compositor.Width, compositor.Height, false));
        }
        finally
        {
            pipeline.Unbind();
            shader->Release();
            gpu.UnlockContext();
        }

        return target.Read().Pixels;
    }

    private static YuvImage Scene()
    {
        var image = new YuvImage(64, 32, true);
        var random = new Random(55);
        for (int y = 0; y < 32; y += 8)
        {
            for (int x = 0; x < 64; x += 8)
            {
                image.Fill(x, y, 8, 8, new YuvCode(random.Next(64, 700), random.Next(400, 624), random.Next(400, 624)));
            }
        }

        return image;
    }
}
