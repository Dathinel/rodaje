using System.Globalization;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Lo que lleva un medio arrastrado desde la biblioteca (<c>rodaje/media-id</c>): el id del medio y, si es un subclip,
/// su tramo en ticks («medio|entrada|salida»). Varias fichas arrastradas juntas van una por línea.
/// </summary>
public readonly record struct MediaDrop(string MediaId, (Time In, Time Out)? Range)
{
    public static string Format(string mediaId, (Time In, Time Out)? range = null) =>
        range is { } r ? string.Create(CultureInfo.InvariantCulture, $"{mediaId}|{r.In.Ticks}|{r.Out.Ticks}") : mediaId;

    /// <summary>
    /// El arrastre leído (la primera ficha si son varias); un texto vacío es <c>null</c> y un tramo que no se entiende se
    /// ignora (entra el medio entero).
    /// </summary>
    public static MediaDrop? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string[] parts = text.Split('\n')[0].Split('|');
        if (parts.Length == 3
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long a)
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)
            && b > a)
        {
            return new MediaDrop(parts[0], (Time.FromTicks(a), Time.FromTicks(b)));
        }

        return new MediaDrop(parts[0], null);
    }

    /// <summary>Todas las fichas del arrastre, en orden.</summary>
    public static IReadOnlyList<MediaDrop> ParseAll(string? text) =>
        [.. (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Parse).OfType<MediaDrop>()];
}
