using Rodaje.App.Services.Shortcuts;
using Rodaje.Packages;

namespace Rodaje.App.Services.Settings;

/// <summary>
/// Lo que Preferencias cambia y solo se confirma al pulsar Guardar: un perfil importado, «Restablecer todo» y los temas
/// borrados o renombrados. Trabaja sobre copias de los ajustes y de los atajos; <see cref="Commit"/> repite los pasos
/// sobre los vivos y <see cref="CommitThemes"/> escribe los temas. Cancelar es no llamar a ninguno (y releer los temas).
/// </summary>
public sealed class PersonalizationDraft
{
    private readonly List<Action<AppSettings, ShortcutRegistry>> _steps = [];
    private readonly HashSet<string> _deletedThemes = new(StringComparer.OrdinalIgnoreCase);

    public PersonalizationDraft(AppSettings settings, ShortcutRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registry);
        Settings = settings.Clone();
        Registry = new ShortcutRegistry();
        Registry.RegisterRange(registry.Actions);
        Registry.Restore(registry.TakeSnapshot());
        Registry.Preset = registry.Preset;
    }

    /// <summary>La copia de los ajustes con lo pendiente ya aplicado: de aquí se rellenan los controles y se exporta.</summary>
    public AppSettings Settings { get; }

    /// <summary>La copia de los atajos y del ratón con lo pendiente ya aplicado.</summary>
    public ShortcutRegistry Registry { get; }

    /// <summary>Hay un perfil importado o un «Restablecer todo» por confirmar.</summary>
    public bool HasProfileChanges => _steps.Count > 0;

    /// <summary>
    /// Lee un <c>.rodaje-perfil</c> y lo aplica a las copias; su tema propio pasa a <paramref name="themes"/> en memoria.
    /// Devuelve el motivo en español si no se pudo; entonces no cambia nada.
    /// </summary>
    public string? Import(string path, ThemeStore? themes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json;
        try
        {
            json = SafeText.ReadAllTextStrict(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return $"No se pudo leer {Path.GetFileName(path)}: {ex.Message}";
        }

        if (!PersonalProfile.Apply(json, Settings, Registry, themes))
        {
            return $"{Path.GetFileName(path)} no es un perfil de Rodaje o es de una versión más nueva.";
        }

        _steps.Add((settings, registry) => PersonalProfile.Apply(json, settings, registry, null));
        return null;
    }

    public void ResetAll()
    {
        PersonalProfile.ResetAll(Settings, Registry);
        _steps.Add(PersonalProfile.ResetAll);
    }

    /// <summary>Quita un tema propio de la lista; su archivo se borra en <see cref="CommitThemes"/>.</summary>
    public void DeleteTheme(ThemeStore themes, string name)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (themes.Own.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _deletedThemes.Add(name);
        }
    }

    /// <summary>Cambia el nombre en memoria; el archivo viejo se borra y el nuevo se escribe en <see cref="CommitThemes"/>.</summary>
    public void RenameTheme(ThemeDefinition theme, string name)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _deletedThemes.Add(theme.Name);
        theme.Name = name;
    }

    /// <summary>Repite sobre los ajustes y los atajos vivos lo importado o restablecido, en el mismo orden.</summary>
    public void Commit(AppSettings settings, ShortcutRegistry registry)
    {
        foreach (Action<AppSettings, ShortcutRegistry> step in _steps)
        {
            step(settings, registry);
        }

        _steps.Clear();
    }

    /// <summary>Borra los archivos de los temas quitados o renombrados y escribe los propios que quedan (antes los borrados: un nombre puede volver).</summary>
    public void CommitThemes(ThemeStore themes)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ThemeDefinition[] keep = [.. themes.Own];
        foreach (string name in _deletedThemes)
        {
            themes.Delete(name);
        }

        foreach (ThemeDefinition theme in keep)
        {
            themes.Save(theme);
        }

        _deletedThemes.Clear();
    }
}
