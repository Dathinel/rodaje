using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>Juegos de atajos de fábrica.</summary>
[JsonConverter(typeof(ShortcutPresetJsonConverter))]
public enum ShortcutPreset
{
    /// <summary>Los de Rodaje: letras sueltas para lo más usado.</summary>
    Rodaje,

    /// <summary>Pocas teclas, con Ctrl para cortar y sin herramientas de zoom ni envolvente.</summary>
    Agile,

    /// <summary>Teclas de editor de escritorio tradicional: flechas para el zoom, Alt para recortar.</summary>
    Classic,
}

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

/// <summary>Guarda el estilo por su nombre y, al leer, migra los nombres viejos; uno desconocido vuelve a Rodaje.</summary>
public sealed class ShortcutPresetJsonConverter : JsonConverter<ShortcutPreset>
{
    public override ShortcutPreset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return ShortcutPresetNames.TryParse(reader.GetString(), out ShortcutPreset preset) ? preset : ShortcutPreset.Rodaje;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) && Enum.IsDefined((ShortcutPreset)value))
        {
            return (ShortcutPreset)value;
        }

        reader.Skip();
        return ShortcutPreset.Rodaje;
    }

    public override void Write(Utf8JsonWriter writer, ShortcutPreset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
