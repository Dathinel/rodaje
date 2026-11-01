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
