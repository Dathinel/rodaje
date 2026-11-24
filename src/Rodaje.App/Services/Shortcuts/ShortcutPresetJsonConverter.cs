using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

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
