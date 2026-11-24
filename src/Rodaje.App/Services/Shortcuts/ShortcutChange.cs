namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Un cambio de atajos hecho desde la página, con el aviso que se le muestra a la persona y lo necesario para
/// deshacerlo (<see cref="ShortcutCustomizer.Undo"/>).
/// </summary>
public sealed class ShortcutChange
{
    internal ShortcutChange(string message, ShortcutPreset preset, ShortcutSnapshot? before, string? warning = null, bool allPresets = false)
    {
        Message = message;
        Preset = preset;
        Before = before;
        Warning = warning;
        AllPresets = allPresets;
    }

    /// <summary>El aviso, en español: «"S" era de "Cortar en el cursor": se intercambiaron».</summary>
    public string Message { get; }

    /// <summary>Una advertencia que no impidió el cambio (Ctrl+Alt es AltGr en los teclados en español).</summary>
    public string? Warning { get; }

    /// <summary>Cambió algo y se puede deshacer.</summary>
    public bool Changed => Before is not null;

    public ShortcutPreset Preset { get; }

    internal ShortcutSnapshot? Before { get; }

    /// <summary>Tocó todos los estilos (importar): deshacerlo los devuelve todos.</summary>
    internal bool AllPresets { get; }
}
