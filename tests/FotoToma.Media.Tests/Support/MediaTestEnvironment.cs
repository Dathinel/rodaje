using FotoToma.Media.Gpu;

namespace FotoToma.Media.Tests.Support;

/// <summary>Qué hay disponible en esta máquina: FFmpeg (DLL y ffmpeg.exe) y una GPU con D3D11VA.</summary>
internal static class MediaTestEnvironment
{
    private static readonly Lazy<(bool Ok, string? Reason)> Ffmpeg = new(() =>
    {
        if (!FfmpegLibrary.TryInitialize(out string? reason))
        {
            return (false, reason);
        }

        return File.Exists(FfmpegExe) && File.Exists(FfprobeExe)
            ? (true, null)
            : (false, $"Faltan ffmpeg.exe o ffprobe.exe en {FfmpegLibrary.Directory}; ejecuta ABRIR.bat preparar-entorno.");
    });

    private static readonly Lazy<(GpuDevice? Device, string? Reason)> SharedGpu = new(() =>
    {
        if (!Ffmpeg.Value.Ok)
        {
            return (null, Ffmpeg.Value.Reason);
        }

        GpuDevice? device = GpuDevice.TryCreate(out string? reason);
        if (device is null)
        {
            return (null, reason);
        }

        return device.SupportsHardwareDecoding
            ? (device, null)
            : (device, $"La GPU {device.AdapterName} no ofrece D3D11VA a FFmpeg.");
    });

    public static string? FfmpegSkipReason => Ffmpeg.Value.Ok ? null : "Sin FFmpeg: " + Ffmpeg.Value.Reason;

    /// <summary>Motivo para saltar una prueba que necesita decodificación por hardware.</summary>
    public static string? HardwareSkipReason =>
        FfmpegSkipReason ?? (SharedGpu.Value.Reason is { } r ? "Sin decodificación D3D11VA: " + r : null);

    /// <summary>Dispositivo compartido entre pruebas (vive todo el proceso de pruebas).</summary>
    public static GpuDevice? Gpu => SharedGpu.Value.Device;

    public static string FfmpegExe => Path.Combine(FfmpegLibrary.Directory ?? "", "ffmpeg.exe");

    public static string FfprobeExe => Path.Combine(FfmpegLibrary.Directory ?? "", "ffprobe.exe");

    /// <summary><c>TestResults\media-cache</c> en la raíz del repositorio (la carpeta con FotoToma.slnx).</summary>
    public static string CacheDirectory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FotoToma.slnx")))
                {
                    string cache = Path.Combine(dir.FullName, "TestResults", "media-cache");
                    Directory.CreateDirectory(cache);
                    return cache;
                }
            }

            string fallback = Path.Combine(Path.GetTempPath(), "FotoToma", "media-cache");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}

/// <summary>Prueba que necesita FFmpeg; se salta con el motivo si no está.</summary>
internal sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute() => Skip = MediaTestEnvironment.FfmpegSkipReason;
}

internal sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute() => Skip = MediaTestEnvironment.FfmpegSkipReason;
}

/// <summary>Prueba que necesita FFmpeg y una GPU con D3D11VA.</summary>
internal sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute() => Skip = MediaTestEnvironment.HardwareSkipReason;
}
