using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Rodaje.Media.Proxies;

/// <summary>El <c>ffmpeg.exe</c> LGPL que viaja junto a las DLL de FFmpeg, verificado antes de usarlo.</summary>
/// <remarks>
/// Solo se acepta el exe de la misma carpeta de la que <see cref="FfmpegLibrary"/> cargó las DLL (nunca el del
/// <c>PATH</c>), y solo si su <c>-buildconf</c> no lleva <c>--enable-gpl</c> ni <c>--enable-nonfree</c>, su
/// <c>-L</c> declara la LGPL, su libavcodec es de la serie que exige Rodaje y las DLL que importa además
/// (<see cref="ExtraLibraries"/>) son de la misma compilación LGPL que las que cargó Rodaje. La verificación se repite
/// si el exe o esas DLL cambian de tamaño o de fecha.
/// </remarks>
public sealed class FfmpegExecutable
{
    public const string FileName = "ffmpeg.exe";

    /// <summary>DLL que <c>ffmpeg.exe</c> importa además de las que carga Rodaje.</summary>
    public static IReadOnlyList<string> ExtraLibraries { get; } = ["avfilter-12", "avdevice-63"];

    private static readonly ConcurrentDictionary<string, Lazy<FfmpegExecutable>> Verified = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<FfmpegExecutable>> VerifiedGpl = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<string?>> _encoderProbes = new(StringComparer.Ordinal);

    private readonly string _stamp;

    private FfmpegExecutable(string path, string version, string configuration, string stamp, bool gplAddOn = false)
    {
        Path = path;
        Version = version;
        Configuration = configuration;
        _stamp = stamp;
        IsGplAddOn = gplAddOn;
    }

    public string Path { get; }

    /// <summary>
    /// Si es el <c>ffmpeg.exe</c> GPL del complemento <c>rodaje-codificadores-gpl</c>: estático, sin DLL, solo para
    /// codificar x264 y x265 en su propio proceso (<see cref="OpenGplAddOn"/>). El editor nunca carga sus bibliotecas.
    /// </summary>
    public bool IsGplAddOn { get; }

    /// <summary>Primera línea de <c>ffmpeg -version</c>.</summary>
    public string Version { get; }

    /// <summary>Línea de <c>configure</c> que imprime <c>-buildconf</c>, en una sola línea.</summary>
    public string Configuration { get; }

    /// <summary>
    /// El <c>ffmpeg.exe</c> junto a las DLL que cargó <see cref="FfmpegLibrary"/>, ya verificado.
    /// </summary>
    /// <exception cref="FfmpegNotFoundException">No hay FFmpeg, falta el exe o no es una compilación LGPL válida.</exception>
    public static FfmpegExecutable Locate()
    {
        FfmpegLibrary.EnsureInitialized();
        return Open(System.IO.Path.Combine(FfmpegLibrary.Directory!, FileName));
    }

    /// <summary>Como <see cref="Locate"/>, pero devuelve el motivo en español en vez de lanzar.</summary>
    public static FfmpegExecutable? TryLocate(out string? failureReason)
    {
        try
        {
            FfmpegExecutable executable = Locate();
            failureReason = null;
            return executable;
        }
        catch (FfmpegNotFoundException ex)
        {
            failureReason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Verifica un <c>ffmpeg.exe</c> concreto (una vez por ruta y proceso, y otra vez si el exe o sus DLL extra
    /// cambiaron de tamaño o de fecha desde la última verificación).
    /// </summary>
    /// <exception cref="FfmpegNotFoundException">El exe falta, no arranca o no es LGPL.</exception>
    public static FfmpegExecutable Open(string path) => OpenVerified(path, Verified, Verify);

    /// <summary>
    /// Verifica el <c>ffmpeg.exe</c> del complemento <c>rodaje-codificadores-gpl</c>: compilación estática (sin DLL), GPL
    /// o LGPL compatible con la GPLv3, nunca <c>nonfree</c>, con el libavcodec de la serie de Rodaje. Solo corre como
    /// proceso aparte, la frontera que separa su licencia del editor.
    /// </summary>
    /// <exception cref="FfmpegNotFoundException">El exe falta, no arranca, es <c>nonfree</c> o es de otra serie.</exception>
    public static FfmpegExecutable OpenGplAddOn(string path) => OpenVerified(path, VerifiedGpl, VerifyGplAddOn);

    private static FfmpegExecutable OpenVerified(string path, ConcurrentDictionary<string, Lazy<FfmpegExecutable>> cache, Func<string, FfmpegExecutable> verify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        while (true)
        {
            Lazy<FfmpegExecutable> entry = cache.GetOrAdd(full, p => new Lazy<FfmpegExecutable>(() => verify(p)));
            FfmpegExecutable executable;
            try
            {
                executable = entry.Value;
            }
            catch (FfmpegNotFoundException)
            {
                // Si el exe aparece después (preparar-entorno), un nuevo intento debe volver a verificar.
                cache.TryRemove(new KeyValuePair<string, Lazy<FfmpegExecutable>>(full, entry));
                throw;
            }

            if (executable.IsUnchanged)
            {
                return executable;
            }

            cache.TryRemove(new KeyValuePair<string, Lazy<FfmpegExecutable>>(full, entry));
        }
    }

    /// <summary>Si el exe y sus DLL extra siguen siendo los que se verificaron (mismo tamaño y fecha).</summary>
    public bool IsUnchanged => Stamp(Path, IsGplAddOn ? [] : ExtraLibraries) == _stamp;

    /// <summary>Este mismo exe si no cambió desde que se verificó; si cambió, lo vuelve a verificar.</summary>
    /// <exception cref="FfmpegNotFoundException">Cambió y ya no pasa la verificación.</exception>
    public FfmpegExecutable EnsureUnchanged() => IsUnchanged ? this : IsGplAddOn ? OpenGplAddOn(Path) : Open(Path);

    /// <summary>Motivo en español por el que la salida de <c>-buildconf</c>, <c>-L</c> y <c>-version</c> no es aceptable.</summary>
    internal static string? BuildProblem(string buildConfiguration, string licenseText, string versionText)
    {
        string license = licenseText.Contains("GNU Lesser General Public License", StringComparison.Ordinal) ? "LGPL"
            : licenseText.Contains("GNU General Public License", StringComparison.Ordinal) ? "GPL"
            : licenseText.Contains("nonfree", StringComparison.OrdinalIgnoreCase) ? "nonfree"
            : "desconocida";
        string? problem = FfmpegLibrary.LicenseProblem(license, [("ffmpeg.exe", buildConfiguration)]);
        if (problem is not null)
        {
            return problem;
        }

        return AvcodecProblem(versionText);
    }

    /// <summary>
    /// Como <see cref="BuildProblem"/> para el exe del complemento GPL: admite GPL o LGPL de la versión 3 o «cualquier
    /// posterior» (compatibles con la GPLv3 de Rodaje) y rechaza <c>--enable-nonfree</c>, que no se puede redistribuir.
    /// </summary>
    internal static string? GplAddOnBuildProblem(string buildConfiguration, string licenseText, string versionText)
    {
        if (buildConfiguration.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("--enable-nonfree", StringComparer.Ordinal)
            || licenseText.Contains("nonfree", StringComparison.OrdinalIgnoreCase))
        {
            return "El ffmpeg.exe del complemento se compiló con --enable-nonfree y no se puede usar ni redistribuir.";
        }

        if (!licenseText.Contains("General Public License", StringComparison.Ordinal)
            || (!licenseText.Contains("version 3", StringComparison.Ordinal) && !licenseText.Contains("any later version", StringComparison.Ordinal)))
        {
            return "El ffmpeg.exe del complemento no declara una licencia GPL o LGPL compatible con la GPLv3.";
        }

        return AvcodecProblem(versionText);
    }

    private static string? AvcodecProblem(string versionText)
    {
        int expected = FfmpegLibrary.ExpectedMajors["avcodec"];
        foreach (string line in versionText.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("libavcodec", StringComparison.Ordinal))
            {
                string[] parts = trimmed["libavcodec".Length..].Split('.', 2);
                return int.TryParse(parts[0].Trim(), out int major) && major == expected
                    ? null
                    : $"El ffmpeg.exe usa libavcodec {parts[0].Trim()} y Rodaje necesita la {expected}.x (FFmpeg 9).";
            }
        }

        return "No se pudo leer la versión de libavcodec de ffmpeg.exe.";
    }

    /// <summary>Si el codificador está en el exe y además codifica unos cuadros de verdad en este equipo.</summary>
    public bool IsEncoderUsable(string encoder) => ProbeEncoder(encoder) is null;

    /// <summary><c>null</c> si el codificador funciona; si no, el motivo.</summary>
    public string? ProbeEncoder(string encoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoder);
        return _encoderProbes.GetOrAdd(encoder, e => new Lazy<string?>(() => RunEncoderProbe(e))).Value;
    }

    internal (int ExitCode, string Output, string Error) Run(IEnumerable<string> arguments, TimeSpan timeout) =>
        RunProcess(Path, arguments, timeout);

    private static FfmpegExecutable Verify(string path)
    {
        string directory = System.IO.Path.GetDirectoryName(path)!;
        if (!File.Exists(path))
        {
            throw new FfmpegNotFoundException(
                $"No existe {path}. Los proxies necesitan el ffmpeg.exe LGPL junto a las DLL; ejecuta Probar.bat preparar-entorno.");
        }

        string? missing = ExtraLibraries.FirstOrDefault(l => !File.Exists(System.IO.Path.Combine(directory, l + ".dll")));
        if (missing is not null)
        {
            throw new FfmpegNotFoundException($"Falta {missing}.dll junto a {path}; ffmpeg.exe la necesita para arrancar.");
        }

        // Antes de ejecutar nada: si el exe cambia durante la verificación, la siguiente apertura lo vuelve a verificar.
        string stamp = Stamp(path, ExtraLibraries);
        foreach (string library in ExtraLibraries)
        {
            if (FfmpegLibrary.CompanionLibraryProblem(System.IO.Path.Combine(directory, library + ".dll")) is { } companion)
            {
                throw new FfmpegNotFoundException($"{companion} Rodaje solo puede usar un FFmpeg LGPL ({path}).");
            }
        }

        TimeSpan timeout = TimeSpan.FromSeconds(20);
        (string buildconf, string license, string version) = (
            Capture(path, ["-hide_banner", "-buildconf"], timeout),
            Capture(path, ["-hide_banner", "-L"], timeout),
            Capture(path, ["-hide_banner", "-version"], timeout));
        string configuration = string.Join(' ', buildconf
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("--", StringComparison.Ordinal)));
        if (BuildProblem(configuration, license, version) is { } problem)
        {
            throw new FfmpegNotFoundException($"{problem} Rodaje solo puede usar un FFmpeg LGPL ({path}).");
        }

        string firstLine = version.Split('\n', 2)[0].Trim();
        return new FfmpegExecutable(path, firstLine, configuration, stamp);
    }

    private static FfmpegExecutable VerifyGplAddOn(string path)
    {
        if (!File.Exists(path))
        {
            throw new FfmpegNotFoundException($"No existe {path}. Reinstala el complemento de codificadores GPL.");
        }

        string stamp = Stamp(path, []);
        TimeSpan timeout = TimeSpan.FromSeconds(20);
        (string buildconf, string license, string version) = (
            Capture(path, ["-hide_banner", "-buildconf"], timeout),
            Capture(path, ["-hide_banner", "-L"], timeout),
            Capture(path, ["-hide_banner", "-version"], timeout));
        string configuration = string.Join(' ', buildconf
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("--", StringComparison.Ordinal)));
        if (GplAddOnBuildProblem(configuration, license, version) is { } problem)
        {
            throw new FfmpegNotFoundException($"{problem} ({path})");
        }

        return new FfmpegExecutable(path, version.Split('\n', 2)[0].Trim(), configuration, stamp, gplAddOn: true);
    }

    // Tamaño y fecha del exe y de las DLL extra; una cadena vacía en la parte de un archivo que falta.
    private static string Stamp(string path, IReadOnlyList<string> libraries)
    {
        string directory = System.IO.Path.GetDirectoryName(path)!;
        return string.Join('|', libraries.Select(l => System.IO.Path.Combine(directory, l + ".dll")).Prepend(path).Select(file =>
        {
            var info = new FileInfo(file);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "";
        }));
    }

    private static string Capture(string path, string[] arguments, TimeSpan timeout)
    {
        (int exitCode, string output, string error) = RunProcess(path, arguments, timeout);
        if (exitCode != 0)
        {
            throw new FfmpegNotFoundException(
                $"{path} {string.Join(' ', arguments)} terminó con código {exitCode}: {Tail(error)}");
        }

        return output;
    }

    private string? RunEncoderProbe(string encoder)
    {
        (int exitCode, _, string error) = RunProcess(
            Path,
            [
                "-hide_banner", "-nostdin", "-loglevel", "error",
                "-f", "lavfi", "-i", "color=c=gray:s=320x240:r=25:d=0.4",
                "-c:v", encoder, .. ProxyEncoders.PixelFormatArguments(encoder), "-f", "null", "-",
            ],
            TimeSpan.FromSeconds(30));
        return exitCode == 0 ? null : $"El codificador {encoder} no funciona en este equipo: {Tail(error)}";
    }

    private static (int ExitCode, string Output, string Error) RunProcess(string path, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = System.IO.Path.GetDirectoryName(path)!,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegNotFoundException($"No se pudo ejecutar {path}: {ex.Message}", ex);
        }

        ChildProcessJob.TryAssign(process);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return (-1, output.GetAwaiter().GetResult(), $"no respondió en {timeout.TotalSeconds:0} s.");
        }

        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult().Replace("\r", "", StringComparison.Ordinal),
            error.GetAwaiter().GetResult().Replace("\r", "", StringComparison.Ordinal));
    }

    internal static string Tail(string text, int maxLines = 6)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }
}
