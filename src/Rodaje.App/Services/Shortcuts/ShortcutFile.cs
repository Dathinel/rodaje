using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

internal sealed class ShortcutFile
{
    public int Version { get; set; } = ShortcutStore.CurrentVersion;

    public Dictionary<string, Dictionary<string, List<string>>> Presets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Por estilo y zona («timeline», «viewer», «library»): combinación → acción, o <c>null</c> sin acción.</summary>
    public Dictionary<string, Dictionary<string, Dictionary<string, string?>>> Mouse { get; set; } = new(StringComparer.Ordinal);
}
