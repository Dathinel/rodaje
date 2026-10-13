using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Una opción de «Rango»: la secuencia entera, lo seleccionado o una región (entrada y salida con nombre).</summary>
/// <param name="Range"><c>null</c>: toda la secuencia.</param>
public sealed record RangeChoice(string Label, ExportRange? Range);

/// <summary>Los rangos que se ofrecen y la ruta de salida.</summary>
public static class ExportRanges
{
    public static IReadOnlyList<RangeChoice> Choices(Sequence sequence, IReadOnlyCollection<string> selectedClipIds)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        var choices = new List<RangeChoice> { new($"Toda la secuencia ({Clock(sequence.Duration)})", null) };
        Clip[] selected = [.. selectedClipIds.Select(id => sequence.FindClip(id)?.Clip).OfType<Clip>()];
        if (selected.Length > 0)
        {
            Time start = selected.Min(c => c.Start);
            Time end = selected.Max(c => c.End);
            if (end > start)
            {
                choices.Add(new($"Lo seleccionado ({Clock(start)} – {Clock(end)})", new ExportRange(start, end)));
            }
        }

        foreach (Region region in sequence.Regions.Where(r => r.End > r.Start))
        {
            string name = string.IsNullOrWhiteSpace(region.Name) ? "Región" : $"Región «{region.Name}»";
            choices.Add(new($"{name} ({Clock(region.Start)} – {Clock(region.End)})", new ExportRange(region.Start, region.End)));
        }

        return choices;
    }

    /// <summary>«1:05», «12:00», «1:02:03».</summary>
    public static string Clock(Time time)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, time.ToSeconds()));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>Extensión de la salida (en las secuencias de imágenes, la del códec).</summary>
    public static string Extension(ExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Container == ExportContainer.ImageSequence
            ? settings.Video?.Codec switch
            {
                ExportCodec.Tiff => "tif",
                ExportCodec.Mjpeg => "jpg",
                ExportCodec.Exr => "exr",
                _ => "png",
            }
            : settings.Container.Extension();
    }

    /// <summary>
    /// Nombre que se propone: el del proyecto sin caracteres prohibidos, con la extensión de los ajustes y sin ser un nombre
    /// reservado de Windows.
    /// </summary>
    public static string SuggestFileName(string projectName, ExportSettings settings)
    {
        string name = string.Concat((projectName ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        // «CON.mp4» o «aux.mp4» serían un dispositivo en Windows, no un archivo: se propone «_CON.mp4».
        return WindowsFileNames.AvoidReserved((name.Length == 0 ? "Exportación" : name) + "." + Extension(settings));
    }

    /// <summary>La ruta con la extensión que piden los ajustes (cambiar MP4 por MOV cambia el «.mp4» del final).</summary>
    public static string WithExtension(string path, ExportSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.ChangeExtension(path, Extension(settings));
    }

    /// <summary>Si se puede escribir ahí: carpeta existente, nombre válido; y si ya hay un archivo (hay que confirmar).</summary>
    public static OutputCheck Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return OutputCheck.Missing;
        }

        string name = Path.GetFileName(path);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Path.IsPathFullyQualified(path))
        {
            return OutputCheck.InvalidName;
        }

        if (Path.GetDirectoryName(path) is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            return OutputCheck.FolderMissing;
        }

        return File.Exists(path) || Directory.Exists(path) ? OutputCheck.Exists : OutputCheck.Ok;
    }

    public static string? Describe(OutputCheck check) => check switch
    {
        OutputCheck.Missing => "Elige dónde guardar el archivo.",
        OutputCheck.InvalidName => "El nombre del archivo no es válido.",
        OutputCheck.FolderMissing => "La carpeta no existe.",
        OutputCheck.Exists => "Ya hay un archivo con ese nombre: se reemplazará al exportar.",
        _ => null,
    };
}

public enum OutputCheck
{
    Ok,
    Missing,
    InvalidName,
    FolderMissing,

    /// <summary>Se puede, pero reemplaza un archivo: el diálogo pide confirmación.</summary>
    Exists,
}
