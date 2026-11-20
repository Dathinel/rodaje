using Rodaje.App.Views.Timeline;

namespace Rodaje.App.Services.Settings;

/// <summary>
/// El alto de la línea de tiempo como parte del alto de la ventana, para que se adapte al cambiar su tamaño. Nunca baja
/// de lo que deja ver tres pistas ni le quita al visor su mínimo.
/// </summary>
public static class TimelineProportion
{
    public const double Balanced = 0.42;
    public const double MoreViewer = 0.30;
    public const double MoreTimeline = 0.55;
    public const double Min = 0.10;
    public const double Max = 0.85;
    public const double ViewerMinHeight = 240;

    /// <summary>El alto fijo de antes de la versión 4 de los ajustes, que se tenía sin haberlo tocado.</summary>
    public const double LegacyDefaultHeight = 300;

    /// <summary>
    /// El mínimo de antes: casi siempre quedaba guardado sin quererlo, al cerrar con la ventana chica (se guardaba el
    /// alto real de la fila, ya apretada).
    /// </summary>
    public const double LegacyMinHeight = 160;

    private const double LegacyWindowHeight = 900;

    /// <summary>Barra (36), regla (28) y barra de desplazamiento (16), más la principal, una de video y una de audio.</summary>
    public const double MinHeight = 36 + 28 + 16 + (2 * TrackLayout.TopPadding) + TrackLayout.MainRowHeight + TrackLayout.VideoRowHeight
        + TrackLayout.AudioRowHeight + (3 * TrackLayout.RowGap) + TrackLayout.SectionGap;

    public static double Normalize(double share) => double.IsFinite(share) ? Math.Clamp(share, Min, Max) : Balanced;

    /// <summary>El alto en la ventana de <paramref name="rootHeight"/>, donde las demás filas fijas miden <paramref name="otherRows"/>.</summary>
    public static double Height(double share, double rootHeight, double otherRows)
    {
        double max = Math.Max(0, rootHeight - otherRows - ViewerMinHeight);
        return Math.Min(Math.Max(Normalize(share) * rootHeight, MinHeight), max);
    }

    /// <summary>La proporción de un alto elegido a mano (al soltar el separador).</summary>
    public static double ShareOf(double height, double rootHeight) => rootHeight > 0 ? Normalize(height / rootHeight) : Balanced;

    /// <summary>
    /// La proporción equivalente al alto en píxeles de los ajustes viejos, sobre el alto de ventana guardado; el alto
    /// de fábrica y el mínimo de antes pasan a la distribución equilibrada.
    /// </summary>
    public static double FromLegacyHeight(double? height, double windowHeight) =>
        height is { } h && double.IsFinite(h) && h != LegacyDefaultHeight && h > LegacyMinHeight
            ? Normalize(h / (windowHeight > 0 ? windowHeight : LegacyWindowHeight))
            : Balanced;

    /// <summary>Doble clic en el separador: de la mitad para arriba pasa a «más visor»; si no, a «más línea de tiempo».</summary>
    public static double Toggle(double share) => share >= (MoreViewer + MoreTimeline) / 2 ? MoreViewer : MoreTimeline;
}
