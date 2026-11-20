namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// Atajos del panel Transcripción. «Quitar muletillas…» es una acción más del catálogo; las teclas del panel son fijas y
/// solo valen con el foco en él, así que no entran en el catálogo, donde chocarían con las mismas de la línea de tiempo.
/// </summary>
public static class TranscriptShortcuts
{
    public const string RemoveFillers = "transcript.removeFillers";
    public const string Delete = "transcript.delete";
    public const string Lift = "transcript.lift";
    public const string Cut = "transcript.cut";
    public const string Paste = "transcript.paste";
    public const string Find = "transcript.find";
    public const string SelectAll = "transcript.selectAll";
    public const string ExtendLeft = "transcript.extendLeft";
    public const string ExtendRight = "transcript.extendRight";
    public const string CorrectWord = "transcript.correctWord";
    public const string AssignSpeaker = "transcript.assignSpeaker";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new(RemoveFillers, "Quitar muletillas…", ShortcutGroups.Tools, []),
    ];

    /// <summary>Teclas del panel con el foco en él.</summary>
    public static IReadOnlyList<(string Id, KeyChord Chord)> PanelKeys { get; } =
    [
        (Delete, KeyChord.Parse("Supr")),
        (Lift, KeyChord.Parse("Retroceso")),
        (Cut, KeyChord.Parse("Ctrl+X")),
        (Paste, KeyChord.Parse("Ctrl+V")),
        (Find, KeyChord.Parse("Ctrl+F")),
        (SelectAll, KeyChord.Parse("Ctrl+A")),
        (ExtendLeft, KeyChord.Parse("Ctrl+Mayús+Izquierda")),
        (ExtendRight, KeyChord.Parse("Ctrl+Mayús+Derecha")),
        (CorrectWord, KeyChord.Parse("F2")),
        .. Enumerable.Range(0, 10).Select(n => (AssignSpeaker + n, KeyChord.Parse("Alt+" + n))),
    ];
}
