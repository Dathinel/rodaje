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

/// <summary>Medios, GPU y lecturas de la aceptación; los motivos para omitir pruebas están en <see cref="Skip"/>.</summary>
internal static class AcceptanceEnvironment
{
    // Solo si se pueden generar y decodificar los medios. El dispositivo existe aunque no ofrezca D3D11VA.
    private static readonly Lazy<GpuDevice?> SharedGpu = new(() =>
        Skip.First(Skip.FfmpegLibraries, Skip.Encoders([VideoCodec.H264, VideoCodec.Hevc], AudioCodec.Aac)) is null
            ? GpuDevice.TryCreate(out _)
            : null);

    private static readonly Lazy<TestMediaGenerator> SharedGenerator = new(() =>
        TestMediaGenerator.CreateDefault() ?? throw new InvalidOperationException(FfmpegTool.UnavailableReason));

    /// <summary>Corrida de aceptación (<c>RODAJE_PRUEBAS_LARGAS=1</c>): las pruebas rápidas también hacen la medida completa.</summary>
    public static bool LongRuns => Skip.LongRuns;

    public static GpuDevice? Gpu => SharedGpu.Value;

    public static bool HasHardwareDecoding => Skip.HardwareDecoding is null;

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
