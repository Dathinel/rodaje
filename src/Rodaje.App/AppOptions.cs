using System.Globalization;

namespace Rodaje.App;

/// <summary>
/// Argumentos: videos o un <c>.rodaje</c> para abrir; <c>--medir N</c> reproduce N segundos, mide y cierra (sin
/// archivos, con el clip H.264 1080p de prueba de <c>TestResults\media-cache</c>); <c>--registro ruta</c> dónde se
/// escriben las cifras; <c>--sin-audio</c> usa el reloj del sistema; <c>--ui-hz N</c> refresca tiempo y cursor de la
/// interfaz N veces por segundo (0: en cada cuadro de XAML; por defecto 30); <c>--escenario efectos</c> arma antes de
/// medir 3 capas con efectos y 4 transiciones sobre el medio, y mueve un deslizador de color al reproducir.
/// </summary>
internal sealed record AppOptions(IReadOnlyList<string> Files, double? MeasureSeconds, string? LogPath, bool EnableAudio, int UiHz)
{
    public string? Scenario { get; init; }

    public const string TestClipPattern = "h264-1080p-23976-aac-*.mp4";

    public static AppOptions Parse(string[] args)
    {
        var files = new List<string>();
        double? measure = null;
        string? log = null;
        bool audio = true;
        int uiHz = 30;
        string? scenario = null;
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
            else if (a.Equals("--sin-audio", StringComparison.OrdinalIgnoreCase))
            {
                audio = false;
            }
            else
            {
                files.Add(Path.GetFullPath(a));
            }
        }

        if (measure is not null && files.Count == 0 && FindTestClip() is { } clip)
        {
            files.Add(clip);
        }

        return new AppOptions(files, measure, log, audio, uiHz) { Scenario = scenario };
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
