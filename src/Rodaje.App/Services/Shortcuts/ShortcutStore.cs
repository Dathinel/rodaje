using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Guarda los cambios de atajos de la persona en <c>atajos.json</c>, por estilo, con las teclas en el formato estable
/// («Ctrl+Shift+Z»). Solo se guardan las diferencias con los de fábrica, así una tecla nueva de fábrica llega sola.
/// </summary>
public sealed class ShortcutStore
{
    public const string FileName = "atajos.json";
    public const int CurrentVersion = 1;

    public ShortcutStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public static string DefaultPath => System.IO.Path.Combine(AppDataPaths.Root, FileName);

    public string Path { get; }

    private string? _lastWritten;

    /// <summary>
    /// Lee los cambios guardados en el registro. Devuelve <c>null</c> si todo fue bien (o no había archivo); si el
    /// archivo estaba dañado, lo aparta y devuelve el aviso en español.
    /// </summary>
    public string? Load(ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!File.Exists(Path))
        {
            return null;
        }

        ShortcutFile? file;
        try
        {
            string text = File.ReadAllText(Path);
            file = JsonSerializer.Deserialize(text, ShortcutJsonContext.Default.ShortcutFile);
            _lastWritten = text;
        }
        catch (JsonException)
        {
            file = null;
        }
        catch (IOException ex)
        {
            return $"No se pudieron leer los atajos personalizados ({ex.Message}); se usan los de fábrica.";
        }

        if (file?.Presets is null)
        {
            string? aside = AppDataPaths.SetAside(Path, ".dañado");
            return "El archivo de atajos personalizados estaba dañado; se usan los de fábrica"
                + (aside is null ? "." : $" y el archivo quedó en {aside}.");
        }

        foreach ((string presetName, Dictionary<string, List<string>>? actions) in file.Presets)
        {
            if (!Enum.TryParse(presetName, ignoreCase: true, out ShortcutPreset preset) || !Enum.IsDefined(preset) || actions is null)
            {
                continue;
            }

            var overrides = new Dictionary<string, IReadOnlyList<KeyChord>>(StringComparer.Ordinal);
            foreach ((string actionId, List<string>? keys) in actions)
            {
                var chords = new List<KeyChord>();
                foreach (string text in keys ?? [])
                {
                    if (KeyChord.TryParse(text, out KeyChord chord))
                    {
                        chords.Add(chord);
                    }
                }

                overrides[actionId] = chords;
            }

            registry.SetOverrides(preset, overrides);
        }

        return null;
    }

    public void Save(ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var file = new ShortcutFile { Version = CurrentVersion };
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> overrides = registry.GetOverrides(preset);
            if (overrides.Count == 0)
            {
                continue;
            }

            file.Presets[preset.ToString()] = overrides
                .OrderBy(o => o.Key, StringComparer.Ordinal)
                .ToDictionary(o => o.Key, o => o.Value.Select(k => k.ToInvariantString()).ToList(), StringComparer.Ordinal);
        }

        string json = JsonSerializer.Serialize(file, ShortcutJsonContext.Default.ShortcutFile);
        if (json == _lastWritten || (_lastWritten is null && file.Presets.Count == 0 && !File.Exists(Path)))
        {
            return;
        }

        AppDataPaths.WriteAllTextAtomic(Path, json);
        _lastWritten = json;
    }
}

internal sealed class ShortcutFile
{
    public int Version { get; set; } = ShortcutStore.CurrentVersion;

    public Dictionary<string, Dictionary<string, List<string>>> Presets { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ShortcutFile))]
internal sealed partial class ShortcutJsonContext : JsonSerializerContext;
