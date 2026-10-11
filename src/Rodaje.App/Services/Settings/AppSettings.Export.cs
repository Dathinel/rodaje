using Rodaje.Export;

namespace Rodaje.App.Services.Settings;

public sealed partial class AppSettings
{
    /// <summary>Lo último que se exportó y los preajustes propios.</summary>
    public ExportPreferences Export { get; set; } = new();
}

/// <summary>Lo que el diálogo de exportar recuerda entre sesiones (<c>ajustes.json</c>).</summary>
public sealed class ExportPreferences
{
    public const int MaxUserPresets = 50;

    /// <summary>Preajuste elegido la última vez; <c>null</c> si fue «Personalizado».</summary>
    public string? PresetId { get; set; }

    /// <summary>Ajustes de la última exportación, sin metadatos (son de cada proyecto).</summary>
    public ExportSettings? Settings { get; set; }

    /// <summary>El modo de rendimiento elegido la última vez: queda marcado en la pregunta siguiente.</summary>
    public ExportPerformance Performance { get; set; } = ExportPerformance.Background;

    /// <summary>Carpeta de la última exportación.</summary>
    public string? Folder { get; set; }

    /// <summary>«Avanzado» desplegado.</summary>
    public bool AdvancedOpen { get; set; }

    /// <summary>Preajustes guardados por el usuario, en el orden en que se crearon.</summary>
    public List<ExportPreset> UserPresets { get; set; } = [];

    public void Normalize()
    {
        if (!Enum.IsDefined(Performance))
        {
            Performance = ExportPerformance.Background;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        UserPresets = [.. (UserPresets ?? [])
            .Where(p => p is { Settings: not null } && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name) && seen.Add(p.Id))
            .Select(p => p with { Origin = ExportPresetOrigin.User })
            .Take(MaxUserPresets)];
    }
}
