namespace Rodaje.App.Services.Shortcuts;

/// <summary>Juegos de atajos de fábrica: los de Rodaje y los de los programas que la persona puede conocer.</summary>
public enum ShortcutPreset
{
    Rodaje,
    CapCut,
    Vegas,
}

/// <summary>
/// Una acción que se puede lanzar con el teclado: su identificador estable, el texto y el grupo de la página de atajos,
/// y sus teclas de fábrica en cada estilo.
/// </summary>
/// <remarks>
/// Las teclas se escriben como texto («Ctrl+Mayús+Z», «Supr», «Alt+[») y se leen al crear la acción. Si un estilo no
/// las da (<c>null</c>), usa las de Rodaje; un arreglo vacío deja la acción sin tecla en ese estilo.
/// </remarks>
public sealed class ShortcutAction
{
    private readonly KeyChord[] _rodaje;
    private readonly KeyChord[]? _capCut;
    private readonly KeyChord[]? _vegas;

    public ShortcutAction(string id, string label, string group, string[] rodaje, string[]? capCut = null, string[]? vegas = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(rodaje);
        Id = id;
        Label = label;
        Group = group;
        _rodaje = Parse(rodaje);
        _capCut = capCut is null ? null : Parse(capCut);
        _vegas = vegas is null ? null : Parse(vegas);
    }

    public string Id { get; }

    /// <summary>Lo que hace, en español, como se ve en menús y en la página de atajos.</summary>
    public string Label { get; }

    /// <summary>Grupo de la página de atajos («Edición», «Reproducción»...).</summary>
    public string Group { get; }

    /// <summary>Sigue repitiéndose mientras la tecla está apretada (por ejemplo, avanzar un cuadro).</summary>
    public bool Repeats { get; init; }

    /// <summary>Funciona aunque el foco esté en un campo de texto (por ejemplo, Guardar).</summary>
    public bool WorksInTextInput { get; init; }

    public IReadOnlyList<KeyChord> DefaultKeys(ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.CapCut => _capCut ?? _rodaje,
        ShortcutPreset.Vegas => _vegas ?? _rodaje,
        _ => _rodaje,
    };

    private static KeyChord[] Parse(string[] keys) => Array.ConvertAll(keys, KeyChord.Parse);
}
