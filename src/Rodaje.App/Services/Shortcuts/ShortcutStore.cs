using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Guarda los cambios de atajos de la persona en <c>atajos.json</c>, por estilo: las teclas en el formato estable
/// («Ctrl+Shift+Z») y el ratón por zona («Ctrl+Wheel»: acción, o <c>null</c> si se dejó sin acción). Solo se guardan
/// las diferencias con los de fábrica, así un atajo nuevo de fábrica llega solo. El mismo formato sirve para exportar e
/// importar el mapa.
/// </summary>
public sealed class ShortcutStore
{
    public const string FileName = "atajos.json";
    public const int CurrentVersion = 2;

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
            file = Parse(text);
            _lastWritten = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin permiso de lectura (UnauthorizedAccessException) la ventana principal no llegaba a abrirse.
            return $"No se pudieron leer los atajos personalizados ({ex.Message}); se usan los de fábrica.";
        }

        if (file is null)
        {
            string? aside = AppDataPaths.SetAside(Path, ".dañado");
            return "El archivo de atajos personalizados estaba dañado; se usan los de fábrica"
                + (aside is null ? "." : $" y el archivo quedó en {aside}.");
        }

        Apply(registry, file, replaceAll: false);
        return null;
    }

    public void Save(ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ShortcutFile file = Build(registry);
        string json = JsonSerializer.Serialize(file, ShortcutJsonContext.Default.ShortcutFile);
        if (json == _lastWritten || (_lastWritten is null && file.Presets.Count == 0 && file.Mouse.Count == 0 && !File.Exists(Path)))
        {
            return;
        }

        AppDataPaths.WriteAllTextAtomic(Path, json);
        _lastWritten = json;
    }

    /// <summary>Escribe el mapa completo (los cambios de todos los estilos) en <paramref name="path"/>.</summary>
    public static void Export(ShortcutRegistry registry, string path)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(path);
        AppDataPaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(Build(registry), ShortcutJsonContext.Default.ShortcutFile));
    }

    /// <summary>
    /// Reemplaza los cambios de todos los estilos por los de un mapa exportado. Devuelve <c>null</c> si se importó o el
    /// motivo en español si no; en ese caso no cambia nada.
    /// </summary>
    public static string? Import(ShortcutRegistry registry, string path)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ShortcutFile? file;
        try
        {
            file = Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"No se pudo leer {System.IO.Path.GetFileName(path)}: {ex.Message}";
        }

        if (file is null)
        {
            return $"{System.IO.Path.GetFileName(path)} no es un mapa de atajos de Rodaje.";
        }

        Apply(registry, file, replaceAll: true);
        return null;
    }

    private static ShortcutFile? Parse(string text)
    {
        try
        {
            ShortcutFile? file = JsonSerializer.Deserialize(text, ShortcutJsonContext.Default.ShortcutFile);
            return file?.Presets is null ? null : file;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ShortcutFile Build(ShortcutRegistry registry)
    {
        var file = new ShortcutFile { Version = CurrentVersion };
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            IReadOnlyDictionary<string, IReadOnlyList<KeyChord>> overrides = registry.GetOverrides(preset);
            if (overrides.Count > 0)
            {
                file.Presets[preset.ToString()] = overrides
                    .OrderBy(o => o.Key, StringComparer.Ordinal)
                    .ToDictionary(o => o.Key, o => o.Value.Select(k => k.ToInvariantString()).ToList(), StringComparer.Ordinal);
            }

            IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>> mouse = registry.Mouse.GetOverrides(preset);
            if (mouse.Count > 0)
            {
                file.Mouse[preset.ToString()] = mouse
                    .OrderBy(m => m.Key)
                    .ToDictionary(
                        m => MouseNames.Invariant(m.Key),
                        m => m.Value
                            .OrderBy(c => c.Key.Input).ThenBy(c => (int)c.Key.Modifiers)
                            .ToDictionary(c => c.Key.ToInvariantString(), c => c.Value, StringComparer.Ordinal),
                        StringComparer.Ordinal);
            }
        }

        return file;
    }

    internal static void Apply(ShortcutRegistry registry, ShortcutFile file, bool replaceAll)
    {
        var keys = new Dictionary<ShortcutPreset, IReadOnlyDictionary<string, IReadOnlyList<KeyChord>>>();
        var mouse = new Dictionary<ShortcutPreset, IReadOnlyDictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>>();
        ShortcutSnapshot current = registry.TakeSnapshot();
        foreach (ShortcutPreset preset in Enum.GetValues<ShortcutPreset>())
        {
            keys[preset] = replaceAll ? new Dictionary<string, IReadOnlyList<KeyChord>>() : current.Keys[preset];
            mouse[preset] = replaceAll ? new Dictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>() : current.Mouse[preset];
        }

        foreach ((string presetName, Dictionary<string, List<string>>? actions) in file.Presets)
        {
            if (!ShortcutPresetNames.TryParse(presetName, out ShortcutPreset preset) || actions is null)
            {
                continue;
            }

            var overrides = new Dictionary<string, IReadOnlyList<KeyChord>>(StringComparer.Ordinal);
            foreach ((string actionId, List<string>? texts) in actions)
            {
                var chords = new List<KeyChord>();
                foreach (string text in texts ?? [])
                {
                    if (KeyChord.TryParse(text, out KeyChord chord) && !ReservedKeys.IsReserved(chord))
                    {
                        chords.Add(chord);
                    }
                }

                overrides[actionId] = chords;
            }

            keys[preset] = overrides;
        }

        foreach ((string presetName, Dictionary<string, Dictionary<string, string?>>? contexts) in file.Mouse ?? [])
        {
            if (!ShortcutPresetNames.TryParse(presetName, out ShortcutPreset preset) || contexts is null)
            {
                continue;
            }

            var byContext = new Dictionary<MouseContext, IReadOnlyDictionary<MouseChord, string?>>();
            foreach ((string contextName, Dictionary<string, string?>? chords) in contexts)
            {
                if (!MouseNames.TryParseContext(contextName, out MouseContext context) || chords is null)
                {
                    continue;
                }

                var map = new Dictionary<MouseChord, string?>();
                foreach ((string text, string? actionId) in chords)
                {
                    if (MouseChord.TryParse(text, out MouseChord chord))
                    {
                        map[chord] = actionId;
                    }
                }

                byContext[context] = map;
            }

            mouse[preset] = byContext;
        }

        registry.Restore(new ShortcutSnapshot(keys, mouse));
    }
}
