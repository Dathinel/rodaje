namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Lógica pura para el recorte de etiquetas de marcadores contiguos en la regla de la línea de tiempo.
/// </summary>
public static class MarkerLabelLayout
{
    /// <summary>Desplazamiento horizontal de la etiqueta respecto al inicio del marcador (icono de bandera).</summary>
    public const double DefaultLabelOffset = 8.0;

    /// <summary>Ancho aproximado por carácter en píxeles para tipografía micro (10 pt Segoe UI).</summary>
    public const double DefaultCharWidth = 6.0;

    /// <summary>Puntos suspensivos usados para truncar.</summary>
    public const string DefaultEllipsis = "…";

    /// <summary>
    /// Calcula el ancho disponible para la etiqueta de un marcador situado en <paramref name="x"/>
    /// hasta el siguiente marcador en <paramref name="nextMarkerX"/>.
    /// Si no hay siguiente marcador, el espacio es infinito.
    /// </summary>
    public static double AvailableWidth(double x, double? nextMarkerX, double labelOffset = DefaultLabelOffset)
    {
        if (nextMarkerX is not { } next)
        {
            return double.PositiveInfinity;
        }

        return Math.Max(0, next - (x + labelOffset));
    }

    /// <summary>
    /// Recorta el nombre de un marcador si se solaparía con el siguiente marcador.
    /// Si el texto cabe en <paramref name="availableWidth"/>, lo devuelve intacto.
    /// Si no cabe, acorta el texto de la izquierda con puntos suspensivos hasta donde quepa.
    /// Si no cabe ni el primer carácter con los puntos suspensivos, devuelve <c>null</c> (indicando
    /// que solo debe mostrarse el marcador gráfico, y su nombre en el tooltip).
    /// </summary>
    public static string? Truncate(
        string? name,
        double availableWidth,
        double charWidth = DefaultCharWidth,
        string ellipsis = DefaultEllipsis)
    {
        if (string.IsNullOrEmpty(name) || availableWidth <= 0 || charWidth <= 0)
        {
            return null;
        }

        return Truncate(name, availableWidth, s => s.Length * charWidth, ellipsis);
    }

    /// <summary>
    /// Recorta el nombre de un marcador empleando una función personalizada de medición de texto.
    /// Si no cabe ni el primer carácter con los puntos suspensivos, devuelve <c>null</c>.
    /// </summary>
    public static string? Truncate(
        string? name,
        double availableWidth,
        Func<string, double> measure,
        string ellipsis = DefaultEllipsis)
    {
        ArgumentNullException.ThrowIfNull(measure);

        if (string.IsNullOrEmpty(name) || availableWidth <= 0)
        {
            return null;
        }

        if (measure(name) <= availableWidth)
        {
            return name;
        }

        string minCandidate = name[..1] + ellipsis;
        if (measure(minCandidate) > availableWidth)
        {
            return null;
        }

        int best = 1;
        int low = 1;
        int high = name.Length - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            string candidate = name[..mid] + ellipsis;
            if (measure(candidate) <= availableWidth)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return name[..best] + ellipsis;
    }
}
