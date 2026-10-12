using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Acceptance.AcceptanceSupport;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>
/// Exactitud y fidelidad en un códec sin pérdida: el primer bloque de 60 s de la escena (1×, 2×, 0,5×, 1001/1000×,
/// «Bala», congelado y 4 transiciones en la principal) a FFV1, con el 100 % de los números, y cuadros contra la vista
/// previa (<see cref="TimelineSession"/> + <see cref="Compositor"/> a resolución completa) convertidos aparte en la CPU.
/// </summary>
[Collection(ExportAcceptanceGroup.Name)]
public sealed class ExportFidelityAcceptanceTests(ITestOutputHelper output)
{
    private static readonly ExportSettings Ffv1 = new()
    {
        Container = ExportContainer.Mkv,
        Video = new VideoExportSettings { Codec = ExportCodec.Ffv1, RateControl = RateControl.Lossless },
        Performance = ExportPerformance.Fastest,
    };

    [ExportAcceptanceFact]
    public async Task Ffv1_has_every_frame_exact_and_matches_the_preview()
    {
        AcceptanceScene scene = Scene(1);
        string folder = Folder("ffv1");
        string path = Path.Combine(folder, "maestro sin pérdida.mkv");
        await ExportAsync(output, scene.Project, path, Ffv1);

        (long checkedCount, long covered, List<string> failures, long frames) = CheckCodes(scene, path);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"FFV1, números: {checkedCount - failures.Count}/{checkedCount} exactos en {frames} cuadros ({covered} lecturas tapadas por transiciones). {string.Join("; ", failures.Take(10))}"));

        // Un cuadro por tramo y por transición (la principal: fundido en 10 s, cortinilla en 20 s, empuje en 30 s, a negro en 40 s).
        long[] samples = [0, 151, 299, 300, 455, 601, 750, 905, 1050, 1201, 1290, 1420, 1600, 1799];
        (int lumaMax, int chromaMax, double lumaOver, double chromaOver) = CompareWithPreview(scene, path, samples);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Contra la vista previa ({samples.Length} cuadros): luma máx. {lumaMax} códigos ({lumaOver:P4} de muestras >1), croma máx. {chromaMax} ({chromaOver:P4} >2)."));
        Delete(folder);

        Assert.Equal(scene.FrameCount, frames);
        Assert.True(failures.Count == 0, $"{failures.Count} números mal: {string.Join("; ", failures.Take(20))}");
        Assert.True(lumaMax <= 1 && chromaMax <= 2, $"luma {lumaMax}, croma {chromaMax}");
    }

    [ExportAcceptanceFact]
    public async Task A_crop_exports_only_its_region()
    {
        AcceptanceScene scene = Scene(1);
        string folder = Folder("recorte");
        string path = Path.Combine(folder, "recorte.mkv");
        ExportSettings settings = Ffv1 with { Video = Ffv1.Video! with { Crop = new ExportCrop(0, 540, 960, 540) } };
        ExportJob job = await ExportAsync(output, scene.Project, path, settings, new ExportRange(Time.Zero, Time.FromSeconds(3)));
        Assert.Equal((960, 540), (job.Report!.Video!.Width, job.Report.Video.Height));
        SceneLayer left = scene.Layers[1];
        var wrong = new List<string>();
        long count = DecodeGray(path, 960, 540, (i, luma) =>
        {
            long? expected = AcceptanceScene.Expected(left, i);
            int? read = FrameCode.Read(luma, 960, 540, 960);
            if (expected is not null && read != expected)
            {
                wrong.Add($"{i}: {read} en vez de {expected}");
            }
        });
        output.WriteLine($"Recorte del cuadrante de abajo a la izquierda: {count - wrong.Count}/{count} con el número del PiP.");
        Delete(folder);
        Assert.Equal(90, count);
        Assert.Empty(wrong);
    }

    /// <summary>
    /// Diferencia máxima por muestra entre el archivo y la vista previa convertida en la CPU (BT.709 limitado, croma
    /// «left» [1 2 1]/4 y promedio vertical, sin tramado): luma, croma y la fracción que pasa de 1 y de 2 códigos.
    /// </summary>
    private (int LumaMax, int ChromaMax, double LumaOver, double ChromaOver) CompareWithPreview(AcceptanceScene scene, string path, long[] frames)
    {
        const int w = AcceptanceScene.Width;
        const int h = AcceptanceScene.Height;
        using GpuDevice gpu = GpuDevice.Create();
        using var session = TimelineSession.Open(scene.Project, AcceptanceScene.SequenceId, gpu,
            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never });
        using var compositor = new Compositor(gpu, w, h);
        int lumaMax = 0;
        int chromaMax = 0;
        long lumaOver = 0;
        long chromaOver = 0;
        long lumaSamples = 0;
        long chromaSamples = 0;
        foreach (long f in frames)
        {
            session.SeekToFrame(f);
            var watch = Stopwatch.StartNew();
            CompositionFrame shown = session.Tick();
            while (session.IsSeekPending)
            {
                shown.Dispose();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"La vista previa no llegó al cuadro {f}.");
                Thread.Sleep(2);
                shown = session.Tick();
            }

            using (shown)
            {
                compositor.Render(shown, l => new SourceGeometry(l.SampleAspectRatio, l.Rotation));
            }

            float[] rgb = ReadbackFloat(gpu, compositor);
            byte[] yuv = DecodeYuv420(path, f, w, h);
            int frameLuma = 0;
            int frameChroma = 0;
            double[] cb = new double[w * h];
            double[] cr = new double[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = ((y * w) + x) * 4;
                    double r = Math.Clamp(rgb[i], 0, 1);
                    double g = Math.Clamp(rgb[i + 1], 0, 1);
                    double b = Math.Clamp(rgb[i + 2], 0, 1);
                    double luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                    cb[(y * w) + x] = (b - luma) / 1.8556;
                    cr[(y * w) + x] = (r - luma) / 1.5748;
                    int d = Math.Abs(yuv[(y * w) + x] - (int)Math.Round(16 + (219 * luma)));
                    frameLuma = Math.Max(frameLuma, d);
                    lumaOver += d > 1 ? 1 : 0;
                    lumaSamples++;
                }
            }

            int cw = w / 2;
            for (int cy = 0; cy < h / 2; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    double sb = 0;
                    double sr = 0;
                    for (int row = 2 * cy; row <= (2 * cy) + 1; row++)
                    {
                        for (int k = -1; k <= 1; k++)
                        {
                            int x = Math.Clamp((2 * cx) + k, 0, w - 1);
                            double weight = k == 0 ? 0.5 : 0.25;
                            sb += weight * cb[(row * w) + x];
                            sr += weight * cr[(row * w) + x];
                        }
                    }

                    int db = Math.Abs(yuv[(w * h) + (cy * cw) + cx] - (int)Math.Round(128 + (224 * sb / 2)));
                    int dr = Math.Abs(yuv[(w * h) + (w * h / 4) + (cy * cw) + cx] - (int)Math.Round(128 + (224 * sr / 2)));
                    frameChroma = Math.Max(frameChroma, Math.Max(db, dr));
                    chromaOver += (db > 2 ? 1 : 0) + (dr > 2 ? 1 : 0);
                    chromaSamples += 2;
                }
            }

            output.WriteLine($"  cuadro {f}: luma máx. {frameLuma}, croma máx. {frameChroma}");
            lumaMax = Math.Max(lumaMax, frameLuma);
            chromaMax = Math.Max(chromaMax, frameChroma);
        }

        return (lumaMax, chromaMax, (double)lumaOver / lumaSamples, (double)chromaOver / chromaSamples);
    }

    /// <summary>La composición RGBA16F tal cual (sin cuantizar a 8 bits), premultiplicada sobre negro opaco.</summary>
    private static unsafe float[] ReadbackFloat(GpuDevice gpu, Compositor compositor)
    {
        var texture = (ID3D11Texture2D*)compositor.Texture;
        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        desc.MipLevels = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Assert.True(device->CreateTexture2D(&desc, null, &staging) >= 0);
        var pixels = new float[compositor.Width * compositor.Height * 4];
        gpu.LockContext();
        try
        {
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, 0, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Assert.True(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped) >= 0);
            for (int y = 0; y < compositor.Height; y++)
            {
                var row = new ReadOnlySpan<Half>((byte*)mapped.pData + ((nuint)y * mapped.RowPitch), compositor.Width * 4);
                for (int i = 0; i < row.Length; i++)
                {
                    pixels[(y * compositor.Width * 4) + i] = (float)row[i];
                }
            }

            context->Unmap((ID3D11Resource*)staging, 0);
        }
        finally
        {
            gpu.UnlockContext();
            staging->Release();
        }

        return pixels;
    }
}
