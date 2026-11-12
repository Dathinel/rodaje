using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Una opción de «Rango»: la secuencia entera, entre la entrada y la salida, lo seleccionado o una región (entrada y salida con nombre).</summary>
/// <param name="Range"><c>null</c>: toda la secuencia.</param>
public sealed record RangeChoice(string Label, ExportRange? Range);

/// <summary>Las marcas de entrada (I) y salida (O) de la secuencia; cualquiera de las dos puede faltar.</summary>
public readonly record struct SequenceMarks(Time? In, Time? Out)
{
    public static SequenceMarks None => default;

    public bool IsEmpty => In is null && Out is null;
}

/// <summary>Los rangos que se ofrecen y la ruta de salida.</summary>
public static class ExportRanges
{
    private static readonly HashSet<string> KnownExtensions = new(
        [.. Enum.GetValues<ExportContainer>().Select(c => c.Extension()), "tif", "tiff", "jpg", "jpeg", "exr"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Texto de la opción «Entre la entrada y la salida», sin los tiempos.</summary>
    public const string MarksLabel = "Entre la entrada y la salida";

    /// <summary>
    /// El rango que marcan I y O en la secuencia: con una sola marca, la otra es el principio o el final de la secuencia
    /// (como en los editores profesionales). Nulo sin marcas o si el tramo queda vacío.
    /// </summary>
    public static ExportRange? MarkedRange(Sequence sequence, SequenceMarks marks)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (marks.IsEmpty)
        {
            return null;
        }

        Time start = Time.Max(Time.Zero, marks.In ?? Time.Zero);
        Time end = Time.Min(sequence.Duration, marks.Out ?? sequence.Duration);
        return end > start ? new ExportRange(start, end) : null;
    }

    /// <summary>
    /// Las opciones de «Rango»: toda la secuencia; entre la entrada y la salida si hay marcas (la opción que se propone
    /// en ese caso, <see cref="DefaultIndex"/>); lo seleccionado; y cada región.
    /// </summary>
    public static IReadOnlyList<RangeChoice> Choices(Sequence sequence, IReadOnlyCollection<string> selectedClipIds, SequenceMarks marks = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        var choices = new List<RangeChoice> { new($"Toda la secuencia ({Clock(sequence.Duration)})", null) };
        if (MarkedRange(sequence, marks) is { } marked)
        {
            choices.Add(new($"{MarksLabel} ({Clock(marked.Start)} – {Clock(marked.End)})", marked));
        }

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

    /// <summary>La opción que se propone al abrir: entre la entrada y la salida si está, y si no, toda la secuencia.</summary>
    public static int DefaultIndex(IReadOnlyList<RangeChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i].Label.StartsWith(MarksLabel, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 0;
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

    /// <summary>
    /// La ruta con la extensión que piden los ajustes: cambia la de exportación que ya tenga (MP4 por MOV cambia el «.mp4»
    /// del final) y, si no tiene ninguna, la añade: en «Boda 24.10.2026» o «Prueba 1.5», lo que sigue al último punto es
    /// parte del nombre.
    /// </summary>
    public static string WithExtension(string path, ExportSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return KnownExtensions.Contains(Path.GetExtension(path).TrimStart('.'))
            ? Path.ChangeExtension(path, Extension(settings))
            : path.TrimEnd('.') + "." + Extension(settings);
    }

    /// <summary>
    /// Si se puede escribir ahí: carpeta existente, nombre válido; y si ya hay un archivo (hay que confirmar). Una secuencia
    /// de imágenes no reemplaza una carpeta con archivos (<see cref="ExportOutputGuard.CheckOutput"/> la rechaza).
    /// </summary>
    public static OutputCheck Check(string? path, ExportContainer? container = null)
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

        if (container == ExportContainer.ImageSequence && Directory.Exists(path) && HasEntries(path))
        {
            return OutputCheck.FolderNotEmpty;
        }

        return File.Exists(path) || Directory.Exists(path) ? OutputCheck.Exists : OutputCheck.Ok;
    }

    private static bool HasEntries(string folder)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? Describe(OutputCheck check) => check switch
    {
        OutputCheck.Missing => "Elige dónde guardar el archivo.",
        OutputCheck.InvalidName => "El nombre del archivo no es válido.",
        OutputCheck.FolderMissing => "La carpeta no existe.",
        OutputCheck.Exists => "Ya hay un archivo con ese nombre: se reemplazará al exportar.",
        OutputCheck.FolderNotEmpty => "La carpeta ya tiene archivos: elige otro nombre.",
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

    /// <summary>La carpeta de una secuencia de imágenes ya tiene archivos: no se puede reemplazar.</summary>
    FolderNotEmpty,
}
