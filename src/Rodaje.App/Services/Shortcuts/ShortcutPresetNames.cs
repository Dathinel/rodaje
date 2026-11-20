using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Nombres de los estilos tal como se ven y como se guardan.</summary>
public static class ShortcutPresetNames
{
    public static string Display(ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.Agile => "Ágil",
        ShortcutPreset.Classic => "Clásico",
        _ => "Rodaje",
    };

    /// <summary>
    /// Lee el nombre guardado de un estilo. Acepta los nombres anteriores al 10 de octubre de 2026, que eran los de otros
    /// programas, para que los atajos y ajustes guardados con ellos sigan valiendo.
    /// </summary>
    public static bool TryParse(string? text, out ShortcutPreset preset)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "rodaje":
                preset = ShortcutPreset.Rodaje;
                return true;
            case "agile" or "capcut":
                preset = ShortcutPreset.Agile;
                return true;
            case "classic" or "vegas":
                preset = ShortcutPreset.Classic;
                return true;
            default:
                preset = ShortcutPreset.Rodaje;
                return false;
        }
    }
}
