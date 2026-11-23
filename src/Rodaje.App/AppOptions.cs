using System.Globalization;

namespace Rodaje.App;

/// <summary>
/// Argumentos: videos o un <c>.rodaje</c> para abrir; <c>--medir N</c> reproduce N segundos, mide y cierra (sin
/// archivos, con el clip H.264 1080p de prueba de <c>TestResults\media-cache</c>); <c>--registro ruta</c> dónde se
/// escriben las cifras; <c>--sin-audio</c> usa el reloj del sistema; <c>--ui-hz N</c> refresca tiempo y cursor de la
/// interfaz N veces por segundo (0: en cada cuadro de XAML; por defecto 30); <c>--escenario efectos</c> arma antes de
/// medir 3 capas con efectos y 4 transiciones sobre el medio, y mueve un deslizador de color al reproducir;
/// <c>--sin-proxies</c> reproduce siempre el original (para medir el peor caso), sin tocar los ajustes;
/// <c>--autoprueba</c> abre la ventana con el clip de prueba, pasa por todas las pestañas del inspector con un clip
/// seleccionado, reproduce un momento y sale con 0 (la usa <c>probar.ps1</c> con el exe de Native AOT);
/// <c>--arranque</c> escribe las marcas de arranque en el registro en cuanto la interfaz responde (con archivos, cuando se
/// ve el primer cuadro) y cierra (la usa <c>build\medir-arranque.ps1</c>); <c>--visible</c> deja a la vista la ventana de
/// esas tres, que si no van de fondo; <c>--caos semilla minutos</c> castiga la interfaz con acciones al azar durante esos
/// minutos (<see cref="Shell.ChaosRun"/>; los archivos son el corpus que importa) y sale con 0 si nada falló.
/// </summary>
internal sealed record AppOptions(IReadOnlyList<string> Files, double? MeasureSeconds, string? LogPath, bool EnableAudio, int UiHz)
{
    public string? Scenario { get; init; }

    public bool NoProxies { get; init; }

    public bool SelfTest { get; init; }

    public bool StartupProbe { get; init; }

    /// <summary>La semilla de <c>--caos</c>; nula fuera del modo caos.</summary>
    public int? ChaosSeed { get; init; }

    public double ChaosMinutes { get; init; }

    /// <summary>La maneja una herramienta (medida, autoprueba, arranque): sin autoguardado ni preguntas al cerrar.</summary>
    /// <summary>El enlace <c>rodaje://unirse/…</c> con el que se abrió Rodaje: abre «Unirse a una sala» con él ya pegado.</summary>
    public string? InviteLink { get; init; }

    public bool IsAutomated => MeasureSeconds is not null || SelfTest || StartupProbe || ChaosSeed is not null;

    public bool Visible { get; init; }

    /// <summary>Corrida automática sin <c>--visible</c>: armazón y ventana fuera de pantalla, sin foco ni botón en la barra de tareas.</summary>
    public bool RunsInBackground => IsAutomated && !Visible;

    public const string TestClipPattern = "h264-1080p-23976-aac-*.mp4";

    public static AppOptions Parse(string[] args)
    {
        var files = new List<string>();
        string? invite = null;
        double? measure = null;
        string? log = null;
        bool audio = true;
        int uiHz = 30;
        string? scenario = null;
        bool noProxies = false;
        bool selfTest = false;
        bool startup = false;
        bool visible = false;
        int? chaosSeed = null;
        double chaosMinutes = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--medir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                measure = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (a.Equals("--registro", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                log = Path.GetFullPath(args[++i]);
            }
            else if (a.Equals("--ui-hz", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                uiHz = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (a.Equals("--escenario", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                scenario = args[++i].ToLowerInvariant();
            }
            else if (a.Equals("--sin-proxies", StringComparison.OrdinalIgnoreCase))
            {
                noProxies = true;
            }
            else if (a.Equals("--autoprueba", StringComparison.OrdinalIgnoreCase))
            {
                selfTest = true;
            }
            else if (a.Equals("--caos", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
            {
                chaosSeed = int.Parse(args[++i], CultureInfo.InvariantCulture);
                chaosMinutes = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (a.Equals("--arranque", StringComparison.OrdinalIgnoreCase))
            {
                startup = true;
            }
            else if (a.Equals("--visible", StringComparison.OrdinalIgnoreCase))
            {
                visible = true;
            }
            else if (a.Equals("--sin-audio", StringComparison.OrdinalIgnoreCase))
            {
                audio = false;
            }
            else if (a.StartsWith("rodaje://", StringComparison.OrdinalIgnoreCase))
            {
                invite = a;
            }
            else
            {
                try
                {
                    files.Add(Path.GetFullPath(a));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Un argumento que no es una ruta (un enlace de otro tipo) no debe impedir que Rodaje arranque.
                }
            }
        }

        if ((measure is not null || selfTest) && files.Count == 0 && FindTestClip() is { } clip)
        {
            files.Add(clip);
        }

        return new AppOptions(files, measure, log, audio, uiHz) { Scenario = scenario, NoProxies = noProxies, SelfTest = selfTest, StartupProbe = startup, ChaosSeed = chaosSeed, ChaosMinutes = chaosMinutes, Visible = visible, InviteLink = invite };
    }

    private static string? FindTestClip()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string cache = Path.Combine(dir.FullName, "TestResults", "media-cache");
            if (Directory.Exists(cache) && Directory.EnumerateFiles(cache, TestClipPattern).FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
