using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Library;

/// <summary>La marca de un medio: favorito, rechazado o ninguna (se excluyen entre sí).</summary>
public enum MediaMark
{
    None,
    Favorite,
    Rejected,
}

/// <summary>Un tramo con nombre de un medio, <c>[In, Out)</c>, que se usa como un medio más (solo organiza la vista).</summary>
public sealed partial record Subclip(string Id, string MediaId, string Name, Time In, Time Out)
{
    public string Id
    {
        get
        {
            CountKeyRead();
            return field;
        }

        init;
    } = Id;

    public string MediaId
    {
        get
        {
            CountKeyRead();
            return field;
        }

        init;
    } = MediaId;

    public Time Duration => Out - In;

    /// <summary>
    /// Costura de prueba: cada lectura de <see cref="Id"/> o de <see cref="MediaId"/>. Buscar un subclip o los de un medio
    /// recorriendo la lista lee la clave de cada uno, así que las pruebas de rendimiento cuentan esto en vez de medir
    /// segundos. Sin cuerpo en la app, el compilador quita la llamada; el cuerpo está en las pruebas.
    /// </summary>
    static partial void CountKeyRead();
}

/// <summary>Un ángulo de una multicámara: su medio y dónde cae su instante 0 en el reloj común.</summary>
public sealed record MulticamAngleEntry(string MediaId, Time Offset);

/// <summary>Medios sincronizados por su sonido para cortar entre ellos; el primero es la referencia.</summary>
public sealed record MulticamGroup(string Id, string Name, IReadOnlyList<MulticamAngleEntry> Angles);

/// <summary>Valoración, marca, etiquetas, subclips, colecciones inteligentes y multicámaras de la biblioteca.</summary>
public sealed partial class LibraryOrganization
{
    public const int MaxRating = 5;

    /// <summary>Largo máximo de una etiqueta y cuántas puede tener un medio.</summary>
    public const int MaxTagLength = 40;

    public const int MaxTagsPerMedia = 32;

    private readonly Dictionary<string, int> _ratings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _tags = new(StringComparer.Ordinal);
    private readonly List<Subclip> _subclips = [];

    /// <summary>
    /// Los mismos subclips por id. La vista los busca uno por uno al rehacer la lista (los que ya no están) y recorrer
    /// la lista entera por cada uno era cuadrático con miles de subclips.
    /// </summary>
    private readonly Dictionary<string, Subclip> _subclipsById = new(StringComparer.Ordinal);
    private readonly List<SmartBin> _collections = [];
    private readonly List<MulticamGroup> _multicams = [];

    public IReadOnlyDictionary<string, int> Ratings => _ratings;

    public IReadOnlyCollection<string> Rejected => _rejected;

    public IReadOnlyList<Subclip> Subclips => _subclips;

    public IReadOnlyList<SmartBin> Collections => _collections;

    public IReadOnlyList<MulticamGroup> Multicams => _multicams;

    /// <summary>Medio → etiquetas, en el orden en que se pusieron.</summary>
    public IReadOnlyDictionary<string, List<string>> TagMap => _tags;

    /// <summary>De 0 (sin valorar) a <see cref="MaxRating"/> estrellas.</summary>
    public int RatingOf(string mediaId) => _ratings.GetValueOrDefault(mediaId);

    public void SetRating(string mediaId, int rating)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        rating = Math.Clamp(rating, 0, MaxRating);
        if (rating == RatingOf(mediaId))
        {
            return;
        }

        if (rating == 0)
        {
            _ratings.Remove(mediaId);
        }
        else
        {
            _ratings[mediaId] = rating;
        }

        Revision++;
    }

    public MediaMark MarkOf(string mediaId) =>
        _favorites.Contains(mediaId) ? MediaMark.Favorite : _rejected.Contains(mediaId) ? MediaMark.Rejected : MediaMark.None;

    /// <summary>Marca el medio; favorito y rechazado se excluyen.</summary>
    public void SetMark(string mediaId, MediaMark flag)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        if (MarkOf(mediaId) == flag)
        {
            return;
        }

        _favorites.Remove(mediaId);
        _rejected.Remove(mediaId);
        if (flag == MediaMark.Favorite)
        {
            _favorites.Add(mediaId);
        }
        else if (flag == MediaMark.Rejected)
        {
            _rejected.Add(mediaId);
        }

        Revision++;
    }

    public bool IsRejected(string mediaId) => _rejected.Contains(mediaId);

    public IReadOnlyList<string> TagsOf(string mediaId) => _tags.TryGetValue(mediaId, out List<string>? tags) ? tags : [];

    /// <summary>Todas las etiquetas usadas, sin repetir (sin mirar mayúsculas ni tildes) y por orden alfabético.</summary>
    public IReadOnlyList<string> AllTags() =>
        [.. _tags.Values.SelectMany(t => t).GroupBy(SearchText.Fold, StringComparer.Ordinal).Select(g => g.First()).OrderBy(SearchText.Fold, StringComparer.Ordinal)];

    /// <summary>Reemplaza las etiquetas del medio (limpias, sin repetir y como mucho <see cref="MaxTagsPerMedia"/>).</summary>
    public void SetTags(string mediaId, IEnumerable<string?> tags)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        ArgumentNullException.ThrowIfNull(tags);
        List<string> clean = CleanTags(tags);
        if (TagsOf(mediaId).SequenceEqual(clean, StringComparer.Ordinal))
        {
            return;
        }

        if (clean.Count == 0)
        {
            _tags.Remove(mediaId);
        }
        else
        {
            _tags[mediaId] = clean;
        }

        Revision++;
    }

    /// <summary>Pone (o quita) una etiqueta a varios medios a la vez.</summary>
    public void ToggleTag(IEnumerable<string> mediaIds, string tag, bool on)
    {
        ArgumentNullException.ThrowIfNull(mediaIds);
        string folded = SearchText.Fold(CleanTag(tag));
        if (folded.Length == 0)
        {
            return;
        }

        foreach (string mediaId in mediaIds)
        {
            List<string> current = [.. TagsOf(mediaId)];
            bool has = current.Exists(t => SearchText.Fold(t) == folded);
            if (on && !has)
            {
                current.Add(CleanTag(tag));
            }
            else if (!on && has)
            {
                current.RemoveAll(t => SearchText.Fold(t) == folded);
            }

            SetTags(mediaId, current);
        }
    }

    /// <summary>Etiquetas escritas separadas por comas: «boda, exteriores» → [«boda», «exteriores»].</summary>
    public static IReadOnlyList<string> ParseTags(string? text) => CleanTags((text ?? "").Split(',', ';'));

    public static string CleanTag(string? tag)
    {
        string text = Clean(tag);
        return text.Length > MaxTagLength ? text[..MaxTagLength].TrimEnd() : text;
    }

    private static List<string> CleanTags(IEnumerable<string?> tags)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? tag in tags)
        {
            string clean = CleanTag(tag);
            if (clean.Length > 0 && result.Count < MaxTagsPerMedia && seen.Add(SearchText.Fold(clean)))
            {
                result.Add(clean);
            }
        }

        return result;
    }

    /// <summary>Crea un subclip del medio; el tramo tiene que tener duración.</summary>
    public Subclip AddSubclip(string mediaId, string? name, Time @in, Time @out)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        if (@out <= @in || @in < Time.Zero)
        {
            throw new ArgumentException("El subclip necesita una salida después de su entrada.", nameof(@out));
        }

        string clean = Clean(name);
        var subclip = new Subclip(NewEntryId("subclip", _subclipsById.ContainsKey), mediaId, clean.Length == 0 ? "Subclip" : clean, @in, @out);
        _subclips.Add(subclip);
        _subclipsById.Add(subclip.Id, subclip);
        Revision++;
        return subclip;
    }

    public bool RemoveSubclip(string subclipId)
    {
        if (subclipId is null || !_subclipsById.Remove(subclipId))
        {
            return false;
        }

        _subclips.RemoveAll(s => s.Id == subclipId);

        Revision++;
        return true;
    }

    public bool RenameSubclip(string subclipId, string? name)
    {
        string clean = Clean(name);
        if (FindSubclip(subclipId) is not { } current || clean.Length == 0 || current.Name == clean)
        {
            return false;
        }

        Subclip renamed = current with { Name = clean };
        _subclips[_subclips.IndexOf(current)] = renamed;
        _subclipsById[renamed.Id] = renamed;
        Revision++;
        return true;
    }

    public Subclip? FindSubclip(string? subclipId) => subclipId is not null && _subclipsById.TryGetValue(subclipId, out Subclip? subclip) ? subclip : null;

    /// <summary>Guarda una colección inteligente (nueva o reemplazando la del mismo id).</summary>
    public SmartBin SaveCollection(SmartBin collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        string name = Clean(collection.Name);
        SmartBin clean = collection with
        {
            Id = string.IsNullOrEmpty(collection.Id) ? NewEntryId("coleccion", id => _collections.Exists(c => c.Id == id)) : collection.Id,
            Name = name.Length == 0 ? "Colección" : name,
        };
        int index = _collections.FindIndex(c => c.Id == clean.Id);
        if (index >= 0)
        {
            if (_collections[index] == clean)
            {
                return clean;
            }

            _collections[index] = clean;
        }
        else
        {
            _collections.Add(clean);
        }

        Revision++;
        return clean;
    }

    public bool RemoveCollection(string collectionId)
    {
        if (_collections.RemoveAll(c => c.Id == collectionId) == 0)
        {
            return false;
        }

        Revision++;
        return true;
    }

    public SmartBin? FindCollection(string? collectionId) => collectionId is null ? null : _collections.Find(c => c.Id == collectionId);

    public MulticamGroup AddMulticam(string? name, IReadOnlyList<MulticamAngleEntry> angles)
    {
        ArgumentNullException.ThrowIfNull(angles);
        if (angles.Count < 2)
        {
            throw new ArgumentException("Una multicámara necesita al menos dos ángulos.", nameof(angles));
        }

        string clean = Clean(name);
        var group = new MulticamGroup(NewEntryId("multicam", id => _multicams.Exists(m => m.Id == id)), clean.Length == 0 ? "Multicámara" : clean, [.. angles]);
        _multicams.Add(group);
        Revision++;
        return group;
    }

    public bool RemoveMulticam(string multicamId)
    {
        if (_multicams.RemoveAll(m => m.Id == multicamId) == 0)
        {
            return false;
        }

        Revision++;
        return true;
    }

    /// <summary>La multicámara que tiene un ángulo con ese medio, si hay.</summary>
    public MulticamGroup? MulticamOf(string mediaId) => _multicams.Find(m => m.Angles.Any(a => a.MediaId == mediaId));

    /// <summary>Olvida marcas, etiquetas, subclips y ángulos de medios que ya no están (las multicámaras con menos de dos ángulos se borran).</summary>
    private int PruneMarks(HashSet<string> existing)
    {
        int removed = 0;
        foreach (string id in _ratings.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            _ratings.Remove(id);
            removed++;
        }

        removed += _rejected.RemoveWhere(id => !existing.Contains(id));
        foreach (string id in _tags.Keys.Where(id => !existing.Contains(id)).ToList())
        {
            _tags.Remove(id);
            removed++;
        }

        int before = _subclips.Count;
        _subclips.RemoveAll(s => !existing.Contains(s.MediaId));
        if (_subclips.Count != before)
        {
            removed += before - _subclips.Count;
            _subclipsById.Clear();
            foreach (Subclip subclip in _subclips)
            {
                _subclipsById.Add(subclip.Id, subclip);
            }
        }
        for (int i = _multicams.Count - 1; i >= 0; i--)
        {
            MulticamGroup group = _multicams[i];
            List<MulticamAngleEntry> kept = [.. group.Angles.Where(a => existing.Contains(a.MediaId))];
            if (kept.Count == group.Angles.Count)
            {
                continue;
            }

            removed++;
            if (kept.Count < 2)
            {
                _multicams.RemoveAt(i);
            }
            else
            {
                _multicams[i] = group with { Angles = kept };
            }
        }

        return removed;
    }

    /// <summary>Carga lo leído del disco (sin subir <see cref="Revision"/>), descartando lo que no tiene sentido.</summary>
    internal void ImportMarks(LibraryMarks? marks)
    {
        if (marks is null)
        {
            return;
        }

        foreach ((string id, int rating) in marks.Ratings ?? [])
        {
            if (!string.IsNullOrEmpty(id) && rating is > 0 and <= MaxRating)
            {
                _ratings[id] = rating;
            }
        }

        foreach (string id in marks.Rejected ?? [])
        {
            if (!string.IsNullOrEmpty(id) && !_favorites.Contains(id))
            {
                _rejected.Add(id);
            }
        }

        foreach ((string id, List<string> tags) in marks.Tags ?? [])
        {
            List<string> clean = CleanTags(tags ?? []);
            if (!string.IsNullOrEmpty(id) && clean.Count > 0)
            {
                _tags[id] = clean;
            }
        }

        // Los ids ya vistos van en conjuntos: buscar en la lista por cada entrada era cuadrático con un archivo enorme.
        foreach (Subclip? subclip in marks.Subclips ?? [])
        {
            if (subclip is { Id.Length: > 0, MediaId.Length: > 0 } && subclip.Out > subclip.In && subclip.In >= Time.Zero && !_subclipsById.ContainsKey(subclip.Id))
            {
                Subclip clean = subclip with { Name = Clean(subclip.Name) is { Length: > 0 } n ? n : "Subclip" };
                _subclips.Add(clean);
                _subclipsById.Add(clean.Id, clean);
            }
        }

        var collectionIds = new HashSet<string>(_collections.Select(c => c.Id), StringComparer.Ordinal);
        foreach (SmartBin? collection in marks.Collections ?? [])
        {
            if (collection is { Id.Length: > 0 } && collectionIds.Add(collection.Id))
            {
                // Una regla con "value": null rompía al describirla o al filtrar con ella.
                _collections.Add(collection with
                {
                    Name = Clean(collection.Name) is { Length: > 0 } n ? n : "Colección",
                    Rules = [.. (collection.Rules ?? []).Where(r => r is { Value: not null })],
                });
            }
        }

        var multicamIds = new HashSet<string>(_multicams.Select(m => m.Id), StringComparer.Ordinal);
        foreach (MulticamGroup? group in marks.Multicams ?? [])
        {
            List<MulticamAngleEntry> angles = [.. (group?.Angles ?? []).Where(a => a is { MediaId.Length: > 0 })];
            if (group is { Id.Length: > 0 } && angles.Count >= 2 && multicamIds.Add(group.Id))
            {
                _multicams.Add(group with { Name = Clean(group.Name) is { Length: > 0 } n ? n : "Multicámara", Angles = angles });
            }
        }
    }

    /// <summary>Lo que se escribe al disco; <c>null</c> si no hay nada.</summary>
    internal LibraryMarks? ExportMarks()
    {
        if (_ratings.Count == 0 && _rejected.Count == 0 && _tags.Count == 0 && _subclips.Count == 0 && _collections.Count == 0 && _multicams.Count == 0)
        {
            return null;
        }

        return new LibraryMarks
        {
            Ratings = _ratings.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            Rejected = [.. _rejected.Order(StringComparer.Ordinal)],
            Tags = _tags.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal),
            Subclips = [.. _subclips],
            Collections = [.. _collections],
            Multicams = [.. _multicams],
        };
    }

    private static string NewEntryId(string prefix, Func<string, bool> taken)
    {
        while (true)
        {
            string id = prefix + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
            if (!taken(id))
            {
                return id;
            }
        }
    }
}

/// <summary>La parte del archivo de la biblioteca (versión 2) con marcas, etiquetas, subclips, colecciones y multicámaras.</summary>
internal sealed class LibraryMarks
{
    public Dictionary<string, int>? Ratings { get; set; }

    public List<string>? Rejected { get; set; }

    public Dictionary<string, List<string>>? Tags { get; set; }

    public List<Subclip?>? Subclips { get; set; }

    public List<SmartBin?>? Collections { get; set; }

    public List<MulticamGroup?>? Multicams { get; set; }
}
