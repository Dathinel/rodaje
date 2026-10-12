namespace Rodaje.Packages.Content;

/// <summary>Qué marca una zona: lo que tapa la interfaz (sombreado) o el borde de un margen seguro (línea discontinua).</summary>
public enum SafeZoneKind
{
    /// <summary>Botones, pie de foto, nombre, barras: no poner títulos debajo.</summary>
    Interface,

    /// <summary>Lo de dentro se ve siempre (margen de acción o de títulos).</summary>
    Safe,
}

/// <summary>Un rectángulo en fracciones de la imagen (0 a 1, desde arriba a la izquierda) con su etiqueta.</summary>
public sealed record SafeZone(string Label, SafeZoneKind Kind, double Left, double Top, double Width, double Height);

/// <summary>
/// <c>rodaje.safezones</c> v1: zonas para el visor, como la interfaz de una red sobre un video vertical o unos márgenes
/// propios. Sin imágenes: solo rectángulos y etiquetas que Rodaje dibuja a su manera. <see cref="AspectWidth"/> y
/// <see cref="AspectHeight"/> son la proporción para la que se pensó (9 y 16), o 0 si sirve para cualquiera;
/// <see cref="Center"/> dibuja la cruz del centro.
/// </summary>
public sealed record SafeZonePreset(string Id, string Name, int AspectWidth, int AspectHeight, IReadOnlyList<SafeZone> Zones, bool Center)
{
    public const string Format = "rodaje.safezones";
    public const int Version = 1;

    /// <summary>Si se pensó para la forma <paramref name="width"/> × <paramref name="height"/> (con 1 % de margen), o para cualquiera.</summary>
    public bool Fits(int width, int height) =>
        AspectWidth <= 0 || AspectHeight <= 0 || width <= 0 || height <= 0
        || Math.Abs(((double)width / height) - ((double)AspectWidth / AspectHeight)) <= 0.01 * AspectWidth / AspectHeight;

    /// <summary>«9:16», o vacío si sirve para cualquier forma.</summary>
    public string AspectText => AspectWidth > 0 && AspectHeight > 0
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{AspectWidth}:{AspectHeight}")
        : "";
}
