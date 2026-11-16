using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange;

public enum InterchangeFormat
{
    /// <summary>EDL CMX 3600: una pista de video por archivo, en cuadros enteros.</summary>
    Edl,

    /// <summary>FCPXML 1.10 (se lee también 1.8 a 1.13).</summary>
    Fcpxml,

    /// <summary>OpenTimelineIO en JSON (<c>.otio</c>).</summary>
    Otio,
}

/// <summary>Opciones de un intercambio; las que no aplican a un formato o a una dirección se ignoran.</summary>
public sealed class InterchangeOptions
{
    /// <summary>
    /// Al leer una EDL, que no dice su tasa: por defecto la de la secuencia activa, o 30000/1001 si la EDL dice
    /// <c>FCM: DROP FRAME</c>. Los demás formatos traen la suya.
    /// </summary>
    public Rational? FrameRate { get; set; }

    /// <summary>Al exportar, TC del primer cuadro de la secuencia si ella no tiene uno propio (<c>01:00:00:00</c>, lo habitual).</summary>
    public string DefaultStartTimecode { get; set; } = "01:00:00:00";

    /// <summary>Pistas de video que se exportan (índices de <c>VideoTracks</c>); nula, todas. En EDL, un archivo por pista.</summary>
    public IReadOnlyList<int>? VideoTracks { get; set; }

    /// <summary>Exportar también el audio.</summary>
    public bool IncludeAudio { get; set; } = true;

    /// <summary>EDL: carretes con el nombre completo del archivo en vez de 8 caracteres (Resolve los lee; otros no).</summary>
    public bool LongReelNames { get; set; }

    /// <summary>Al importar, carpeta donde buscar los medios que no están en su ruta.</summary>
    public string? MediaSearchFolder { get; set; }

    public InterchangeLimits Limits { get; set; } = InterchangeLimits.Default;
}
