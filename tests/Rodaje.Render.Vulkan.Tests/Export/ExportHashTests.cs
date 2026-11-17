using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.Vulkan;
using Rodaje.Render.Tests.Gpu;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// La exportación por CPU (FFV1 y ProRes) de un proyecto con efecto de color, fundido, velocidad, reversa e imagen en imagen, con el motor
/// Vulkan (cuadros decodificados en la CPU y subidos por el compositor) frente a la de Windows con D3D11 WARP. En Windows se comparan los dos
/// motores en el mismo proceso; en Linux, con el archivo que deja el trabajo de Windows de la CI (<c>RODAJE_REFERENCIAS_WARP</c>).
/// </summary>
/// <remarks>
/// El mismo hash cuadro a cuadro no se puede pedir entre rasterizadores: el filtro bilineal y el redondeo a 10 bits difieren en las fronteras
/// (con la RTX 3050, el 0,36 % de las muestras sale con un código de diferencia). Se pide lo que sí es reproducible: los mismos cuadros, en
/// FFV1 (sin pérdidas, lo que entró por la tubería) como mucho 1 código de 10 bits en el 1 % de las muestras, y en ProRes, que recomprime,
/// una relación señal/ruido de 60 dB o más. Cuántos cuadros salen idénticos se escribe en la salida.
/// </remarks>
public sealed class ExportHashTests(ITestOutputHelper output)
{
    private const string ReferenceVariable = "RODAJE_REFERENCIAS_WARP";

    public static TheoryData<string> Codecs => ["ffv1", "prores_ks"];

    [VulkanTheory]
    [MemberData(nameof(Codecs))]
    public async Task Vulkan_exports_the_same_frames_as_d3d11_warp(string encoder)
    {
        if (Rodaje.TestMedia.Skip.Proxies is { } reason)
        {
            output.WriteLine(reason);
            return;
        }

        using var scratch = new Scratch();
        string vulkan = await ExportAsync(encoder, () => VulkanGpu.Create(preferSoftware: true), null, scratch.Combine("vulkan"));
        string? reference = await ReferenceAsync(encoder, scratch);
        if (reference is null)
        {
            output.WriteLine($"Sin la exportación de WARP para {encoder} ({ReferenceVariable}): solo se comprobó que exporta.");
            return;
        }

        string format = PixelFormat(encoder);
        ushort[] actual = Decode(vulkan, format);
        ushort[] expected = Decode(reference, format);
        Assert.Equal(expected.Length, actual.Length);
        int frames = FrameCount(reference);
        int frameSamples = expected.Length / frames;
        int identical = 0, differing = 0, worst = 0;
        double squared = 0;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<ushort> a = expected.AsSpan(f * frameSamples, frameSamples);
            ReadOnlySpan<ushort> b = actual.AsSpan(f * frameSamples, frameSamples);
            if (a.SequenceEqual(b))
            {
                identical++;
                continue;
            }

            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                if (d != 0)
                {
                    differing++;
                    worst = Math.Max(worst, d);
                    squared += (double)d * d;
                }
            }
        }

        double fraction = (double)differing / expected.Length;
        double psnr = squared == 0 ? 99 : 10 * Math.Log10(1023.0 * 1023.0 * expected.Length / squared);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{encoder}: {identical} de {frames} cuadros idénticos; {fraction:P3} de las muestras distintas, como mucho {worst} código(s); {psnr:0.0} dB"));
        if (encoder == "ffv1")
        {
            Assert.True(worst <= 1, $"Diferencia de {worst} códigos.");
            Assert.True(fraction <= 0.01, $"El {fraction:P3} de las muestras difiere.");
        }
        else
        {
            Assert.True(psnr >= 60, $"{psnr:0.0} dB");
        }
    }

    /// <summary>Con <c>RODAJE_REFERENCIAS_WARP</c> en Windows, deja la exportación con WARP para el trabajo de Linux.</summary>
    [Fact]
    public async Task Generate_warp_references()
    {
        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        if (folder is null || !OperatingSystem.IsWindowsVersionAtLeast(7) || Rodaje.TestMedia.Skip.Proxies is not null)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (string encoder in Codecs)
        {
            await ExportAsync(encoder, null, GpuDevice.CreateWarp, Path.Combine(folder, "export-" + encoder));
        }
    }

    private static async Task<string?> ReferenceAsync(string encoder, Scratch scratch)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return await ExportAsync(encoder, null, GpuDevice.CreateWarp, scratch.Combine("warp"));
        }

        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        string? file = folder is null ? null : Path.Combine(folder, "export-" + encoder + Extension(encoder));
        return file is not null && File.Exists(file) ? file : null;
    }

    private static string Extension(string encoder) => encoder == "prores_ks" ? ".mov" : ".mkv";

    private static string PixelFormat(string encoder) => encoder == "prores_ks" ? "yuv422p10le" : "yuv420p10le";

    /// <summary>Exporta a <paramref name="stem"/> (más la extensión del contenedor) y devuelve la ruta.</summary>
    private static async Task<string> ExportAsync(string encoder, Func<IGpu>? createGpu, Func<GpuDevice>? createDevice, string stem)
    {
        FfmpegExecutable ffmpeg = Rodaje.TestMedia.Skip.ProxyExecutable!;
        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(ffmpeg).GetAsync();
        EncoderSpec spec = encoders.First(e => e.Id == encoder);
        bool prores = spec.Codec == ExportCodec.ProRes;
        string path = stem + Extension(encoder);
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = ffmpeg, CreateGpu = createGpu, CreateDevice = createDevice });
        using ExportJob job = runner.Start(new ExportRequest(ExportProjects.TwoTracks(320, 180), "sec", path, new ExportSettings
        {
            Container = prores ? ExportContainer.Mov : ExportContainer.Mkv,
            Video = new VideoExportSettings
            {
                Codec = spec.Codec,
                EncoderId = encoder,
                BitDepth = 10,
                Chroma = prores ? ChromaSubsampling.Chroma422 : ChromaSubsampling.Chroma420,
                Profile = prores ? "hq" : null,
                RateControl = prores ? RateControl.ProfileDefined : RateControl.Lossless,
            },
        }), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(10));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        return path;
    }

    private static int FrameCount(string path) => System.Text.Encoding.ASCII.GetString(Run("-i", path, "-map", "0:v:0", "-f", "framemd5", "-"))
        .Split('\n').Count(l => l.Length > 0 && l[0] != '#');

    private static ushort[] Decode(string path, string pixelFormat) =>
        MemoryMarshal.Cast<byte, ushort>(Run("-i", path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", pixelFormat, "-")).ToArray();

    private static byte[] Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Rodaje.TestMedia.Skip.ProxyExecutable!.Path) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return memory.ToArray();
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-exportacion-").FullName;

        public string Combine(string name) => Path.Combine(_folder, name);

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
