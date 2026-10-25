namespace Rodaje.App.Views.Export.Logic;

using System;
using System.IO;
using Rodaje.App.Services.Settings;
using Rodaje.Core.Timing;

/// <summary>
/// Reglas de nombrado y ubicación para las capturas rápidas de fotogramas (BACKLOG-1.0.2).
/// </summary>
public static class StillCaptureNaming
{
    public const string DefaultSubfolder = "Capturas";

    /// <summary>
    /// Genera el nombre del archivo de captura con la plantilla &lt;proyecto&gt;_&lt;hh-mm-ss-ff&gt;.png.
    /// </summary>
    public static string FormatFileName(string projectName, Time time, Rational rate)
    {
        string safeProject = string.Concat((projectName ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safeProject))
        {
            safeProject = "Proyecto";
        }

        double fps = rate.ToDouble();
        if (fps <= 0)
        {
            fps = 30;
        }

        long totalFrames = Math.Max(0, time.ToFrames(rate));
        long framesPerSec = (long)Math.Round(fps);
        if (framesPerSec <= 0)
        {
            framesPerSec = 30;
        }

        long ff = totalFrames % framesPerSec;
        long totalSeconds = totalFrames / framesPerSec;
        long ss = totalSeconds % 60;
        long totalMinutes = totalSeconds / 60;
        long mm = totalMinutes % 60;
        long hh = totalMinutes / 60;

        return $"{safeProject}_{hh:00}-{mm:00}-{ss:00}-{ff:00}.png";
    }

    /// <summary>
    /// <paramref name="path"/> o, si ya hay un archivo con ese nombre, «… (2).png», «… (3).png»: una segunda captura del
    /// mismo cuadro (tras retocar el color) no pisa la primera ni choca con ella si se añadió a la biblioteca.
    /// </summary>
    public static string Available(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string folder = Path.GetDirectoryName(path) ?? "";
        string candidate = path;
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(path)} ({n}){Path.GetExtension(path)}");
        }

        return candidate;
    }

    /// <summary>
    /// Resuelve la carpeta destino para las capturas: la configurada en preferencias o «Capturas» junto al proyecto.
    /// </summary>
    public static string ResolveCaptureFolder(AppSettings settings, string? projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.StillCaptureFolder))
        {
            return settings.StillCaptureFolder;
        }

        string baseFolder = !string.IsNullOrWhiteSpace(projectFilePath) && Path.GetDirectoryName(projectFilePath) is { Length: > 0 } dir
            ? dir
            : FileDialogFolderPolicy.SaveFolder(settings, SaveDialogKind.Still);

        return Path.Combine(baseFolder, DefaultSubfolder);
    }

    /// <summary>
    /// Ruta completa donde guardar la captura de un fotograma.
    /// </summary>
    public static string ResolveCapturePath(AppSettings settings, string projectName, string? projectFilePath, Time time, Rational rate)
    {
        string folder = ResolveCaptureFolder(settings, projectFilePath);
        string fileName = FormatFileName(projectName, time, rate);
        return Path.Combine(folder, fileName);
    }
}
