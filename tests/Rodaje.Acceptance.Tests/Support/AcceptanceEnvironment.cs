using System.Diagnostics;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.TestMedia;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Rodaje.Acceptance.Tests.Support;

public enum DecodePath
{
    /// <summary>D3D11VA sobre el dispositivo compartido.</summary>
    Hardware,

    /// <summary>Decodificación por software, cuadros en la CPU.</summary>
    Software,
}

/// <summary>Qué hay en esta máquina: ffmpeg.exe con codificadores (para generar), las DLL de FFmpeg y D3D11VA.</summary>
internal static class AcceptanceEnvironment
{
    public const string LongVariable = "RODAJE_PRUEBAS_LARGAS";

    public const string CompleteVariable = "RODAJE_PRUEBAS_COMPLETAS";

    private static readonly Lazy<string?> MediaReason = new(() =>
    {
        FfmpegTool? tool = FfmpegTool.Default;
        if (tool is null)
        {
            return "Omitida: " + FfmpegTool.UnavailableReason;
        }

        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.Hevc })
        {
            if (tool.FindVideoEncoder(codec) is null)
            {
                return $"Omitida: ningún codificador de {codec} funciona con {tool.ExecutablePath}.";
            }
        }

        if (tool.ProbeEncoder(FfmpegTool.AudioEncoderName(AudioCodec.Aac)) is { } audio)
        {
            return "Omitida: " + audio;
        }

        return FfmpegLibrary.TryInitialize(out string? reason) ? null : "Omitida: sin las DLL de FFmpeg: " + reason;
    });

    private static readonly Lazy<(GpuDevice? Device, string? Reason)> SharedGpu = new(() =>
    {
        if (MediaReason.Value is { } reason)
        {
            return (null, reason);
        }

        GpuDevice? device = GpuDevice.TryCreate(out string? failure);
        if (device is null)
        {
            return (null, "Omitida: sin GPU Direct3D 11: " + failure);
        }

        return device.SupportsHardwareDecoding
            ? (device, null)
            : (device, $"Omitida: la GPU {device.AdapterName} no ofrece D3D11VA a FFmpeg.");
    });

    private static readonly Lazy<TestMediaGenerator> SharedGenerator = new(() =>
        TestMediaGenerator.CreateDefault() ?? throw new InvalidOperationException(FfmpegTool.UnavailableReason));

    public static string? SkipReason(bool hardware, bool longRunning, bool complete = false)
    {
        string? reason = hardware ? SharedGpu.Value.Reason : MediaReason.Value;
        if (reason is null && longRunning && !LongRuns)
        {
            reason = $"Omitida: prueba larga; se activa con {LongVariable}=1 (ABRIR.bat aceptacion).";
        }

        if (reason is null && complete && Environment.GetEnvironmentVariable(CompleteVariable) != "1")
        {
            reason = $"Omitida: prueba de 10 minutos en tiempo real; se activa con {CompleteVariable}=1 (herramientas\\aceptacion.ps1 -Completa).";
        }

        return reason;
    }

    /// <summary>Corrida de aceptación (<c>RODAJE_PRUEBAS_LARGAS=1</c>): las pruebas rápidas también hacen la medida completa.</summary>
    public static bool LongRuns => Environment.GetEnvironmentVariable(LongVariable) == "1";

    public static GpuDevice? Gpu => SharedGpu.Value.Device;

    public static bool HasHardwareDecoding => SharedGpu.Value.Reason is null;

    public static TestMediaGenerator Generator => SharedGenerator.Value;

    public static string MediaPath(MediaSpec spec) => Generator.GetOrCreate(spec).Path;

    public static VideoDecoderOptions Options(DecodePath path) => path == DecodePath.Hardware
        ? new VideoDecoderOptions { Device = Gpu ?? throw new InvalidOperationException("No hay GPU con D3D11VA.") }
        : new VideoDecoderOptions();

    /// <summary>
    /// Caché con lugar para la ventana adelantada más unos cuadros, medida con el tamaño real que el
    /// decodificador declara (en GPU es el de la textura del pool, que puede ser mayor que el cuadro visible).
    /// </summary>
    public static Rodaje.Playback.FrameCache CacheFor(VideoDecoder decoder, Rodaje.Playback.PlaybackOptions options)
    {
        using Rodaje.Playback.DecodedFrame first = decoder.GetFrame(0) ?? throw new InvalidOperationException("El medio no tiene cuadro 0.");
        return new Rodaje.Playback.FrameCache(first.SizeBytes * (options.PrefetchFrames + 4));
    }

    /// <summary>Lee el código dibujado en el cuadro; null si no se distingue.</summary>
    public static int? ReadCode(Rodaje.Playback.DecodedFrame frame)
    {
        LumaImage luma = FrameReadback.ReadLuma(frame);
        return FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
    }

    /// <summary>Índices de los keyframes en orden de presentación, según ffprobe.</summary>
    public static IReadOnlyList<int> KeyFrames(string path, int gopFallback, int frameCount)
    {
        string? ffprobe = FfmpegTool.Default?.FfprobePath;
        if (ffprobe is null)
        {
            return Enumerable.Range(0, (frameCount + gopFallback - 1) / gopFallback).Select(i => i * gopFallback).ToList();
        }

        var info = new ProcessStartInfo(ffprobe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=key_frame", "-of", "csv=p=0", path })
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar {ffprobe}.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe terminó con código {process.ExitCode}: {errors.Result}");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((flag, index) => (flag, index))
            .Where(x => x.flag.StartsWith('1'))
            .Select(x => x.index)
            .ToList();
    }
}

/// <summary>Prueba de aceptación: se omite con el motivo si falta FFmpeg, un codificador o (si se pide) D3D11VA.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AcceptanceFactAttribute : FactAttribute
{
    public AcceptanceFactAttribute(bool hardware = false, bool longRunning = false, bool complete = false) =>
        Skip = AcceptanceEnvironment.SkipReason(hardware, longRunning || complete, complete);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AcceptanceTheoryAttribute : TheoryAttribute
{
    public AcceptanceTheoryAttribute(bool hardware = false, bool longRunning = false) =>
        Skip = AcceptanceEnvironment.SkipReason(hardware, longRunning);
}
