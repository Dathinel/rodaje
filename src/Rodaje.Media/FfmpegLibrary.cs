using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;

namespace Rodaje.Media;

/// <summary>Carga única de las DLL de FFmpeg 9 por ruta absoluta, con comprobación de versión y de licencia LGPL.</summary>
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
            explicitDirectory, AppContext.BaseDirectory, EnvironmentVariables.Read);
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
        string? licenseProblem = LicenseProblem(
            ffmpeg.avcodec_license(),
            [
                ("avutil", ffmpeg.avutil_configuration()),
                ("avcodec", ffmpeg.avcodec_configuration()),
                ("avformat", ffmpeg.avformat_configuration()),
                ("swresample", ffmpeg.swresample_configuration()),
                ("swscale", ffmpeg.swscale_configuration()),
            ]);
        if (licenseProblem is not null)
        {
            throw new FfmpegNotFoundException($"{licenseProblem} Rodaje solo puede cargar un FFmpeg LGPL. "
                + $"Bibliotecas en {directory}. {MissingMessage}");
        }

        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_FATAL);
        _directory = directory;
    }

    /// <summary>Motivo en español por el que un FFmpeg no es LGPL, o <c>null</c> si lo es.</summary>
    /// <remarks>Rechaza <c>--enable-gpl</c> o <c>--enable-nonfree</c> en la configuración de cualquier biblioteca.</remarks>
    internal static string? LicenseProblem(string? license, IReadOnlyList<(string Library, string? Configuration)> configurations)
    {
        foreach ((string library, string? configuration) in configurations)
        {
            string[] flags = (configuration ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (string forbidden in (string[])["--enable-gpl", "--enable-nonfree"])
            {
                if (flags.Contains(forbidden, StringComparer.Ordinal))
                {
                    return $"El FFmpeg encontrado se compiló con {forbidden} ({library}).";
                }
            }
        }

        if (license is null || !license.StartsWith("LGPL", StringComparison.Ordinal))
        {
            return $"El FFmpeg encontrado tiene licencia \"{license}\" y no LGPL.";
        }

        return null;
    }

    /// <summary>
    /// Motivo en español por el que una DLL de FFmpeg que Rodaje no carga (por ejemplo <c>avfilter-12.dll</c>, que
    /// importa <c>ffmpeg.exe</c>) no es de la misma compilación LGPL que las ya cargadas, o <c>null</c> si lo es.
    /// </summary>
    /// <param name="path">Ruta absoluta de la DLL, con el nombre <c>&lt;biblioteca&gt;-&lt;mayor&gt;.dll</c>.</param>
    internal static unsafe string? CompanionLibraryProblem(string path)
    {
        EnsureInitialized();
        string name = Path.GetFileNameWithoutExtension(path);
        string[] parts = name.Split('-', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out int expectedMajor))
        {
            return $"{name}.dll no tiene el nombre de una biblioteca de FFmpeg.";
        }

        string library = parts[0];
        nint handle;
        try
        {
            handle = NativeLibrary.Load(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            return $"No se pudo cargar {path}: {ex.Message}";
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, library + "_version", out nint version)
                || !NativeLibrary.TryGetExport(handle, library + "_configuration", out nint configuration)
                || !NativeLibrary.TryGetExport(handle, library + "_license", out nint license))
            {
                return $"{path} no exporta {library}_version, {library}_configuration y {library}_license.";
            }

            return CompanionProblem(
                name,
                expectedMajor,
                ((delegate* unmanaged<uint>)version)(),
                Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<byte*>)configuration)()),
                Marshal.PtrToStringUTF8((nint)((delegate* unmanaged<byte*>)license)()),
                ffmpeg.avcodec_configuration());
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>Compara una DLL compañera con la compilación cargada: misma serie, misma configuración y LGPL.</summary>
    internal static string? CompanionProblem(string name, int expectedMajor, uint version, string? configuration, string? license, string? reference)
    {
        int major = (int)(version >> 16);
        if (major != expectedMajor)
        {
            return $"{name}.dll es la versión {major}.{(version >> 8) & 0xFF}.{version & 0xFF} y su nombre dice {expectedMajor}.";
        }

        if (LicenseProblem(license, [(name, configuration)]) is { } problem)
        {
            return problem;
        }

        return string.Equals(configuration?.Trim(), reference?.Trim(), StringComparison.Ordinal)
            ? null
            : $"{name}.dll no es de la misma compilación de FFmpeg que las bibliotecas cargadas (su configuración es distinta).";
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
                $"La versión de {library} cargada es la {major}.{(version >> 8) & 0xFF}.{version & 0xFF} y Rodaje "
                + $"necesita la {expected}.x (FFmpeg 9). {MissingMessage}");
        }
    }
}

/// <summary>FFmpeg no está disponible o no es la versión que Rodaje necesita.</summary>
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
