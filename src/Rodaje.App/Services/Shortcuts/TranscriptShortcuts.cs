namespace Rodaje.App.Services.Shortcuts;

public static class TranscriptShortcuts
{
    public const string Delete = "transcript.delete";
    public const string Lift = "transcript.lift";
    public const string Cut = "transcript.cut";
    public const string Paste = "transcript.paste";
    public const string Find = "transcript.find";
    public const string SelectAll = "transcript.selectAll";
    public const string ExtendLeft = "transcript.extendLeft";
    public const string ExtendRight = "transcript.extendRight";

    public static IReadOnlyList<ShortcutAction> Catalog { get; } =
    [
        new("transcript.removeFillers", "Quitar muletillas…", "Transcripción", []),
        new(Delete, "Borrar palabras con ripple", "Transcripción", ["Supr"]),
        new(Lift, "Borrar palabras dejando hueco", "Transcripción", ["Retroceso"]),
        new(Cut, "Cortar frase", "Transcripción", ["Ctrl+X"]),
        new(Paste, "Mover frase aquí", "Transcripción", ["Ctrl+V"]),
        new(Find, "Buscar en la transcripción", "Transcripción", ["Ctrl+F"]),
        new(SelectAll, "Seleccionar todas las palabras", "Transcripción", ["Ctrl+A"]),
        new(ExtendLeft, "Ampliar a la palabra anterior", "Transcripción", ["Ctrl+Mayús+Izquierda"]),
        new(ExtendRight, "Ampliar a la palabra siguiente", "Transcripción", ["Ctrl+Mayús+Derecha"]),
    ];
}
