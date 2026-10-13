using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Quita los datos personales del texto de un informe de fallo, que la app invita a pegar en GitHub (público):
/// <list type="bullet">
/// <item>de una ruta a un medio, un proyecto o un recurso del usuario (video, audio, imagen, subtítulo, LUT,
/// <c>.rodaje</c>) deja solo el nombre del archivo;</item>
/// <item>las carpetas del usuario (<c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, <c>%TEMP%</c>, <c>%USERPROFILE%</c>) pasan a
/// su variable, sin distinguir mayúsculas y con <c>\</c> o <c>/</c>;</item>
/// <item>cualquier otro <c>X:\Users\&lt;nombre&gt;</c> (otra unidad, el nombre corto 8.3, otra sesión) pasa a
/// <c>%USERPROFILE%</c>.</item>
/// </list>
/// Se aplica a todo el informe: el error, la pila, la línea de comandos del arranque y las líneas del registro. Las
/// rutas de compilación de las pilas (<c>D:\a\rodaje\…\Archivo.cs:line 12</c>) no son del usuario y quedan como están.
/// Nunca lanza: si una expresión tarda demasiado, se queda con lo que ya limpió y sigue con el paso siguiente.
/// </summary>
public sealed class CrashReportScrubber
{
    /// <summary>Extensiones de los archivos cuya carpeta no se muestra (solo el nombre).</summary>
    public static readonly IReadOnlyList<string> MediaExtensions =
    [
        // Video.
        "mp4", "mkv", "mov", "m4v", "webm", "avi", "mts", "m2ts", "ts", "wmv", "flv", "3gp", "mxf", "mpg", "mpeg", "m2v",
        "vob", "dv", "r3d", "braw", "insv", "360",
        // Audio.
        "mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "wma", "aif", "aiff", "ac3", "mka",
        // Imagen.
        "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "avif", "svg", "exr", "dpx", "psd",
        // Subtítulos, LUT y proyectos.
        "srt", "vtt", "ass", "ssa", "cube", "3dl", "rodaje",
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // Comienzo de una ruta absoluta: unidad (con «\\?\» delante o no) o UNC «\\servidor\». No empieza dentro de una
    // palabra («abc:\» no es una unidad).
    private const string RootPattern = @"(?<![\p{L}\p{N}])(?:(?:\\\\[?.]\\)?[A-Za-z]:|\\\\[^\\/\s:*?""<>|]+)[\\/]+";

    // Un nombre dentro de una ruta: puede llevar espacios y tildes, no los caracteres prohibidos en Windows ni saltos.
    private const string SegmentChars = @"[^\\/:*?""<>|\r\n]";

    private static readonly Regex MediaPath = new(
        RootPattern + "(?:" + SegmentChars + @"+[\\/]+)*?" + "(?<name>" + SegmentChars + @"*?\.(?:" + string.Join('|', MediaExtensions) + @"))(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, Timeout);

    // «X:\Users\<nombre>»: el nombre puede llevar espacios si le sigue una barra; al final, sin espacios.
    private static readonly Regex AnyProfile = new(
        @"(?<![\p{L}\p{N}])[A-Za-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+(?!(?:Public|Default|All Users)(?:[\\/]|$|\s))"
            + "(?:" + SegmentChars + @"+?(?=[\\/])|[^\\/:*?""<>|\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);

    private readonly IReadOnlyList<(Regex Pattern, string Token)> _folders;

    /// <summary>
    /// <paramref name="folders"/>: carpeta del usuario → variable que la sustituye (<c>%TEMP%</c>…). Las vacías se
    /// ignoran; las más largas se sustituyen primero (<c>%TEMP%</c> suele estar dentro de <c>%LOCALAPPDATA%</c>).
    /// </summary>
    public CrashReportScrubber(IEnumerable<KeyValuePair<string, string?>> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = [.. folders
            .Select(f => (Path: Trim(f.Value), Token: f.Key))
            .Where(f => f.Path.Length >= 3)
            .DistinctBy(f => f.Path.Replace('/', '\\').ToUpperInvariant())
            .OrderByDescending(f => f.Path.Length)
            .Select(f => (FolderPattern(f.Path), f.Token))];
    }

    /// <summary>El de este usuario, con las variables de entorno de este proceso.</summary>
    public static CrashReportScrubber Default { get; } = FromEnvironment();

    /// <summary>Con las carpetas de las variables de entorno de este proceso.</summary>
    public static CrashReportScrubber FromEnvironment()
    {
        string[] names = ["LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "USERPROFILE"];
        return new CrashReportScrubber(names.Select(n => new KeyValuePair<string, string?>(
            n == "TMP" ? "%TEMP%" : "%" + n + "%", Environment.GetEnvironmentVariable(n))));
    }

    /// <summary>El texto sin rutas de medios ni carpetas del usuario.</summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        text = TryReplace(text, MediaPath, m => m.Groups["name"].Value);
        foreach ((Regex pattern, string token) in _folders)
        {
            text = TryReplace(text, pattern, _ => token);
        }

        return TryReplace(text, AnyProfile, _ => "%USERPROFILE%");
    }

    private static string TryReplace(string text, Regex pattern, MatchEvaluator evaluator)
    {
        try
        {
            return pattern.Replace(text, evaluator);
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    private static string Trim(string? path) => (path ?? string.Empty).Trim().TrimEnd('\\', '/');

    // La carpeta con «\» o «/» en cada separador, sin distinguir mayúsculas, y sin seguir dentro de un nombre más largo
    // («C:\Users\ana» no toca «C:\Users\anabel»).
    private static Regex FolderPattern(string path)
    {
        var pattern = new StringBuilder(@"(?<![\p{L}\p{N}])");
        foreach (char c in path)
        {
            pattern.Append(c is '\\' or '/' ? @"[\\/]+" : Regex.Escape(c.ToString()));
        }

        pattern.Append(@"(?![\p{L}\p{N}_.~$\-])");
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
    }
}
