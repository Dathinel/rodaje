namespace Rodaje.App.Views.Library;

/// <summary>
/// Los subclips agrupados por su medio, en el orden de la lista. La biblioteca pone cada medio visible seguido de sus
/// subclips; buscarlos con un filtro por cada medio recorría la lista entera de subclips una vez por medio
/// (medios × subclips en cada reconstrucción de la lista). Se agrupan una sola vez por lista. Sin WinUI.
/// </summary>
public sealed class SubclipsByMedia
{
    private readonly Dictionary<string, List<Subclip>> _byMedia = new(StringComparer.Ordinal);

    /// <summary>Agrupa <paramref name="subclips"/> por <see cref="Subclip.MediaId"/> recorriéndolos una vez.</summary>
    public SubclipsByMedia(IEnumerable<Subclip> subclips)
    {
        ArgumentNullException.ThrowIfNull(subclips);
        foreach (Subclip subclip in subclips)
        {
            if (!_byMedia.TryGetValue(subclip.MediaId, out List<Subclip>? list))
            {
                list = [];
                _byMedia[subclip.MediaId] = list;
            }

            list.Add(subclip);
        }
    }

    /// <summary>Los subclips de <paramref name="mediaId"/> en el orden de la lista; vacío si no tiene.</summary>
    public IReadOnlyList<Subclip> Of(string mediaId) =>
        _byMedia.TryGetValue(mediaId, out List<Subclip>? list) ? list : [];

    /// <summary>
    /// Cada medio de <paramref name="media"/> que da elemento, seguido de los de sus subclips, en el orden dado. Los medios
    /// para los que <paramref name="mediaItem"/> da <see langword="null"/> se saltan con sus subclips.
    /// </summary>
    public static List<TItem> Interleave<TMedia, TItem>(IEnumerable<TMedia> media, Func<TMedia, string> mediaId, IEnumerable<Subclip> subclips,
        Func<TMedia, TItem?> mediaItem, Func<Subclip, TMedia, TItem> subclipItem)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(mediaId);
        ArgumentNullException.ThrowIfNull(mediaItem);
        ArgumentNullException.ThrowIfNull(subclipItem);
        var groups = new SubclipsByMedia(subclips);
        var items = new List<TItem>();
        foreach (TMedia entry in media)
        {
            if (mediaItem(entry) is not { } item)
            {
                continue;
            }

            items.Add(item);
            foreach (Subclip subclip in groups.Of(mediaId(entry)))
            {
                items.Add(subclipItem(subclip, entry));
            }
        }

        return items;
    }
}
