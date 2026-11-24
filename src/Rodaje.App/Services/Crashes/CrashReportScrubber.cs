using System.Text;
using Rodaje.App.Services.Media;

namespace Rodaje.App.Services.Crashes;

/// <summary>
/// Quita los datos personales del texto de un informe de fallo, que la app invita a pegar en GitHub (público). Funciona
/// con una lista blanca: de toda ruta absoluta (unidad, UNC, <c>\\?\</c>, <c>file:///</c>, también con las barras
/// dobles de un JSON) queda solo su último segmento, salvo en las raíces conocidas, que no son del usuario y ayudan a
/// depurar:
/// <list type="bullet">
/// <item><c>X:\Windows</c>, <c>X:\Program Files</c>, <c>X:\Program Files (x86)</c>, <c>X:\ProgramData</c> y las carpetas
/// comunes de <c>X:\Users</c> (<c>Public</c>, <c>Default</c>…);</item>
/// <item>las rutas de compilación del runner (<c>D:\a\rodaje\…\Archivo.cs:line 12</c>);</item>
/// <item>las variables <c>%…%</c> (<c>%LOCALAPPDATA%\Rodaje\fallos</c>), salvo <c>%USERPROFILE%</c> y las de OneDrive,
/// que solo conservan lo que cuelga de <c>AppData</c>, y <c>%TEMP%</c>, que solo conserva las carpetas de Rodaje
/// (<c>%TEMP%\rodaje-…</c>; las demás quedan en <c>%TEMP%\…\</c>).</item>
/// </list>
/// <c>%SystemDrive%</c>, <c>%HOMEDRIVE%</c> y <c>\Device\HarddiskVolumeN</c> cuentan como una unidad.
/// <para>
/// Una ruta usa como separador el primero que sigue a su raíz. La otra barra, y también la «/» cuando es el separador,
/// solo deja de separar tras un archivo con una extensión conocida seguido de texto («a.mp4 y/o respaldo») y cuando lo que
/// la sigue es claramente frase (un conector como el de «y/o», una cuenta, un paréntesis): así la «/» de «y/o respaldo» o
/// de «12/34)» no se come la frase que sigue a <c>D:\…\a.mp4</c> ni a <c>file:///D:/…/a.mp4</c>, y una carpeta como
/// «Ana.Perez Boda», «Acme S.A. Boda» o «Informe.log final» se tapa (ante la duda, se tapa). El último
/// segmento de una ruta se conserva tal cual (también si no tiene extensión: puede ser un archivo como <c>notas</c>, salvo
/// tras una carpeta «archivo.ext texto», donde tampoco se sabe si es frase o nombre) y
/// termina en su extensión seguida de un espacio, ante « (» o ante dos espacios.
/// </para>
/// Antes, las carpetas del usuario (<c>%LOCALAPPDATA%</c>, <c>%APPDATA%</c>, <c>%TEMP%</c>, <c>%USERPROFILE%</c>) pasan a
/// su variable, y cualquier otro <c>X:\Users\&lt;nombre&gt;</c> a <c>%USERPROFILE%</c>. Fuera de las rutas, el nombre del
/// usuario y el del equipo pasan a <c>%USERNAME%</c> y <c>%COMPUTERNAME%</c>.
/// <para>
/// Se aplica a todo el informe, línea a línea y con código propio de costo lineal (sin expresiones regulares). Una línea
/// de más de <see cref="MaxLineLength"/> caracteres se recorta. Nunca lanza ni falla abierto: si una línea no se puede
/// limpiar o se agota <see cref="Budget"/> (trabajo, no tiempo: el resultado no depende de la carga del equipo), la
/// línea se tapa en vez de quedar como estaba.
/// </para>
/// </summary>
public sealed class CrashReportScrubber
{
    /// <summary>Largo máximo de una línea; lo que sigue se recorta.</summary>
    public const int MaxLineLength = 8192;

    /// <summary>Lo que sustituye a una línea que no se pudo limpiar.</summary>
    public const string HiddenLine = "[línea omitida: no se pudo limpiar]";

    /// <summary>Lo que sustituye a las líneas que quedan cuando se agota <see cref="Budget"/>.</summary>
    public const string OutOfTimeLine = "[línea omitida: se agotó el tope del limpiador]";

    // Raíces de una unidad que no son del usuario: la ruta queda entera.
    private static readonly string[] SystemRoots = ["Windows", "Program Files", "Program Files (x86)", "ProgramData"];

    // Carpetas de «X:\Users» que no son de una persona.
    private static readonly string[] SharedProfiles = ["Public", "Default", "Default User", "All Users"];

    // Variables cuyo contenido es del usuario (documentos, escritorio, OneDrive): solo queda lo de «AppData».
    private static readonly string[] PersonalVariables =
        ["%USERPROFILE%", "%HOMEPATH%", "%ONEDRIVE%", "%ONEDRIVECOMMERCIAL%", "%ONEDRIVECONSUMER%"];

    // Extensiones de archivo tras las que una «/» puede ser de la frase («a.mp4 y/o respaldo»): los medios que importa la
    // app y lo que suele aparecer en un informe. Tras cualquier otro nombre que parezca texto («Acme S.A. Boda»,
    // «Ana.Perez Boda», «Boda.v2 12»), la «/» separa carpetas (tarea 191).
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KnownExtensions =
        new HashSet<string>(
            WatchedFolderScanner.MediaExtensions.Concat(
            [
                ".mpg", ".mpeg", ".mxf", ".vob", ".ogv", ".3g2", ".aif", ".aiff", ".wma", ".ac3",
                ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".tga",
                ".dpx", ".exr", ".srt", ".vtt", ".ass", ".cube", ".lut", ".ttf", ".otf", ".json", ".xml", ".txt", ".log",
                ".dll", ".exe", ".cs", ".zip", ".pdf", ".tmp", ".bin",
            ]),
            StringComparer.OrdinalIgnoreCase).GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly IReadOnlyList<(string[] Components, string Token)> _folders;
    private readonly string? _userName;
    private readonly string? _machineName;

    /// <summary>
    /// <paramref name="folders"/>: carpeta del usuario → variable que la sustituye (<c>%TEMP%</c>…). Las vacías o que no
    /// son rutas absolutas se ignoran; las más largas se sustituyen primero (<c>%TEMP%</c> suele estar dentro de
    /// <c>%LOCALAPPDATA%</c>). <paramref name="userName"/> y <paramref name="machineName"/> (de 3 caracteres o más) se
    /// sustituyen por <c>%USERNAME%</c> y <c>%COMPUTERNAME%</c> en todo el texto, sin distinguir mayúsculas y solo como
    /// palabra entera.
    /// </summary>
    public CrashReportScrubber(IEnumerable<KeyValuePair<string, string?>> folders, string? userName = null, string? machineName = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        _folders = [.. folders
            .Select(f => (Components: FolderComponents(f.Value), Token: f.Key))
            .Where(f => f.Components is not null)
            .Select(f => (Components: f.Components!, f.Token))
            .DistinctBy(f => string.Join('\\', f.Components).ToUpperInvariant())
            .OrderByDescending(f => f.Components.Length)];
        _userName = Name(userName);
        _machineName = Name(machineName);
    }

    /// <summary>El de este usuario, con las variables de entorno de este proceso.</summary>
    public static CrashReportScrubber Default { get; } = FromEnvironment();

    /// <summary>
    /// Trabajo máximo de una llamada a <see cref="Scrub"/>, en las unidades de su <c>work</c>; las líneas que quedan
    /// después se tapan. Con un tope de reloj (2 s), un PC sin CPU justo al fallar tapaba el error y la pila del informe.
    /// Unas 2,4 veces el peor informe realista: 200 líneas de registro de <see cref="MaxLineLength"/> a 4 por carácter.
    /// </summary>
    public long Budget { get; init; } = 16_000_000;

    /// <summary>
    /// Con las carpetas de las variables de entorno de este proceso, el usuario y el equipo. <c>%TEMP%</c> se reconoce
    /// también por <see cref="Path.GetTempPath"/> y, en los dos casos, en su forma 8.3 (<c>C:\Users\ANAPR~1\…</c>) y en
    /// su nombre largo (<c>C:\Users\Ana Pérez\…</c>): una ruta escrita en la otra forma dejaba a la vista sus carpetas
    /// (<c>%LOCALAPPDATA%\Temp\Boda Pérez\…</c>; #263).
    /// </summary>
    public static CrashReportScrubber FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable, Path.GetTempPath, CrashLongPathName.Expand);

    /// <summary>
    /// Como <see cref="FromEnvironment()"/>, con las costuras de las pruebas: <paramref name="variable"/> lee una
    /// variable de entorno, <paramref name="tempPath"/> da la carpeta temporal y <paramref name="longPath"/> expande una
    /// ruta 8.3 a su nombre largo (devuelve la entrada, o <c>null</c>, si no puede).
    /// </summary>
    internal static CrashReportScrubber FromEnvironment(
        Func<string, string?> variable, Func<string> tempPath, Func<string, string?> longPath)
    {
        string[] names = ["LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "USERPROFILE"];
        var folders = new List<KeyValuePair<string, string?>>();
        foreach (string name in names)
        {
            string token = name == "TMP" ? "%TEMP%" : "%" + name + "%";
            string? value = variable(name);
            folders.Add(new(token, value));
            if (token == "%TEMP%")
            {
                AddLongForm(folders, token, value, longPath);
            }
        }

        string? temp = Safe(tempPath);
        folders.Add(new("%TEMP%", temp));
        AddLongForm(folders, "%TEMP%", temp, longPath);
        return new CrashReportScrubber(folders, Safe(() => Environment.UserName), Safe(() => Environment.MachineName));
    }

    // La forma larga de una carpeta, si es distinta de la que vino. Nunca lanza: sin forma larga queda la que hay.
    private static void AddLongForm(List<KeyValuePair<string, string?>> folders, string token, string? folder, Func<string, string?> longPath)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        string? expanded;
        try
        {
            expanded = longPath(folder.Trim());
        }
#pragma warning disable CA1031 // El limpiador no puede fallar por no saber expandir una ruta.
        catch (Exception)
#pragma warning restore CA1031
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(expanded) && !string.Equals(expanded, folder, StringComparison.OrdinalIgnoreCase))
        {
            folders.Add(new(token, expanded));
        }
    }

    /// <summary>El texto sin rutas personales ni nombres del usuario o del equipo.</summary>
    public string Scrub(string? text) => Scrub(text, out _);

    /// <summary>
    /// Como <see cref="Scrub(string?)"/>, y además <paramref name="work"/>: cuántos caracteres recorrió el analizador de
    /// rutas (cada posición de la línea y lo que lee cada intento de ruta desde ella). Mide el coste sin depender del
    /// reloj: las pruebas comprueban que crece en proporción al texto.
    /// </summary>
    internal string Scrub(string? text, out long work)
    {
        work = 0;
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = new StringBuilder(text.Length);
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline;
            if (end > start || newline >= 0)
            {
                result.Append(work >= Budget ? OutOfTimeLine : SafeScrubLine(text[start..end], ref work));
            }

            if (newline < 0)
            {
                break;
            }

            result.Append('\n');
            start = newline + 1;
        }

        return result.ToString();
    }

    private string SafeScrubLine(string line, ref long work)
    {
        try
        {
            string carriageReturn = line.EndsWith('\r') ? "\r" : string.Empty;
            string body = line[..(line.Length - carriageReturn.Length)];
            string suffix = string.Empty;
            if (body.Length > MaxLineLength)
            {
                suffix = $" … [recortado: {body.Length - MaxLineLength} caracteres]";
                body = body[..MaxLineLength];
            }

            return ReplaceNames(ScrubPaths(body, ref work)) + suffix + carriageReturn;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OutOfMemoryException)
        {
            return HiddenLine;
        }
    }

    private string ScrubPaths(string line, ref long work)
    {
        var output = new StringBuilder(line.Length);
        int copied = 0;
        int i = 0;
        while (i < line.Length)
        {
            work++;
            if (!TryParsePath(line, i, out ParsedPath path, ref work))
            {
                i++;
                continue;
            }

            (string replacement, int resume) = Replace(line, path);
            output.Append(line, copied, i - copied).Append(replacement);
            copied = resume;

            // Siempre se avanza: el último segmento se vuelve a recorrer por si empieza otra ruta en él.
            i = resume;
        }

        output.Append(line, copied, line.Length - copied);
        return output.ToString();
    }

    // Qué queda en lugar de [inicio de la ruta, resume): la ruta sin el último segmento (que se copia aparte) o, si el
    // último segmento es el nombre de un perfil, la ruta entera.
    private (string Replacement, int Resume) Replace(string line, ParsedPath path)
    {
        List<Span> components = path.Components;
        string? token = null;
        int matched = 0;

        foreach ((string[] folder, string folderToken) in _folders)
        {
            if (StartsWith(line, components, folder))
            {
                token = folderToken;
                matched = folder.Length;
                break;
            }
        }

        if (token is null && path.Kind == RootKind.Drive && components.Count >= 3
            && (Is(line, components[1], "Users") || Is(line, components[1], "Documents and Settings"))
            && !SharedProfiles.Any(p => Is(line, components[2], p)))
        {
            token = "%USERPROFILE%";
            matched = 3;
        }

        if (token is null && path.Kind == RootKind.Variable)
        {
            token = Text(line, components[0]);
            matched = 1;
        }

        bool tailMatched = path.HasTail && matched == components.Count;
        if (tailMatched)
        {
            // «C:\Users\Ana Perez» al final: el segmento entero es el nombre, con sus espacios.
            return (token!, path.TailEnd);
        }

        // En «%TEMP%» solo quedan las carpetas de Rodaje («rodaje-exportar-3»); las demás pueden llevar nombres del
        // usuario («%TEMP%\Boda Pérez\…»): quedan en «%TEMP%\…\».
        int folders = path.HasTail ? components.Count - 1 : components.Count;
        if (token is not null && matched < folders
            && (token.Equals("%TEMP%", StringComparison.OrdinalIgnoreCase) || token.Equals("%TMP%", StringComparison.OrdinalIgnoreCase))
            && !line.AsSpan(components[matched].Start, components[matched].End - components[matched].Start).StartsWith("rodaje-", StringComparison.OrdinalIgnoreCase))
        {
            return path.HideTail
                ? (token + line[components[matched - 1].End..components[matched].Start] + "…", path.TailEnd)
                : (token + line[components[matched - 1].End..components[matched].Start] + "…"
                    + line[components[folders - 1].End..path.TailStart], path.TailStart);
        }

        bool keep;
        if (token is not null)
        {
            keep = matched == components.Count
                || !PersonalVariables.Contains(token, StringComparer.OrdinalIgnoreCase)
                || (matched < components.Count && Is(line, components[matched], "AppData"));
        }
        else
        {
            keep = path.Kind == RootKind.Drive
                && (components.Count < 2 || !path.HasSegments
                    || SystemRoots.Any(r => Is(line, components[1], r))
                    || (Is(line, components[1], "Users") && components.Count >= 3 && SharedProfiles.Any(p => Is(line, components[2], p)))
                    || (char.ToUpperInvariant(line[components[0].Start]) == 'D' && Is(line, components[1], "a")));
        }

        if (keep)
        {
            if (token is null)
            {
                return (line[path.Start..path.TailStart], path.TailStart);
            }

            return (token + line[components[matched - 1].End..path.TailStart], path.TailStart);
        }

        // Se reduce al último segmento. Si la ruta termina en barra (una carpeta), queda «…\».
        // En «file://servidor/…» quedan las barras de la URL.
        // Tras una carpeta «archivo.ext texto» (HideTail), el último tramo tampoco queda: puede ser frase o nombre.
        string url = path.Kind == RootKind.Unc && path.Start > 0 && line[path.Start - 1] == ':' ? line[components[0].Start..components[0].End] : string.Empty;
        if (path.HideTail)
        {
            return (url + "…", path.TailEnd);
        }

        return (url + (path.HasTail ? string.Empty : "…" + line[path.TailStart - 1]), path.TailStart);
    }

    private static bool StartsWith(string line, List<Span> components, string[] folder)
    {
        if (folder.Length > components.Count)
        {
            return false;
        }

        for (int k = 0; k < folder.Length; k++)
        {
            if (!Is(line, components[k], folder[k]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Is(string line, Span span, string value) =>
        span.Normalized is not null
            ? string.Equals(span.Normalized, value, StringComparison.OrdinalIgnoreCase)
            : line.AsSpan(span.Start, span.End - span.Start).Equals(value, StringComparison.OrdinalIgnoreCase);

    private static string Text(string line, Span span) => span.Normalized ?? line[span.Start..span.End];

    // Una ruta absoluta que empieza en «i»: su raíz y sus segmentos. Recorre cada carácter una vez y suma a «work» lo que
    // lee hacia delante.
    private static bool TryParsePath(string s, int i, out ParsedPath path, ref long work)
    {
        path = default;
        if ((i > 0 && IsWordChar(s[i - 1])) || (s[i] != '%' && !IsSeparator(s[i]) && !IsDrive(s, i)))
        {
            // La mayoría de las posiciones no empiezan ninguna raíz: se descartan sin reservar nada.
            return false;
        }

        var components = new List<Span>();
        RootKind kind;
        int p;
        char separator;
        if (IsDrive(s, i))
        {
            kind = RootKind.Drive;
            components.Add(new Span(i, i + 2, char.ToUpperInvariant(s[i]) + ":"));
            separator = s[i + 2];
            p = SkipSeparators(s, i + 2);
        }
        else if (IsDeviceVolume(s, i, out int volumeEnd, ref work))
        {
            // «\Device\HarddiskVolume3\…»: la ruta de un volumen, como una unidad.
            kind = RootKind.Drive;
            components.Add(new Span(i, volumeEnd, @"\Device\HarddiskVolume"));
            separator = '\\';
            p = SkipSeparators(s, volumeEnd);
        }
        else if (s[i] == '%')
        {
            int j = i + 1;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_'))
            {
                j++;
            }

            work += j - i;
            if (j == i + 1 || j + 1 >= s.Length || s[j] != '%' || !IsSeparator(s[j + 1]))
            {
                return false;
            }

            // «%SystemDrive%» y «%HOMEDRIVE%» son una unidad: se limpian como «C:\».
            ReadOnlySpan<char> name = s.AsSpan(i, j + 1 - i);
            bool drive = name.Equals("%SystemDrive%", StringComparison.OrdinalIgnoreCase) || name.Equals("%HOMEDRIVE%", StringComparison.OrdinalIgnoreCase);
            kind = drive ? RootKind.Drive : RootKind.Variable;
            components.Add(new Span(i, j + 1, drive ? "%SYSTEMDRIVE%" : null));
            separator = s[j + 1];
            p = SkipSeparators(s, j + 1);
        }
        else if (IsSeparator(s[i]))
        {
            if (i > 0 && IsSeparator(s[i - 1]))
            {
                return false;
            }

            int run = SkipSeparators(s, i);
            if (run - i < 2)
            {
                return false;
            }

            if (i > 0 && s[i - 1] == ':')
            {
                // Una URL: solo «file://servidor/…»; «file:///D:/…» lo toma la unidad que sigue.
                if (i < 5 || !s.AsSpan(i - 5, 5).Equals("file:", StringComparison.OrdinalIgnoreCase) || IsDrive(s, run))
                {
                    return false;
                }
            }

            p = run;
            if (p + 1 < s.Length && s[p] is '?' or '.' && IsSeparator(s[p + 1]))
            {
                // «\\?\C:\…», «\\?\UNC\servidor\…» o «\\.\pipe\…».
                p = SkipSeparators(s, p + 1);
                if (IsDrive(s, p))
                {
                    kind = RootKind.Drive;
                    components.Add(new Span(p, p + 2, char.ToUpperInvariant(s[p]) + ":"));
                    separator = s[p + 2];
                    p = SkipSeparators(s, p + 2);
                    return ParseSegments(s, i, p, kind, components, separator, out path, ref work);
                }

                if (p + 3 < s.Length && s.AsSpan(p, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase) && IsSeparator(s[p + 3]))
                {
                    p = SkipSeparators(s, p + 3);
                }
            }

            kind = RootKind.Unc;
            components.Add(new Span(i, p, @"\\"));
            separator = s[i];
        }
        else
        {
            return false;
        }

        return ParseSegments(s, i, p, kind, components, separator, out path, ref work) && (kind != RootKind.Unc || path.HasSegments);
    }

    // «separator» es la barra de la ruta (la primera tras la raíz). Tras un archivo con extensión conocida seguido de texto
    // («a.mp4 y/o», «a.mp4 (no válido / corrupto)»), una «/» (y la «\» cuando no es la barra de la ruta) solo separa si lo
    // que la sigue continúa la ruta (ContinuesPath); si no, la ruta termina y el segmento es el último. Tras cualquier otro
    // segmento, también si parece texto («Acme S.A. Boda»), la barra separa: ante la duda, se tapa. Si la carpeta que
    // precede al último segmento es una de esas («Informe.log final») y el último no parece un archivo («Secreto
    // confidencial»), no se sabe si es frase o nombre, así que también se tapa (HideTail; tarea 196).
    private static bool ParseSegments(string s, int start, int p, RootKind kind, List<Span> components, char separator, out ParsedPath path, ref long work)
    {
        bool hasSegments = false;
        bool afterFileWithText = false;
        int tailStart;
        int tailEnd;
        while (true)
        {
            int segment = p;
            int textStart = -1;
            while (p < s.Length && IsSegmentChar(s[p]))
            {
                if (textStart < 0 && char.IsWhiteSpace(s[p]) && EndsName(s, segment, p))
                {
                    textStart = p;
                }

                p++;
            }

            if (p > segment && p < s.Length && IsSeparator(s[p])
                && (textStart < 0 || (s[p] == separator && separator == '\\')
                    || !HasKnownExtension(s, segment, textStart) || ContinuesPath(s, p, ref work)))
            {
                // Con «\» como barra de la ruta, el segmento es una carpeta aunque parezca texto: ante la duda, se tapa.
                // Con «/» es también la barra de la frase («a.mp4 y/o respaldo», «(error 12/34)» tras una URL «file:///»),
                // pero una carpeta puede parecer texto («Maria.Lopez Garcia», «Boda.v2 final», «OneDrive - Acme S.A.
                // Clientes»). Solo se corta tras un archivo de extensión conocida y cuando lo que sigue a la barra no
                // puede ser el resto de la ruta (tareas 183 y 191): sin ese contexto, el segmento se tapa. Solo cuenta
                // la carpeta inmediatamente anterior al último segmento (HideTail).
                afterFileWithText = textStart >= 0 && !(s[p] == separator && separator == '\\') && HasKnownExtension(s, segment, textStart);
                components.Add(new Span(segment, p, null));
                hasSegments = true;
                p = SkipSeparators(s, p);
                continue;
            }

            tailStart = segment;
            tailEnd = textStart < 0 ? p : textStart;
            break;
        }

        // Lo que sigue a la última barra tras un espacio («C:\Users\ana\ (12 s)») no es un nombre de la ruta.
        bool hasTail = tailEnd > tailStart && !char.IsWhiteSpace(s[tailStart]);
        if (hasTail)
        {
            components.Add(new Span(tailStart, tailEnd, null));
        }

        // Todo el análisis avanza un solo cursor desde «start» hasta «p» (más un vistazo acotado hacia atrás en EndsName).
        work += p - start;
        bool hideTail = hasTail && afterFileWithText && !HasExtension(s, tailStart, tailEnd);
        path = new ParsedPath(start, kind, components, hasSegments, tailStart, tailEnd, hasTail, hideTail);
        return true;
    }

    // Si el espacio en «space» cierra el nombre que empieza en «segment»: va seguido de « (» o de otro espacio, o el
    // nombre acaba en una extensión («a.mp4», «a.mp4'», «ana.lopez»). Mira como mucho 7 caracteres hacia atrás.
    private static bool EndsName(string s, int segment, int space)
    {
        if (space + 1 < s.Length && (s[space + 1] == '(' || char.IsWhiteSpace(s[space + 1])))
        {
            return true;
        }

        return HasExtension(s, segment, space);
    }

    // Si el nombre que empieza en «segment» acaba en una extensión justo antes de «end» («a.mp4», «a.mp4'», «ana.lopez»).
    // Mira como mucho 7 caracteres hacia atrás.
    private static bool HasExtension(string s, int segment, int end) => !Extension(s, segment, end).IsEmpty;

    // Como HasExtension, y además la extensión es de una lista conocida («a.mp4», no «ana.lopez» ni «Boda.v2»).
    private static bool HasKnownExtension(string s, int segment, int end) => KnownExtensions.Contains(Extension(s, segment, end));

    // La extensión con su punto («.mp4») si el nombre que empieza en «segment» acaba en una justo antes de «end»; si no,
    // vacía. Mira como mucho 7 caracteres hacia atrás.
    private static ReadOnlySpan<char> Extension(string s, int segment, int end)
    {
        if (end - 1 > segment && s[end - 1] is '\'' or ')' or ']' or ',' or '.')
        {
            end--;
        }

        int k = end;
        bool letter = false;
        while (k > segment && end - k < 6 && char.IsAsciiLetterOrDigit(s[k - 1]))
        {
            letter |= char.IsAsciiLetter(s[k - 1]);
            k--;
        }

        return letter && end - k is >= 1 and <= 5 && k - 1 > segment && s[k - 1] == '.' ? s.AsSpan(k - 1, end - k + 1) : default;
    }

    // Si lo que sigue a la barra en «slash», tras un archivo con extensión conocida seguido de texto, puede ser el resto de
    // la ruta. Ante la duda, sí: así la carpeta se tapa. Lee el tramo hasta la siguiente barra (o hasta lo que no cabe en
    // un nombre) una vez; a lo sumo se vuelve a leer como el segmento siguiente. Es frase, y la ruta termina antes de la
    // barra:
    // - si el tramo empieza por un espacio («a.mp4 (formato / corrupto)») o lleva un «)» sin su «(» («(error 12/34)»,
    //   «(copia \ original) y»): ninguna carpeta es así;
    // - si son cifras tras cifras y la cadena de barras y cifras termina ahí («12/300», «12/10/2026»); si sigue un nombre
    //   («2024/10/12/a.mp4»), son carpetas;
    // - si empieza por un conector corto («y/o respaldo», «y/o», «and/or») o es una cuenta («3/4 hechos») y no va seguido
    //   de otra barra. Cualquier otro tramo, aunque lleve espacios («Informe.log final/Secreto confidencial»), puede ser
    //   una carpeta o el último nombre (tarea 196).
    private static bool ContinuesPath(string s, int slash, ref long work)
    {
        int p = SkipSeparators(s, slash);
        int q = p;
        int firstWordEnd = -1;
        bool digits = true;
        int open = 0;
        bool unbalanced = false;
        while (q < s.Length && IsSegmentChar(s[q]))
        {
            char c = s[q];
            if (char.IsWhiteSpace(c))
            {
                firstWordEnd = firstWordEnd < 0 ? q : firstWordEnd;
            }
            else if (c == '(')
            {
                open++;
            }
            else if (c == ')')
            {
                unbalanced |= open == 0;
                open = Math.Max(0, open - 1);
            }

            digits &= char.IsAsciiDigit(c);
            q++;
        }

        work += q - p;
        if (q == p)
        {
            return true;
        }

        if (char.IsWhiteSpace(s[p]) || unbalanced)
        {
            return false;
        }

        if (digits && char.IsAsciiDigit(s[slash - 1]))
        {
            return q < s.Length && IsSeparator(s[q]) && !OnlyDigitsFollow(s, q, ref work);
        }

        ReadOnlySpan<char> firstWord = s.AsSpan(p, (firstWordEnd < 0 ? q : firstWordEnd) - p);
        bool count = char.IsAsciiDigit(s[slash - 1]) && !firstWord.ContainsAnyExceptInRange('0', '9');
        return (q < s.Length && IsSeparator(s[q])) || !(IsConnector(firstWord) || count);
    }

    // Las palabras que siguen a una «/» en una frase («y/o», «y/u», «and/or»), no al separar carpetas.
    private static bool IsConnector(ReadOnlySpan<char> word) =>
        word.Equals("o", StringComparison.OrdinalIgnoreCase) || word.Equals("u", StringComparison.OrdinalIgnoreCase)
        || word.Equals("y", StringComparison.OrdinalIgnoreCase) || word.Equals("e", StringComparison.OrdinalIgnoreCase)
        || word.Equals("or", StringComparison.OrdinalIgnoreCase) || word.Equals("and", StringComparison.OrdinalIgnoreCase);

    // Si tras la barra en «slash» solo quedan cifras separadas por barras hasta el final del nombre («/10/2026 y»). Lee la
    // cadena una vez: solo la consulta el segmento con texto que la precede, y lo que va dentro no la vuelve a consultar.
    private static bool OnlyDigitsFollow(string s, int slash, ref long work)
    {
        while (true)
        {
            int p = SkipSeparators(s, slash);
            int q = p;
            while (q < s.Length && char.IsAsciiDigit(s[q]))
            {
                q++;
            }

            work += q - p;
            if (q == p)
            {
                return false;
            }

            if (q < s.Length && IsSeparator(s[q]))
            {
                slash = q;
                continue;
            }

            return q == s.Length || !IsSegmentChar(s[q]) || char.IsWhiteSpace(s[q]);
        }
    }

    // «\Device\HarddiskVolumeN» seguido de «\»; «end» queda en esa barra.
    private static bool IsDeviceVolume(string s, int i, out int end, ref long work)
    {
        const string prefix = @"\Device\HarddiskVolume";
        end = i + prefix.Length;
        if ((i > 0 && IsSeparator(s[i - 1])) || end >= s.Length || !s.AsSpan(i, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        while (end < s.Length && char.IsAsciiDigit(s[end]))
        {
            end++;
            work++;
        }

        return end > i + prefix.Length && end < s.Length && s[end] == '\\';
    }

    private static bool IsDrive(string s, int i) =>
        i + 2 < s.Length && char.IsAsciiLetter(s[i]) && s[i + 1] == ':' && IsSeparator(s[i + 2])
        && (i == 0 || !IsWordChar(s[i - 1]));

    private static int SkipSeparators(string s, int p)
    {
        while (p < s.Length && IsSeparator(s[p]))
        {
            p++;
        }

        return p;
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    // Un nombre dentro de una ruta: con espacios y tildes; sin los caracteres prohibidos en Windows, «;» (separa las
    // rutas de una lista) ni caracteres de control.
    private static bool IsSegmentChar(char c) => c >= ' ' && c is not ('\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ';');

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private string ReplaceNames(string line)
    {
        // El equipo primero: suele contener el nombre del usuario («ANA-PC»).
        if (_machineName is not null)
        {
            line = ReplaceWord(line, _machineName, "%COMPUTERNAME%");
        }

        return _userName is not null ? ReplaceWord(line, _userName, "%USERNAME%") : line;
    }

    private static string ReplaceWord(string line, string word, string token)
    {
        int found = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (found < 0)
        {
            return line;
        }

        var output = new StringBuilder(line.Length);
        int copied = 0;
        while (found >= 0)
        {
            int after = found + word.Length;
            bool before = found == 0 || !IsWordChar(line[found - 1]);
            bool next = after == line.Length || !IsWordChar(line[after]);
            bool insideToken = found > 0 && line[found - 1] == '%' && after < line.Length && line[after] == '%';
            if (before && next && !insideToken)
            {
                output.Append(line, copied, found - copied).Append(token);
                copied = after;
            }

            found = after < line.Length ? line.IndexOf(word, after, StringComparison.OrdinalIgnoreCase) : -1;
        }

        return output.Append(line, copied, line.Length - copied).ToString();
    }

    private static string[]? FolderComponents(string? folder)
    {
        // «Path.GetTempPath()» termina en barra: sin ella, la carpeta es la misma.
        string text = (folder ?? string.Empty).Trim();
        if (text.Length > 3)
        {
            text = text.TrimEnd('\\', '/');
        }

        long work = 0;
        if (text.Length < 3 || !TryParsePath(text, 0, out ParsedPath path, ref work) || path.Kind == RootKind.Variable
            || path.TailEnd != text.Length || path.Components.Count < 2)
        {
            return null;
        }

        return [.. path.Components.Select(c => Text(text, c))];
    }

    private static string? Name(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length >= 3 ? trimmed : null;
    }

    private static string? Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private enum RootKind
    {
        Drive,
        Unc,
        Variable,
    }

    // Un componente de la ruta en la línea; «Normalized» es su forma canónica cuando no coincide con el texto (la raíz).
    private readonly record struct Span(int Start, int End, string? Normalized);

    // Components: la raíz, los segmentos seguidos de barra y, si lo hay, el último segmento (TailStart..TailEnd).
    // HideTail: el último segmento también se tapa cuando la ruta no se conserva.
    private readonly record struct ParsedPath(
        int Start, RootKind Kind, List<Span> Components, bool HasSegments, int TailStart, int TailEnd, bool HasTail, bool HideTail);
}
