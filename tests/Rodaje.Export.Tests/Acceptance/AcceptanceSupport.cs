using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>Criterio de terminado de la fase 6: solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>, <c>ffmpeg.exe</c>, la GPU y NVENC.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExportAcceptanceFactAttribute : FactAttribute
{
    public ExportAcceptanceFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.LongRun, Rodaje.TestMedia.Skip.Proxies,
        Rodaje.TestMedia.Skip.GpuTextures, Rodaje.TestMedia.Skip.HardwareDecoding, Rodaje.TestMedia.Skip.Nvenc);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExportAcceptanceGroup
{
    public const string Name = "Aceptación de la exportación";
}

/// <summary>Lo que comparten las pruebas de aceptación: escena en caché, codificadores, carpetas, decodificar y medir.</summary>
internal static partial class AcceptanceSupport
{
    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> SharedEncoders = new(() =>
        new CatalogEncoderSource(Ffmpeg).GetAsync());

    private static readonly Dictionary<int, AcceptanceScene> Scenes = [];

    public static FfmpegExecutable Ffmpeg => Rodaje.TestMedia.Skip.ProxyExecutable!;

    public static string FfprobePath => Path.Combine(Path.GetDirectoryName(Ffmpeg.Path)!, "ffprobe.exe");

    public static Task<IReadOnlyList<EncoderSpec>> ProbedEncoders => SharedEncoders.Value;

    /// <summary>La escena de <paramref name="minutes"/> minutos (una por proceso).</summary>
    public static AcceptanceScene Scene(int minutes)
    {
        lock (Scenes)
        {
            if (!Scenes.TryGetValue(minutes, out AcceptanceScene? scene))
            {
                scene = AcceptanceScene.Build(Rodaje.Core.Timing.Time.FromSeconds(minutes * 60));
                Scenes[minutes] = scene;
            }

            return scene;
        }
    }

    /// <summary>Carpeta nueva en el disco de los medios de prueba (los maestros sin pérdida ocupan gigas), con espacio y tilde.</summary>
    public static string Folder(string name)
    {
        string root = Path.Combine(Path.GetDirectoryName(TestMediaGenerator.DefaultCacheDirectory)!, "aceptación exportar");
        string folder = Path.Combine(root, name + "-" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Exporta y espera; falla la prueba si no termina bien.</summary>
    public static async Task<ExportJob> ExportAsync(ITestOutputHelper output, Project project, string path, ExportSettings settings,
        ExportRange? range = null, ExportRunnerOptions? options = null, TimeSpan? timeout = null)
    {
        IReadOnlyList<EncoderSpec> encoders = await ProbedEncoders;
        var runner = new ExportRunner(options ?? new ExportRunnerOptions { Ffmpeg = Ffmpeg });
        ExportJob job = runner.Start(new ExportRequest(project.Clone(), AcceptanceScene.SequenceId, path, settings, range), encoders);
        await job.Completion.WaitAsync(timeout ?? TimeSpan.FromMinutes(20));
        ExportProgress p = job.Progress;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(path)}: {p.State} en {p.Elapsed.TotalSeconds:0.0} s, {p.FramesDone} cuadros ({p.FramesDone / Math.Max(p.Elapsed.TotalSeconds, 0.001):0} fps de media), {job.Plan?.VideoEncoder?.Id} {job.Plan?.Video?.PixelFormat}, {(File.Exists(path) ? new FileInfo(path).Length / 1048576.0 : 0):0.0} MB. {job.Failure?.Message}"));
        Assert.True(p.State == ExportJobState.Completed, job.Failure?.ToString());
        return job;
    }

    /// <summary>
    /// Decodifica el video entero a luma de 8 bits y comprueba en cada cuadro el número de cada capa contra
    /// <see cref="AcceptanceScene.Expected"/>. Devuelve (comprobados, tapados por transiciones, fallos).
    /// </summary>
    public static (long Checked, long Covered, List<string> Failures, long Frames) CheckCodes(AcceptanceScene scene, string path, long firstFrame = 0)
    {
        long checkedCount = 0;
        long covered = 0;
        var failures = new List<string>();
        long frames = DecodeGray(path, AcceptanceScene.Width, AcceptanceScene.Height, (index, luma) =>
        {
            long frame = firstFrame + index;
            foreach (SceneLayer layer in scene.Layers)
            {
                long? expected = AcceptanceScene.Expected(layer, frame);
                if (expected is null)
                {
                    covered++;
                    continue;
                }

                checkedCount++;
                int? read = AcceptanceScene.Read(layer, luma);
                if (read != expected && failures.Count < 10_000)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{layer.Name}@{frame}: leído {(read is { } r ? r.ToString(CultureInfo.InvariantCulture) : "ilegible")}, esperado {expected}"));
                }
            }
        });
        return (checkedCount, covered, failures, frames);
    }

    /// <summary>Cuadros de luma de 8 bits, sin duplicar ni descartar.</summary>
    public static long DecodeGray(string path, int width, int height, Action<long, ReadOnlySpan<byte>> onFrame)
    {
        using Process process = Start(Ffmpeg.Path, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough",
            "-f", "rawvideo", "-pix_fmt", "gray", "-");
        Task<string> error = process.StandardError.ReadToEndAsync();
        Stream stream = process.StandardOutput.BaseStream;
        byte[] buffer = new byte[width * height];
        long count = 0;
        while (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length)
        {
            onFrame(count++, buffer);
        }

        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return count;
    }

    /// <summary>Planos Y, Cb y Cr de 8 bits (4:2:0) del cuadro <paramref name="frame"/>.</summary>
    public static byte[] DecodeYuv420(string path, long frame, int width, int height)
    {
        using Process process = Start(Ffmpeg.Path, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path,
            "-vf", string.Create(CultureInfo.InvariantCulture, $"select=eq(n\\,{frame})"), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-");
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.True(memory.Length == (long)width * height * 3 / 2, $"Cuadro {frame}: {memory.Length} bytes. {error.Result}");
        return memory.ToArray();
    }

    /// <summary>El primer audio, mono float a 48 kHz.</summary>
    public static float[] DecodeAudioMono(string path)
    {
        using Process process = Start(Ffmpeg.Path, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:a:0",
            "-f", "f32le", "-ac", "1", "-ar", "48000", "-");
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return MemoryMarshal.Cast<byte, float>(memory.GetBuffer().AsSpan(0, (int)memory.Length)).ToArray();
    }

    /// <summary><c>start_time</c> de cada flujo según ffprobe, en segundos.</summary>
    public static (double Video, double Audio) StartTimes(string path)
    {
        using Process process = Start(FfprobePath, "-v", "error", "-show_entries", "stream=codec_type,start_time", "-of", "json", path);
        string json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        using JsonDocument doc = JsonDocument.Parse(json);
        double video = double.NaN;
        double audio = double.NaN;
        foreach (JsonElement s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            double start = double.Parse(s.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
            if (s.GetProperty("codec_type").GetString() == "video")
            {
                video = start;
            }
            else
            {
                audio = start;
            }
        }

        return (video, audio);
    }

    /// <summary>VMAF (modelo v0.6.1) de <paramref name="distorted"/> contra <paramref name="reference"/>: media y percentil 1 por cuadro.</summary>
    public static (double Mean, double P1, double Psnr) Vmaf(string reference, string distorted, string folder)
    {
        // El registro va por nombre relativo a la carpeta: una ruta con ':' no pasa bien por la sintaxis de filtros.
        string name = "vmaf-" + Path.GetFileNameWithoutExtension(distorted) + ".json";
        string log = Path.Combine(folder, name);
        string threads = Math.Max(2, Environment.ProcessorCount - 2).ToString(CultureInfo.InvariantCulture);
        using Process process = StartIn(folder, Ffmpeg.Path, "-hide_banner", "-loglevel", "error", "-nostdin", "-i", distorted, "-i", reference,
            "-lavfi", $"[0:v]format=yuv420p,settb=1/30,setpts=N[d];[1:v]format=yuv420p,settb=1/30,setpts=N[r];[d][r]libvmaf=log_fmt=json:log_path={name}:n_threads={threads}:feature=name=psnr",
            "-f", "null", "-");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(log));
        List<double> scores = [.. doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => f.GetProperty("metrics").GetProperty("vmaf").GetDouble())];
        Worst = string.Join(" ", scores.Select((v, i) => (v, i)).OrderBy(x => x.v).Take(8).Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.i}:{x.v:0}")));
        scores.Sort();
        JsonElement pooled = doc.RootElement.GetProperty("pooled_metrics");
        return (pooled.GetProperty("vmaf").GetProperty("mean").GetDouble(), scores[(int)(scores.Count * 0.01)],
            pooled.GetProperty("psnr_y").GetProperty("mean").GetDouble());
    }

    /// <summary>Los cuadros peores de la última medida de <see cref="Vmaf"/> (índice:puntuación).</summary>
    public static string Worst { get; private set; } = "";

    public static Process Start(string exe, params string[] arguments) => StartIn(null, exe, arguments);

    public static Process StartIn(string? directory, string exe, params string[] arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = directory ?? "",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        return Process.Start(info)!;
    }

    /// <summary>Procesos <c>ffmpeg.exe</c> hijos de <paramref name="parent"/>.</summary>
    public static List<int> FfmpegChildren(int parent)
    {
        var children = new List<int>();
        foreach (Process p in Process.GetProcessesByName("ffmpeg"))
        {
            using (p)
            {
                if (ParentOf(p) == parent)
                {
                    children.Add(p.Id);
                }
            }
        }

        return children;
    }

    public static bool IsAlive(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<double> sorted = [.. values.Order()];
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Round(fraction * (sorted.Count - 1)))];
    }

    private static int ParentOf(Process process)
    {
        try
        {
            var info = default(ProcessBasicInformation);
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : -1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, ref ProcessBasicInformation information, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
