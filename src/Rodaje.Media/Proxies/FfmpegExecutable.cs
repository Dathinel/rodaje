using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Rodaje.Media.Proxies;

/// <summary>El <c>ffmpeg.exe</c> LGPL que viaja junto a las DLL de FFmpeg, verificado antes de usarlo.</summary>
/// <remarks>
/// Solo se acepta el exe de la misma carpeta de la que <see cref="FfmpegLibrary"/> cargó las DLL (nunca el del
/// <c>PATH</c>), y solo si su <c>-buildconf</c> no lleva <c>--enable-gpl</c> ni <c>--enable-nonfree</c>, su
/// <c>-L</c> declara la LGPL y su libavcodec es de la serie que exige Rodaje.
/// </remarks>
public sealed class FfmpegExecutable
{
    public const string FileName = "ffmpeg.exe";

    /// <summary>DLL que <c>ffmpeg.exe</c> importa además de las que carga Rodaje.</summary>
    public static IReadOnlyList<string> ExtraLibraries { get; } = ["avfilter-12", "avdevice-63"];

    private static readonly ConcurrentDictionary<string, Lazy<FfmpegExecutable>> Verified = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<string?>> _encoderProbes = new(StringComparer.Ordinal);

    private FfmpegExecutable(string path, string version, string configuration)
    {
        Path = path;
        Version = version;
        Configuration = configuration;
    }

    public string Path { get; }

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

    /// <summary>Verifica un <c>ffmpeg.exe</c> concreto (una vez por ruta y proceso).</summary>
    /// <exception cref="FfmpegNotFoundException">El exe falta, no arranca o no es LGPL.</exception>
    public static FfmpegExecutable Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        Lazy<FfmpegExecutable> entry = Verified.GetOrAdd(full, p => new Lazy<FfmpegExecutable>(() => Verify(p)));
        try
        {
            return entry.Value;
        }
        catch (FfmpegNotFoundException)
        {
            // Si el exe aparece después (preparar-entorno), un nuevo intento debe volver a verificar.
            Verified.TryRemove(new KeyValuePair<string, Lazy<FfmpegExecutable>>(full, entry));
            throw;
        }
    }

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
                $"No existe {path}. Los proxies necesitan el ffmpeg.exe LGPL junto a las DLL; ejecuta ABRIR.bat preparar-entorno.");
        }

        string? missing = ExtraLibraries.FirstOrDefault(l => !File.Exists(System.IO.Path.Combine(directory, l + ".dll")));
        if (missing is not null)
        {
            throw new FfmpegNotFoundException($"Falta {missing}.dll junto a {path}; ffmpeg.exe la necesita para arrancar.");
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
            throw new FfmpegNotFoundException($"{problem} Rodaje es MIT y solo puede usar un FFmpeg LGPL ({path}).");
        }

        string firstLine = version.Split('\n', 2)[0].Trim();
        return new FfmpegExecutable(path, firstLine, configuration);
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
