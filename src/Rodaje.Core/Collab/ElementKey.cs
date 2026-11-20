namespace Rodaje.Core.Collab;

/// <summary>Clases de elemento que viajan por separado en un parche (<c>docs/propuestas/colaboracion-api-parches.md</c>).</summary>
public enum ElementKind
{
    /// <summary>Un medio del proyecto, sin su ruta.</summary>
    Media,

    /// <summary>La transcripción de un medio (su identificador es el del medio).</summary>
    Transcript,

    /// <summary>La cabecera de una secuencia: formato, mezcla maestra y buses; sin pistas, marcadores ni regiones.</summary>
    Sequence,

    /// <summary>Una pista (con sus efectos, envíos y transiciones) sin sus clips; su padre es la secuencia.</summary>
    Track,

    /// <summary>Un clip; su padre es la pista.</summary>
    Clip,

    /// <summary>Un marcador; su padre es la secuencia.</summary>
    Marker,

    /// <summary>Una región; su padre es la secuencia.</summary>
    Region,
}

/// <summary>Identifica un elemento compartido. Los identificadores son únicos en todo el proyecto, pero se separa por clase para no mezclarlos.</summary>
public readonly record struct ElementKey(ElementKind Kind, string Id)
{
    /// <summary>Nombre del tipo en el JSON de red.</summary>
    public string WireKind => ElementKinds.ToWire(Kind);

    public override string ToString() => $"{WireKind}:{Id}";
}

/// <summary>Nombres de las clases de elemento en el JSON de red.</summary>
public static class ElementKinds
{
    public static string ToWire(ElementKind kind) => kind switch
    {
        ElementKind.Media => "media",
        ElementKind.Transcript => "transcript",
        ElementKind.Sequence => "sequence",
        ElementKind.Track => "track",
        ElementKind.Clip => "clip",
        ElementKind.Marker => "marker",
        ElementKind.Region => "region",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string? wire, out ElementKind kind)
    {
        switch (wire)
        {
            case "media": kind = ElementKind.Media; return true;
            case "transcript": kind = ElementKind.Transcript; return true;
            case "sequence": kind = ElementKind.Sequence; return true;
            case "track": kind = ElementKind.Track; return true;
            case "clip": kind = ElementKind.Clip; return true;
            case "marker": kind = ElementKind.Marker; return true;
            case "region": kind = ElementKind.Region; return true;
            default: kind = default; return false;
        }
    }
}
