using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;

namespace FotoToma.Media;

/// <summary>Carga única de las DLL de FFmpeg 9 por ruta absoluta, con comprobación de versión.</summary>
/// <remarks>
/// El resolver de DllImport nunca devuelve 0: si lo hiciera, Windows buscaría la DLL en el <c>PATH</c>,
/// donde puede haber un FFmpeg GPL o de otra versión.
/// </remarks>
public static class FfmpegLibrary
{
    private const string MissingMessage = "Faltan las bibliotecas de FFmpeg; ejecuta ABRIR.bat preparar-entorno.";

    private static readonly Lock Gate = new();
    private static string? _directory;
    private static Exception? _failure;

    /// <summary>Versiones mayores que exige FFmpeg.AutoGen 9.</summary>
    internal static IReadOnlyDictionary<string, int> ExpectedMajors { get; } = new Dictionary<string, int>
    {
        ["avutil"] = 61,
        ["avcodec"] = 63,
        ["avformat"] = 63,
        ["swresample"] = 7,
        ["swscale"] = 10,
    };

    /// <summary>Carpeta de la que se cargaron las DLL, o <c>null</c> si todavía no se cargaron.</summary>
    public static string? Directory
    {
        get
        {
            lock (Gate)
            {
                return _directory;
            }
        }
    }

    public static bool IsInitialized => Directory is not null;

    /// <summary>Versión de FFmpeg cargada, como la reporta <c>av_version_info</c>.</summary>
    public static string VersionInfo
    {
        get
        {
            EnsureInitialized();
            return ffmpeg.av_version_info();
        }
    }

    /// <summary>Carga FFmpeg si hace falta. Llamadas posteriores no hacen nada (o repiten el mismo error).</summary>
    /// <exception cref="FfmpegNotFoundException">No están las DLL o su versión no es la esperada.</exception>
    public static void EnsureInitialized(string? explicitDirectory = null)
    {
        lock (Gate)
        {
            if (_directory is not null)
            {
                return;
            }

            if (_failure is not null)
            {
                throw new FfmpegNotFoundException(_failure.Message, _failure);
            }

            try
            {
                Initialize(explicitDirectory);
            }
            catch (Exception ex) when (ex is FfmpegNotFoundException or DllNotFoundException or BadImageFormatException
                or EntryPointNotFoundException)
            {
                _failure = ex as FfmpegNotFoundException ?? new FfmpegNotFoundException($"{MissingMessage} {ex.Message}", ex);
                throw _failure;
            }
        }
    }

    /// <summary>Intenta cargar FFmpeg; devuelve el motivo en español si no se pudo.</summary>
    public static bool TryInitialize(out string? failureReason, string? explicitDirectory = null)
    {
        try
        {
            EnsureInitialized(explicitDirectory);
            failureReason = null;
            return true;
        }
        catch (FfmpegNotFoundException ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private static void Initialize(string? explicitDirectory)
    {
        IReadOnlyList<string> candidates = FfmpegLocator.Candidates(
            explicitDirectory, AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
        string directory = FfmpegLocator.Find(candidates)
            ?? throw new FfmpegNotFoundException(
                $"{MissingMessage} Se buscaron {string.Join(", ", FfmpegLocator.RequiredLibraries)} en: "
                + string.Join("; ", candidates));

        Assembly bindings = typeof(DynamicallyLinkedBindings).Assembly;
        NativeLibrary.SetDllImportResolver(bindings, (name, _, _) => LoadFrom(directory, name));
        DynamicallyLinkedBindings.Initialize();

        CheckMajor("avutil", ffmpeg.avutil_version());
        CheckMajor("avcodec", ffmpeg.avcodec_version());
        CheckMajor("avformat", ffmpeg.avformat_version());
        CheckMajor("swresample", ffmpeg.swresample_version());
        CheckMajor("swscale", ffmpeg.swscale_version());
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_FATAL);
        _directory = directory;
    }

    private static nint LoadFrom(string directory, string name)
    {
        string file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        if (!file.Contains('-', StringComparison.Ordinal))
        {
            string prefix = Path.GetFileNameWithoutExtension(file) + "-";
            string? versioned = FfmpegLocator.RequiredLibraries.FirstOrDefault(
                l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (versioned is not null)
            {
                file = versioned + ".dll";
            }
        }

        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
        {
            throw new DllNotFoundException($"No existe {path}.");
        }

        return NativeLibrary.Load(path);
    }

    private static void CheckMajor(string library, uint version)
    {
        int major = (int)(version >> 16);
        int expected = ExpectedMajors[library];
        if (major != expected)
        {
            throw new FfmpegNotFoundException(
                $"La versión de {library} cargada es la {major}.{(version >> 8) & 0xFF}.{version & 0xFF} y FotoToma "
                + $"necesita la {expected}.x (FFmpeg 9). {MissingMessage}");
        }
    }
}

/// <summary>FFmpeg no está disponible o no es la versión que FotoToma necesita.</summary>
public sealed class FfmpegNotFoundException : Exception
{
    public FfmpegNotFoundException()
    {
    }

    public FfmpegNotFoundException(string message)
        : base(message)
    {
    }

    public FfmpegNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
