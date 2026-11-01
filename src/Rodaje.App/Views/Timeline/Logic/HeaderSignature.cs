namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Lo que obliga a rearmar las cabeceras de pista: las pistas con su nombre, papel y estado, la altura y posición de cada
/// fila, si la principal es magnética, el ancho de la columna y el tamaño del texto de la línea de tiempo (Preferencias lo
/// cambia sin reiniciar). Si la firma no cambia, las cabeceras se quedan como están.
/// </summary>
internal static class HeaderSignature
{
    public static string Of(TrackLayout layout, bool magnetic, double width, double fontScale)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return string.Join("|", layout.Rows.Select(r =>
            $"{r.TrackId}:{r.Track.Name}:{r.Track.Role}:{r.Track.Hidden}:{r.Track.Muted}:{r.Track.Solo}:{r.Track.Locked}:{r.IsMain}:{r.Top}:{r.Height}"))
            + $"|{magnetic}|{width}|{fontScale}";
    }
}
