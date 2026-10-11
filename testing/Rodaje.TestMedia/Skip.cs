using System.Collections.Concurrent;
using Rodaje.Audio;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Xunit;

namespace Rodaje.TestMedia;

/// <summary>
/// Por qué omitir una prueba en esta máquina, o <c>null</c> si puede correr. Es el único lugar donde se decide:
/// los atributos de abajo (<c>[FfmpegFact]</c>, <c>[HardwareFact]</c>, <c>[GpuFact]</c>...) solo combinan estos motivos.
/// Lo que falta en el equipo omite la prueba con su motivo en vez de hacerla fallar; las pruebas largas o ruidosas
/// esperan su variable de entorno.
/// </summary>
public static class Skip
{
    /// <summary>Pruebas largas y medidas en tiempo real: <c>ABRIR.bat aceptacion</c> la pone en 1.</summary>
    public const string LongVariable = "RODAJE_PRUEBAS_LARGAS";

    /// <summary>Los 10 minutos en tiempo real con el dispositivo de audio: <c>herramientas\aceptacion.ps1 -Completa</c>.</summary>
    public const string CompleteVariable = "RODAJE_PRUEBAS_COMPLETAS";

    /// <summary>La prueba que hace sonar un tono de verdad.</summary>
    public const string AudioVariable = "RODAJE_PRUEBAS_AUDIO";

    private static readonly Lazy<string?> FfmpegLibrariesReason = new(() =>
    {
        if (!FfmpegLibrary.TryInitialize(out string? reason))
        {
            return "Omitida: sin FFmpeg: " + reason;
        }

        string directory = FfmpegLibrary.Directory ?? string.Empty;
        return File.Exists(Path.Combine(directory, "ffmpeg.exe")) && File.Exists(Path.Combine(directory, "ffprobe.exe"))
            ? null
            : $"Omitida: sin FFmpeg: faltan ffmpeg.exe o ffprobe.exe en {directory}; ejecuta ABRIR.bat preparar-entorno.";
    });

    private static readonly Lazy<string?> HardwareDecodingReason = new(() =>
    {
        if (FfmpegLibrariesReason.Value is { } ffmpeg)
        {
            return ffmpeg;
        }

        using GpuDevice? gpu = GpuDevice.TryCreate(out string? failure);
        if (gpu is null)
        {
            return "Omitida: sin decodificación D3D11VA: " + failure;
        }

        return gpu.SupportsHardwareDecoding ? null : $"Omitida: la GPU {gpu.AdapterName} no ofrece D3D11VA a FFmpeg.";
    });

    private static readonly Lazy<string?> GpuTexturesReason = new(() =>
    {
        using GpuDevice? gpu = GpuDevice.TryCreate(out string? failure);
        if (gpu is null)
        {
            return "Omitida: " + failure;
        }

        return gpu.SupportsNv12Textures && gpu.SupportsP010Textures
            ? null
            : $"Omitida: la GPU {gpu.AdapterName} no muestrea texturas NV12 y P010.";
    });

    private static readonly Lazy<(FfmpegExecutable? Executable, string? Reason)> ProxyState = new(() =>
    {
        if (FfmpegLibrariesReason.Value is { } ffmpeg)
        {
            return (null, ffmpeg);
        }

        FfmpegExecutable? executable = FfmpegExecutable.TryLocate(out string? failure);
        if (executable is null)
        {
            return (null, "Omitida: " + failure);
        }

        if (Encoders([VideoCodec.H264, VideoCodec.Hevc]) is { } encoders)
        {
            return (null, encoders);
        }

        return ProxyEncoders.Preference.Any(executable.IsEncoderUsable)
            ? (executable, null)
            : (null, "Omitida: ningún codificador de proxies funciona en este equipo.");
    });

    private static readonly ConcurrentDictionary<VideoCodec, Lazy<string?>> BFramesReasons = new();

    private static readonly Lazy<string?> MissingAudioDevice = new(() =>
        WasapiOutput.ProbeDefaultDevice(out string description)
            ? null
            : $"No hay un dispositivo de salida de audio usable en este equipo: {description}");

    /// <summary>Corrida de aceptación: las pruebas largas corren y las rápidas hacen la medida completa.</summary>
    public static bool LongRuns => IsOn(LongVariable);

    public static bool CompleteRuns => IsOn(CompleteVariable);

    public static string? LongRun => LongRuns
        ? null
        : $"Omitida: prueba larga; se activa con {LongVariable}=1 (ABRIR.bat aceptacion).";

    public static string? CompleteRun => CompleteRuns
        ? null
        : $"Omitida: prueba de 10 minutos en tiempo real; se activa con {CompleteVariable}=1 (herramientas\\aceptacion.ps1 -Completa).";

    /// <summary>Las DLL de FFmpeg del editor, con ffmpeg.exe y ffprobe.exe en la misma carpeta.</summary>
    public static string? FfmpegLibraries => FfmpegLibrariesReason.Value;

    /// <summary><see cref="FfmpegLibraries"/> y una GPU Direct3D 11 que ofrece D3D11VA a FFmpeg.</summary>
    public static string? HardwareDecoding => HardwareDecodingReason.Value;

    /// <summary>Direct3D 11 por hardware que muestrea texturas NV12 y P010 (no necesita FFmpeg).</summary>
    public static string? GpuTextures => GpuTexturesReason.Value;

    /// <summary>Lo necesario para generar proxies: FFmpeg, ffmpeg.exe del editor y algún codificador de proxies.</summary>
    public static string? Proxies => ProxyState.Value.Reason;

    /// <summary>El ffmpeg.exe de los proxies cuando <see cref="Proxies"/> es <c>null</c>.</summary>
    public static FfmpegExecutable? ProxyExecutable => ProxyState.Value.Executable;

    /// <summary><see cref="Proxies"/> y además NVENC.</summary>
    public static string? Nvenc => Proxies
        ?? (ProxyExecutable!.ProbeEncoder("h264_nvenc") is { } r ? "Omitida: sin NVENC. " + r : null);

    /// <summary>Medidas con el dispositivo de audio real (reproduce silencio); van en la corrida de aceptación.</summary>
    public static string? AudioDevice => LongRuns
        ? MissingAudioDevice.Value
        : $"Medición de audio en tiempo real; se activa con {LongVariable}=1 (ABRIR.bat aceptacion).";

    /// <summary>La prueba que suena: solo con <c>RODAJE_PRUEBAS_AUDIO=1</c> y un dispositivo real.</summary>
    public static string? AudioTone => IsOn(AudioVariable)
        ? MissingAudioDevice.Value
        : $"Esta prueba hace sonar un tono de 3 s; se activa con la variable de entorno {AudioVariable}=1.";

    /// <summary>ffmpeg.exe (el que genera los medios de prueba) con un codificador que funcione para cada códec pedido.</summary>
    public static string? Encoders(IEnumerable<VideoCodec> video, AudioCodec audio = AudioCodec.None)
    {
        ArgumentNullException.ThrowIfNull(video);
        FfmpegTool? ffmpeg = FfmpegTool.Default;
        if (ffmpeg is null)
        {
            return "Omitida: " + FfmpegTool.UnavailableReason;
        }

        foreach (VideoCodec codec in video)
        {
            if (ffmpeg.FindVideoEncoder(codec) is null)
            {
                string tried = string.Join(", ", FfmpegTool.VideoEncoderCandidates(codec));
                return $"Omitida: ningún codificador de {codec} funciona con {ffmpeg.ExecutablePath} (se probaron {tried}).";
            }
        }

        if (audio != AudioCodec.None && ffmpeg.ProbeEncoder(FfmpegTool.AudioEncoderName(audio)) is { } reason)
        {
            return "Omitida: " + reason;
        }

        return null;
    }

    /// <summary>
    /// ffmpeg.exe con un codificador concreto, para medios que solo se generan con él. Omite solo con causa de hardware
    /// o de build: el codificador no está en ffmpeg, o es de NVIDIA y el equipo no tiene el controlador (<c>nvcuda.dll</c>).
    /// Un fallo pasajero de la sonda (por ejemplo, el límite de sesiones de NVENC) no omite: la prueba corre y falla.
    /// </summary>
    public static string? Encoder(string encoder)
    {
        FfmpegTool? ffmpeg = FfmpegTool.Default;
        if (ffmpeg is null)
        {
            return "Omitida: " + FfmpegTool.UnavailableReason;
        }

        if (!ffmpeg.IsEncoderListed(encoder))
        {
            return $"Omitida: el medio de prueba solo se genera con {encoder}, que este ffmpeg ({ffmpeg.ExecutablePath}) no incluye.";
        }

        if (encoder.EndsWith("_nvenc", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll")))
        {
            return $"Omitida: el medio de prueba solo se genera con {encoder} y este equipo no tiene el controlador de NVIDIA (nvcuda.dll).";
        }

        return null;
    }

    /// <summary>
    /// El codificador que se usaría para el códec produce cuadros B cuando se le piden, como exigen los presets con
    /// <c>BFrames</c> &gt; 0. libopenh264 no los produce nunca y las pruebas ya lo tienen en cuenta, así que no se omite.
    /// </summary>
    public static string? BFrames(VideoCodec codec) =>
        Encoders([codec]) ?? BFramesReasons.GetOrAdd(codec, c => new Lazy<string?>(() => ProbeBFrames(FfmpegTool.Default!, c))).Value;

    // Sonda propia: dos segundos de testsrc2 a 320x240 con -bf 2. Las opciones de control de cada codificador copian las
    // de TestMediaGenerator.BuildArguments (misma rama por codificador), pero la entrada no es la del generador
    // (testsrc2 en vez de rawvideo yuv420p) ni lleva sus opciones de color, y la rama «resto» añade -bf 2.
    private static string? ProbeBFrames(FfmpegTool ffmpeg, VideoCodec codec)
    {
        string encoder = ffmpeg.FindVideoEncoder(codec)!;
        if (encoder == "libopenh264")
        {
            return null;
        }

        // Si hay un NVENC de más preferencia en el build y el controlador existe pero se eligió otro, la sonda de
        // NVENC falló (p. ej. por el límite de sesiones): no es una causa concreta, la prueba corre y falla.
        if (!encoder.EndsWith("_nvenc", StringComparison.Ordinal)
            && File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"))
            && FfmpegTool.VideoEncoderCandidates(codec)
                .TakeWhile(c => c != encoder)
                .Any(c => c.EndsWith("_nvenc", StringComparison.Ordinal) && ffmpeg.IsEncoderListed(c)))
        {
            return null;
        }

        string[] options = encoder.EndsWith("_nvenc", StringComparison.Ordinal)
            ? ["-preset", "p2", "-rc", "cbr", "-b:v", "2000k", "-g", "25", "-bf", "2", "-no-scenecut", "1", "-strict_gop", "1"]
            : encoder.EndsWith("_mf", StringComparison.Ordinal)
                ? ["-rate_control", "cbr", "-b:v", "2000k", "-g", "25", "-bf", "2"]
                : encoder == "libx264"
                    ? ["-preset", "veryfast", "-b:v", "2000k", "-g", "25", "-keyint_min", "25", "-sc_threshold", "0", "-bf", "2"]
                    : encoder == "libx265"
                        ? ["-preset", "veryfast", "-b:v", "2000k", "-x265-params", "log-level=error:keyint=25:min-keyint=25:scenecut=0:bframes=2"]
                        : ["-b:v", "2000k", "-g", "25", "-bf", "2"];
        string path = Path.Combine(Path.GetTempPath(), $"rodaje-cuadros-b-{Guid.NewGuid():N}.mp4");
        try
        {
            ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2", "-c:v", encoder, .. options, path]);
            IReadOnlyDictionary<char, int>? types = ffmpeg.CountPictureTypes(path);
            if (types is null || types.Values.Sum() == 0 || types.GetValueOrDefault('B') > 0)
            {
                return null;
            }

            return $"Omitida: {encoder}, el codificador de {codec} de este equipo, no produce cuadros B aunque se le pidan "
                + $"({string.Join(", ", types.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"))}), y el preset los exige.";
        }
        catch (FfmpegException)
        {
            // Una sonda que falla no es una causa concreta: no se omite, la prueba corre y falla con su error real.
            return null;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>El primer motivo que no sea <c>null</c>.</summary>
    public static string? First(params string?[] reasons) => reasons.FirstOrDefault(r => r is not null);

    /// <summary>Lo que piden las opciones comunes de los atributos, en este orden: códecs, prueba larga y prueba completa.</summary>
    internal static string? Options(string? requirement, VideoCodec[] video, AudioCodec audio, bool longRunning, bool complete) =>
        requirement
        ?? (video.Length > 0 || audio != AudioCodec.None ? Encoders(video, audio) : null)
        ?? (longRunning || complete ? LongRun : null)
        ?? (complete ? CompleteRun : null);

    private static bool IsOn(string variable) => Environment.GetEnvironmentVariable(variable) == "1";
}

/// <summary>Necesita el ffmpeg.exe que genera los medios de prueba y, si se piden, codificadores que funcionen.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegFactAttribute : FactAttribute
{
    private readonly VideoCodec[] _video;
    private AudioCodec _audio;
    private bool _longRunning;
    private bool _requiresBFrames;

    public FfmpegFactAttribute(params VideoCodec[] video)
    {
        _video = video;
        Skip = Reason();
    }

    public FfmpegFactAttribute(VideoCodec video, AudioCodec audio)
    {
        _video = [video];
        _audio = audio;
        Skip = Reason();
    }

    /// <summary>Prueba larga: solo corre con <c>RODAJE_PRUEBAS_LARGAS=1</c> (ABRIR.bat aceptacion).</summary>
    public bool LongRunning
    {
        get => _longRunning;
        set
        {
            _longRunning = value;
            Skip = Reason();
        }
    }

    /// <summary>El medio lleva cuadros B: se omite si el codificador de alguno de los códecs no los produce.</summary>
    public bool RequiresBFrames
    {
        get => _requiresBFrames;
        set
        {
            _requiresBFrames = value;
            Skip = Reason();
        }
    }

    // Sin códecs pedidos, Options no mira ffmpeg.exe: sin esta comprobación la prueba correría sin él.
    private string? Reason() => (FfmpegTool.Default is null ? "Omitida: " + FfmpegTool.UnavailableReason : null)
        ?? Rodaje.TestMedia.Skip.Options(null, _video, _audio, _longRunning, complete: false)
        ?? (_requiresBFrames ? Rodaje.TestMedia.Skip.First([.. _video.Select(Rodaje.TestMedia.Skip.BFrames)]) : null);
}

/// <summary>Teoría que necesita el ffmpeg.exe que genera los medios de prueba.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute(params VideoCodec[] video) =>
        Skip = Rodaje.TestMedia.Skip.Options(null, video, AudioCodec.None, longRunning: false, complete: false);
}

/// <summary>
/// Necesita las DLL de FFmpeg (con ffmpeg.exe y ffprobe.exe al lado) y, si se piden, codificadores para generar
/// los medios; con <see cref="LongRunning"/> o <see cref="Complete"/>, además su variable de entorno.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegLibraryFactAttribute : FactAttribute
{
    private readonly VideoCodec[] _video;
    private AudioCodec _audio;
    private bool _longRunning;
    private bool _complete;
    private string? _encoder;

    public FfmpegLibraryFactAttribute(params VideoCodec[] video)
    {
        _video = video;
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _audio;
        set
        {
            _audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _longRunning;
        set
        {
            _longRunning = value;
            Skip = Reason();
        }
    }

    /// <summary>Los 10 minutos en tiempo real: <c>RODAJE_PRUEBAS_COMPLETAS=1</c> (implica prueba larga).</summary>
    public bool Complete
    {
        get => _complete;
        set
        {
            _complete = value;
            Skip = Reason();
        }
    }

    /// <summary>Un codificador concreto que debe funcionar: el único con el que se genera el medio de la prueba.</summary>
    public string? Encoder
    {
        get => _encoder;
        set
        {
            _encoder = value;
            Skip = Reason();
        }
    }

    private string? Reason() => Rodaje.TestMedia.Skip.First(
        Rodaje.TestMedia.Skip.Options(Rodaje.TestMedia.Skip.FfmpegLibraries, _video, _audio, _longRunning, _complete),
        _encoder is null ? null : Rodaje.TestMedia.Skip.Encoder(_encoder));
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegLibraryTheoryAttribute : TheoryAttribute
{
    private readonly VideoCodec[] _video;
    private AudioCodec _audio;
    private bool _longRunning;

    public FfmpegLibraryTheoryAttribute(params VideoCodec[] video)
    {
        _video = video;
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _audio;
        set
        {
            _audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _longRunning;
        set
        {
            _longRunning = value;
            Skip = Reason();
        }
    }

    private string? Reason() =>
        Rodaje.TestMedia.Skip.Options(Rodaje.TestMedia.Skip.FfmpegLibraries, _video, _audio, _longRunning, complete: false);
}

/// <summary>Necesita FFmpeg y una GPU con D3D11VA; con códecs, también ffmpeg.exe para generar el clip.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareFactAttribute : FactAttribute
{
    private readonly VideoCodec[] _video;
    private AudioCodec _audio;
    private bool _longRunning;
    private string? _encoder;

    public HardwareFactAttribute(params VideoCodec[] video)
    {
        _video = video;
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _audio;
        set
        {
            _audio = value;
            Skip = Reason();
        }
    }

    public bool LongRunning
    {
        get => _longRunning;
        set
        {
            _longRunning = value;
            Skip = Reason();
        }
    }

    /// <summary>Un codificador concreto que debe funcionar: el único con el que se genera el medio de la prueba.</summary>
    public string? Encoder
    {
        get => _encoder;
        set
        {
            _encoder = value;
            Skip = Reason();
        }
    }

    private string? Reason() => Rodaje.TestMedia.Skip.First(
        Rodaje.TestMedia.Skip.Options(Rodaje.TestMedia.Skip.HardwareDecoding, _video, _audio, _longRunning, complete: false),
        _encoder is null ? null : Rodaje.TestMedia.Skip.Encoder(_encoder));
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareTheoryAttribute : TheoryAttribute
{
    private readonly VideoCodec[] _video;
    private AudioCodec _audio;

    public HardwareTheoryAttribute(params VideoCodec[] video)
    {
        _video = video;
        Skip = Reason();
    }

    public AudioCodec Audio
    {
        get => _audio;
        set
        {
            _audio = value;
            Skip = Reason();
        }
    }

    private string? Reason() =>
        Rodaje.TestMedia.Skip.Options(Rodaje.TestMedia.Skip.HardwareDecoding, _video, _audio, longRunning: false, complete: false);
}

/// <summary>Necesita Direct3D 11 por hardware con texturas NV12 y P010.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute() => Skip = Rodaje.TestMedia.Skip.GpuTextures;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GpuTheoryAttribute : TheoryAttribute
{
    public GpuTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.GpuTextures;
}

/// <summary>Necesita lo que hace falta para generar proxies.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ProxyFactAttribute : FactAttribute
{
    public ProxyFactAttribute() => Skip = Rodaje.TestMedia.Skip.Proxies;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ProxyTheoryAttribute : TheoryAttribute
{
    public ProxyTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.Proxies;
}

/// <summary>Necesita NVENC. Con <see cref="LongRunning"/>, además <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NvencFactAttribute : FactAttribute
{
    private bool _longRunning;

    public NvencFactAttribute() => Skip = Rodaje.TestMedia.Skip.Nvenc;

    public bool LongRunning
    {
        get => _longRunning;
        set => (_longRunning, Skip) = (value, Rodaje.TestMedia.Skip.Options(Rodaje.TestMedia.Skip.Nvenc, [], AudioCodec.None, value, complete: false));
    }
}

/// <summary>Necesita un dispositivo de salida de audio real (reproduce silencio) y <c>RODAJE_PRUEBAS_LARGAS=1</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute() => Skip = Rodaje.TestMedia.Skip.AudioDevice;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AudioDeviceTheoryAttribute : TheoryAttribute
{
    public AudioDeviceTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.AudioDevice;
}

/// <summary>Hace ruido: solo corre con <c>RODAJE_PRUEBAS_AUDIO=1</c> y un dispositivo real.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AudioToneFactAttribute : FactAttribute
{
    public AudioToneFactAttribute() => Skip = Rodaje.TestMedia.Skip.AudioTone;
}
