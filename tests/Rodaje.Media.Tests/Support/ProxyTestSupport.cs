using System.Collections.Concurrent;
using Rodaje.Core.Timing;
using Rodaje.Media.Proxies;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Support;

/// <summary>Medios con el número de cuadro dibujado (<see cref="FrameCode"/>) para comprobar los proxies.</summary>
internal static class ProxyTestMedia
{
    /// <summary>H.264 1080p a 29,97 con cuadros B, GOP 60, en MP4 (599 cuadros).</summary>
    public static MediaSpec H264Ntsc30 => TestMediaPresets.H264Ntsc30BFrames;

    /// <summary>HEVC Main 10 1080p a 25 con cuadros B, en MKV y empezando en 1,5 s.</summary>
    public static MediaSpec Hevc10Offset { get; } = new("hevc10-1080p-25-desfase", 1920, 1080, Rational.Fps25, TimeSpan.FromSeconds(12))
    {
        Codec = VideoCodec.Hevc,
        BitDepth = 10,
        GopLength = 50,
        BFrames = 2,
        Container = ContainerFormat.Mkv,
        StartOffset = TimeSpan.FromSeconds(1.5),
    };

    /// <summary>H.264 1080p a 23,976 con cuadros B, en MP4 y empezando en 2,5 s.</summary>
    public static MediaSpec H264Offset { get; } = new("h264-1080p-23976-desfase", 1920, 1080, Rational.Ntsc24, TimeSpan.FromSeconds(10))
    {
        Codec = VideoCodec.H264,
        GopLength = 48,
        BFrames = 2,
        Container = ContainerFormat.Mp4,
        StartOffset = TimeSpan.FromSeconds(2.5),
    };

    /// <summary>Clip corto y pequeño para las pruebas del resolvedor.</summary>
    public static MediaSpec Small { get; } = new("h264-360p-30-corto", 640, 360, Rational.Fps30, TimeSpan.FromSeconds(3))
    {
        Codec = VideoCodec.H264,
        GopLength = 30,
        Container = ContainerFormat.Mp4,
    };

    /// <summary>HEVC Main 10 en 4K a 30 con GOP 120 y alta tasa de bits: lo que la fase 3 no puede reproducir en varias capas.</summary>
    public static MediaSpec Uhd { get; } = new("hevc10-2160p-30", 3840, 2160, Rational.Fps30, TimeSpan.FromSeconds(10))
    {
        Codec = VideoCodec.Hevc,
        BitDepth = 10,
        GopLength = 120,
        BFrames = 2,
        Container = ContainerFormat.Mp4,
        VideoBitrateKbps = 60_000,
    };

    private static readonly ConcurrentDictionary<string, Lazy<GeneratedMedia>> Cache = new();

    public static GeneratedMedia Get(MediaSpec spec) =>
        Cache.GetOrAdd(spec.Name, _ => new Lazy<GeneratedMedia>(() => TestMediaGenerator.CreateDefault()!.GetOrCreate(spec))).Value;
}

/// <summary>El ffmpeg.exe de los proxies; qué hace falta para generarlos lo decide <see cref="Skip.Proxies"/>.</summary>
internal static class ProxyTestEnvironment
{
    public static FfmpegExecutable? Executable => Skip.ProxyExecutable;
}

/// <summary>Carpeta temporal propia de una prueba, borrada al final.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rodaje-pruebas-proxies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Reloj fijo que se adelanta a mano.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary><see cref="IProgress{T}"/> que llama en el mismo hilo, sin contexto de sincronización.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
