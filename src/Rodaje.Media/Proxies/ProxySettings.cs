using System.Globalization;

namespace Rodaje.Media.Proxies;

/// <summary>Cómo se codifica un proxy. Dos proxies del mismo origen con ajustes distintos conviven en el almacén.</summary>
public sealed record ProxySettings
{
    /// <summary>Se sube cuando cambia lo que produce el generador, para que los proxies viejos no se reutilicen.</summary>
    public const int FormatVersion = 1;

    public static ProxySettings Default { get; } = new();

    /// <summary>Alto del proxy en píxeles (540 o 720); un origen más bajo conserva su alto.</summary>
    public int TargetHeight { get; init; } = 540;

    /// <summary>Cuadros entre keyframes: con GOP corto, cualquier seek decodifica pocos cuadros.</summary>
    public int GopLength { get; init; } = 15;

    /// <summary>Tasa de bits para 30 fps; se escala con la tasa del origen.</summary>
    public int BitrateKbpsAt30Fps => TargetHeight <= 540 ? 3_000 : 5_000;

    /// <summary>Identificador corto de los ajustes, parte del nombre de archivo del proxy.</summary>
    public string Tag => string.Create(CultureInfo.InvariantCulture, $"h{TargetHeight}-g{GopLength}-v{FormatVersion}");

    public void Validate()
    {
        if (TargetHeight is not (540 or 720))
        {
            throw new ArgumentOutOfRangeException(nameof(TargetHeight), $"El alto del proxy debe ser 540 o 720; se recibió {TargetHeight}.");
        }

        if (GopLength is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(GopLength), $"El GOP del proxy debe estar entre 1 y 120; se recibió {GopLength}.");
        }
    }

    /// <summary>
    /// Tamaño del proxy en orientación de pantalla, con píxeles cuadrados y lados pares, sin agrandar nunca.
    /// <see cref="TargetHeight"/> se aplica al lado corto: un video vertical de 1080 × 1920 da 540 × 960.
    /// </summary>
    public (int Width, int Height) OutputSize(int displayWidth, int displayHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(displayWidth, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(displayHeight, 2);
        bool portrait = displayHeight > displayWidth;
        int shortSide = portrait ? displayWidth : displayHeight;
        int longSide = portrait ? displayHeight : displayWidth;
        int newShort = Math.Max(2, Math.Min(TargetHeight, shortSide) & ~1);
        int newLong = Math.Max(2, (int)Math.Round((double)longSide * newShort / shortSide / 2, MidpointRounding.AwayFromZero) * 2);
        return portrait ? (newShort, newLong) : (newLong, newShort);
    }
}
