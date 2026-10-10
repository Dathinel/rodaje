using Rodaje.Media.Gpu;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Support;

/// <summary>La GPU compartida y las rutas de FFmpeg; los motivos para omitir pruebas están en <see cref="Skip"/>.</summary>
internal static class MediaTestEnvironment
{
    // Solo con FFmpeg. El dispositivo existe aunque no ofrezca D3D11VA: sirve para subir cuadros decodificados por software.
    private static readonly Lazy<GpuDevice?> SharedGpu = new(() =>
        Skip.FfmpegLibraries is null ? GpuDevice.TryCreate(out _) : null);

    /// <summary>Dispositivo compartido entre pruebas (vive todo el proceso de pruebas).</summary>
    public static GpuDevice? Gpu => SharedGpu.Value;

    public static string FfmpegExe => Path.Combine(FfmpegLibrary.Directory ?? "", "ffmpeg.exe");

    public static string FfprobeExe => Path.Combine(FfmpegLibrary.Directory ?? "", "ffprobe.exe");

    /// <summary><c>TestResults\media-cache</c> en la raíz del repositorio (la carpeta con Rodaje.slnx).</summary>
    public static string CacheDirectory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
                {
                    string cache = Path.Combine(dir.FullName, "TestResults", "media-cache");
                    Directory.CreateDirectory(cache);
                    return cache;
                }
            }

            string fallback = Path.Combine(Path.GetTempPath(), "Rodaje", "media-cache");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
