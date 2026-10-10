namespace Rodaje.App.Services.Shortcuts;

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
    private readonly KeyChord[]? _agile;
    private readonly KeyChord[]? _classic;
    private readonly string? _shortLabel;

    public ShortcutAction(string id, string label, string group, string[] rodaje, string[]? agile = null, string[]? classic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(rodaje);
        Id = id;
        Label = label;
        Group = group;
        _rodaje = Parse(rodaje);
        _agile = agile is null ? null : Parse(agile);
        _classic = classic is null ? null : Parse(classic);
    }

    public string Id { get; }

    /// <summary>Lo que hace, en español, como se ve en menús y en la página de atajos.</summary>
    public string Label { get; }

    /// <summary>Qué hace, en una frase corta y sencilla, para la página de atajos; vacío si no hace falta explicarlo.</summary>
    public string Description { get; init; } = "";

    /// <summary>Una o dos palabras para el teclado dibujado («Cortar», «Ver todo»); si no se da, el texto completo.</summary>
    public string ShortLabel
    {
        get => string.IsNullOrWhiteSpace(_shortLabel) ? Label : _shortLabel;
        init => _shortLabel = value;
    }

    /// <summary>Grupo de la página de atajos («Edición», «Reproducción»...).</summary>
    public string Group { get; }

    /// <summary>Sigue repitiéndose mientras la tecla está apretada (por ejemplo, avanzar un cuadro).</summary>
    public bool Repeats { get; init; }

    /// <summary>Funciona aunque el foco esté en un campo de texto (por ejemplo, Guardar).</summary>
    public bool WorksInTextInput { get; init; }

    public IReadOnlyList<KeyChord> DefaultKeys(ShortcutPreset preset) => preset switch
    {
        ShortcutPreset.Agile => _agile ?? _rodaje,
        ShortcutPreset.Classic => _classic ?? _rodaje,
        _ => _rodaje,
    };

    private static KeyChord[] Parse(string[] keys) => Array.ConvertAll(keys, KeyChord.Parse);
}
