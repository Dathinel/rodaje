using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Collab;

/// <summary>Límites y puentes de quien aplica un parche.</summary>
public sealed record PatchApplyOptions
{
    /// <summary>Límites de la forma del parche; <see langword="null"/> = <see cref="WireLimits.Default"/>.</summary>
    public WireLimits? Limits { get; init; }

    /// <summary>Traduce las referencias <c>blob:</c> del parche a archivos de este equipo; sin él, quedan vacías.</summary>
    public IBlobMap? Blobs { get; init; }
}

/// <summary>Aplica parches a un proyecto con todas las comprobaciones.</summary>
public static class PatchApply
{
    /// <summary>
    /// Aplica <paramref name="patch"/> sobre una copia candidata: forma, versiones, padres y <c>ModelValidator.Validate</c> del
    /// proyecto entero. Si todo pasa, <paramref name="project"/> cambia (los objetos que el parche no toca siguen siendo los
    /// mismos) y <paramref name="versions"/> avanza; si no, no cambia nada y el resultado dice por qué. Nunca lanza por un parche
    /// mal formado o hostil. Aplicar los mismos parches en el mismo orden deja siempre el mismo <see cref="SharedState.Hash"/>.
    /// </summary>
    public static PatchResult TryApply(Project project, ElementVersions versions, Patch patch, PatchApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(patch);
        options ??= new PatchApplyOptions();

        if (CheckShape(patch, options) is { } malformed)
        {
            return new PatchResult(malformed);
        }

        foreach (PatchOp op in patch.Ops)
        {
            int current = versions.Get(op.Key);
            if (op.BaseVersion != current)
            {
                return PatchResult.Reject(
                    PatchRejectionReason.VersionConflict,
                    "Otra persona cambió este elemento antes que tú. Tu cambio no entró; vuelve a hacerlo.",
                    op.Key);
            }
        }

        foreach (PatchOrder order in patch.Orders)
        {
            if (order.BaseVersion != versions.GetOrder(order.Container))
            {
                return PatchResult.Reject(
                    PatchRejectionReason.VersionConflict,
                    "Otra persona cambió el orden de esa lista antes que tú. Tu cambio no entró; vuelve a hacerlo.");
            }
        }

        PatchOp[] ordered = [.. patch.Ops.Order(Comparer<PatchOp>.Create(PatchCanon.Compare))];
        PatchRejection? rejected = DecodeAll(ordered, options.Blobs, out Decoded[] decoded);
        if (rejected is not null)
        {
            return new PatchResult(rejected);
        }

        // Un segundo juego de objetos para el proyecto vivo: los del primero cuelgan de la copia candidata, y el contenido (y lo que
        // resuelva el mapa de blobs) se lee una sola vez, así que lo validado es exactamente lo que se aplica.
        Decoded[] forLive;
        try
        {
            forLive = [.. decoded.Select(d => d.Copy())];
            Project candidate = project.Clone();
            if (Apply(candidate, ordered, decoded, patch.Orders) is { } failed)
            {
                return new PatchResult(failed);
            }

            if (ProjectSerializer.FindNullItems(candidate).Count > 0)
            {
                return WireSanitizer.Reject("El cambio trae un elemento vacío (null) dentro de una lista y no se aplicó.");
            }

            IReadOnlyList<string> problems = ModelValidator.Validate(candidate);
            if (problems.Count > 0)
            {
                // Sin el texto del problema: puede traer un valor de este equipo, como la ruta de un archivo.
                return PatchResult.Reject(
                    PatchRejectionReason.Invalid,
                    $"El cambio dejaría el proyecto con {problems.Count} dato(s) inconsistente(s) y no se aplicó.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Datos hostiles que pasan el formato pero rompen una operación (un null dentro de una lista, por ejemplo).
            return WireSanitizer.Reject("El cambio trae datos que no se pudieron comprobar y no se aplicó.");
        }

        // La copia candidata salió bien: el mismo cambio sobre el proyecto vivo no puede fallar (es el mismo algoritmo sobre lo mismo).
        Apply(project, ordered, forLive, patch.Orders);
        versions.Advance(patch);
        return PatchResult.Accepted;
    }

    private static PatchRejection? DecodeAll(PatchOp[] ordered, IBlobMap? blobs, out Decoded[] decoded)
    {
        decoded = [.. ordered.Select(_ => new Decoded())];
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Content is { } content)
            {
                (decoded[i], PatchRejection? rejection) = Decode(ordered[i], content, blobs);
                if (rejection is not null)
                {
                    return rejection;
                }
            }
        }

        return null;
    }

    private sealed record Decoded(MediaRef? Media = null, MediaTranscript? Transcript = null, Sequence? Sequence = null, Track? Track = null, Clip? Clip = null, Marker? Marker = null, Region? Region = null)
    {
        public Decoded Copy() => new(Media?.Clone(), Transcript?.Clone(), Sequence?.Clone(), Track?.Clone(), Clip?.Clone(), Marker?.Clone(), Region?.Clone());
    }

    private static PatchRejection? CheckShape(Patch patch, PatchApplyOptions options)
    {
        WireLimits limits = options.Limits ?? WireLimits.Default;
        if (patch.Ops is null || patch.Orders is null || patch.Blobs is null || patch.Id is null || patch.Author is null || patch.Description is null)
        {
            return WireSanitizer.Malformed("El parche está incompleto.");
        }

        // Lo que llega por la red ya pasó por PatchWire.TryParse; aquí se comprueba igual por si el parche se armó a mano.
        try
        {
            return WireSanitizer.Check(PatchWire.ToJson(patch), limits);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return WireSanitizer.Malformed("El parche no se puede escribir como JSON válido.");
        }
    }

    private static (Decoded, PatchRejection?) Decode(PatchOp op, string content, IBlobMap? blobs)
    {
        string? error;
        Decoded result;
        switch (op.Kind)
        {
            case ElementKind.Media:
                result = new Decoded(Media: ElementCodec.DecodeMedia(content, blobs, out error));
                break;
            case ElementKind.Transcript:
                result = new Decoded(Transcript: ElementCodec.DecodeTranscript(content, blobs, out error));
                break;
            case ElementKind.Sequence:
                result = new Decoded(Sequence: ElementCodec.DecodeSequence(content, blobs, out error));
                break;
            case ElementKind.Track:
                result = new Decoded(Track: ElementCodec.DecodeTrack(content, blobs, out error));
                break;
            case ElementKind.Clip:
                result = new Decoded(Clip: ElementCodec.DecodeClip(content, blobs, out error));
                break;
            case ElementKind.Marker:
                result = new Decoded(Marker: ElementCodec.DecodeMarker(content, out error));
                break;
            default:
                result = new Decoded(Region: ElementCodec.DecodeRegion(content, out error));
                break;
        }

        if (error is not null)
        {
            return (result, new PatchRejection(
                error.StartsWith('!') ? PatchRejectionReason.Hostile : PatchRejectionReason.Malformed,
                error.TrimStart('!'),
                op.Key));
        }

        string? innerId = result.Media?.Id ?? result.Transcript?.MediaId ?? result.Sequence?.Id ?? result.Track?.Id ?? result.Clip?.Id ?? result.Marker?.Id ?? result.Region?.Id;
        if (innerId != op.Id)
        {
            return (result, WireSanitizer.Malformed("El contenido de un cambio no lleva el mismo identificador que el cambio.", op.Key));
        }

        // Los clips de una pista no llevan clips; las pistas y las secuencias de red llegan vacías de lo que cuelga de ellas.
        if ((result.Track is { Clips.Count: > 0 }) || (result.Sequence is { VideoTracks.Count: > 0 } or { AudioTracks.Count: > 0 } or { Markers.Count: > 0 } or { Regions.Count: > 0 }))
        {
            return (result, WireSanitizer.Malformed("Una pista o una secuencia del parche trae dentro lo que debería viajar aparte.", op.Key));
        }

        return (result, null);
    }

    private static PatchRejection? Apply(Project project, PatchOp[] ops, Decoded[] decoded, IReadOnlyList<PatchOrder> orders)
    {
        var touched = new HashSet<ElementKey>(ops.Select(o => o.Key));
        Dictionary<string, int>? mediaIndex = null;
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i].Kind == ElementKind.Media && ops[i].Content is not null)
            {
                mediaIndex ??= IndexById(project.Media);
            }

            PatchRejection? rejection = ops[i].Content is null
                ? Delete(project, ops[i], touched)
                : Upsert(project, ops[i], decoded[i], mediaIndex);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        foreach (PatchOrder order in orders)
        {
            Reorder(project, order);
        }

        return null;
    }

    // Id → sitio en la lista: dar de alta muchos medios sin él sale cuadrático (buscar cada id recorriéndola entera).
    private static Dictionary<string, int> IndexById(List<MediaRef> media)
    {
        var index = new Dictionary<string, int>(media.Count, StringComparer.Ordinal);
        for (int i = 0; i < media.Count; i++)
        {
            index.TryAdd(media[i].Id, i);
        }

        return index;
    }

    private static PatchRejection Occupied(PatchOp op) =>
        new(PatchRejectionReason.VersionConflict, "Otra persona añadió algo a lo que querías borrar. Tu cambio no entró.", op.Key);

    private static PatchRejection Gone(PatchOp op) =>
        new(PatchRejectionReason.VersionConflict, "Otra persona quitó este elemento antes que tú. Tu cambio no entró.", op.Key);

    private static PatchRejection? Delete(Project project, PatchOp op, HashSet<ElementKey> touched)
    {
        bool removed = false;
        switch (op.Kind)
        {
            case ElementKind.Clip:
                removed = project.Sequences.Any(s => s.AllTracks.Any(t => t.Clips.RemoveAll(c => c.Id == op.Id) > 0));
                break;
            case ElementKind.Marker:
                removed = project.Sequences.Any(s => s.Markers.RemoveAll(m => m.Id == op.Id) > 0);
                break;
            case ElementKind.Region:
                removed = project.Sequences.Any(s => s.Regions.RemoveAll(r => r.Id == op.Id) > 0);
                break;
            case ElementKind.Track:
                // Lo que cuelga de ella se borra o se mueve con su propio cambio y su propia versión: una pista con clips de otra persona no se borra.
                if (project.Sequences.SelectMany(s => s.AllTracks).Any(t => t.Id == op.Id && t.Clips.Exists(c => !touched.Contains(new ElementKey(ElementKind.Clip, c.Id)))))
                {
                    return Occupied(op);
                }

                removed = project.Sequences.Any(s => s.VideoTracks.RemoveAll(t => t.Id == op.Id) + s.AudioTracks.RemoveAll(t => t.Id == op.Id) > 0);
                break;
            case ElementKind.Sequence:
                if (project.Sequences.Any(s => s.Id == op.Id && (s.AllTracks.Any(t => !touched.Contains(new ElementKey(ElementKind.Track, t.Id)))
                    || s.Markers.Exists(m => !touched.Contains(new ElementKey(ElementKind.Marker, m.Id)))
                    || s.Regions.Exists(r => !touched.Contains(new ElementKey(ElementKind.Region, r.Id))))))
                {
                    return Occupied(op);
                }

                removed = project.Sequences.RemoveAll(s => s.Id == op.Id) > 0;
                break;
            case ElementKind.Transcript:
                removed = project.Transcripts.RemoveAll(t => t.MediaId == op.Id) > 0;
                break;
            case ElementKind.Media:
                removed = project.Media.RemoveAll(m => m.Id == op.Id) > 0;
                break;
        }

        return removed ? null : Gone(op);
    }

    private static PatchRejection Unknown(PatchOp op) =>
        new(PatchRejectionReason.UnknownParent, "El cambio cuelga de una pista o una secuencia que ya no existe.", op.Key);

    private static PatchRejection Clash(PatchOp op) =>
        new(PatchRejectionReason.Invalid, "El cambio mueve un elemento a un sitio donde no puede estar.", op.Key);

    private static PatchRejection? Upsert(Project project, PatchOp op, Decoded decoded, Dictionary<string, int>? mediaIndex)
    {
        switch (op.Kind)
        {
            case ElementKind.Media:
            {
                MediaRef media = decoded.Media!;
                int at = mediaIndex is null ? project.Media.FindIndex(m => m.Id == op.Id) : mediaIndex.GetValueOrDefault(op.Id, -1);
                if (at < 0)
                {
                    mediaIndex?.Add(op.Id, project.Media.Count);
                    project.Media.Add(media);
                }
                else
                {
                    media.Path = project.Media[at].Path;
                    project.Media[at] = media;
                }

                return null;
            }

            case ElementKind.Transcript:
            {
                int at = project.Transcripts.FindIndex(t => t.MediaId == op.Id);
                if (at < 0)
                {
                    project.Transcripts.Add(decoded.Transcript!);
                }
                else
                {
                    project.Transcripts[at] = decoded.Transcript!;
                }

                return null;
            }

            case ElementKind.Sequence:
            {
                Sequence? existing = project.FindSequence(op.Id);
                if (existing is null)
                {
                    project.Sequences.Add(decoded.Sequence!);
                }
                else
                {
                    existing.CopyHeaderFrom(decoded.Sequence!);
                }

                return null;
            }

            case ElementKind.Track:
                return UpsertTrack(project, op, decoded.Track!);
            case ElementKind.Marker:
                return UpsertListItem(project, op, s => s.Markers, decoded.Marker!, m => m.Id);
            case ElementKind.Region:
                return UpsertListItem(project, op, s => s.Regions, decoded.Region!, r => r.Id);
            default:
                return UpsertClip(project, op, decoded.Clip!);
        }
    }

    private static PatchRejection? UpsertTrack(Project project, PatchOp op, Track track)
    {
        Sequence? sequence = project.FindSequence(op.Parent!);
        if (sequence is null)
        {
            return Unknown(op);
        }

        Sequence? owner = project.Sequences.FirstOrDefault(s => s.FindTrack(op.Id) is not null);
        if (owner is null)
        {
            (track.Kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks).Add(track);
            return null;
        }

        Track existing = owner.GetTrack(op.Id);
        if (owner != sequence || existing.Kind != track.Kind)
        {
            return Clash(op);
        }

        existing.CopyHeaderFrom(track);
        return null;
    }

    private static PatchRejection? UpsertListItem<T>(Project project, PatchOp op, Func<Sequence, List<T>> list, T item, Func<T, string> id)
    {
        Sequence? sequence = project.FindSequence(op.Parent!);
        if (sequence is null)
        {
            return Unknown(op);
        }

        foreach (Sequence other in project.Sequences)
        {
            int at = list(other).FindIndex(x => id(x) == op.Id);
            if (at < 0)
            {
                continue;
            }

            if (other != sequence)
            {
                return Clash(op);
            }

            list(other)[at] = item;
            return null;
        }

        list(sequence).Add(item);
        return null;
    }

    private static PatchRejection? UpsertClip(Project project, PatchOp op, Clip clip)
    {
        Track? parent = project.Sequences.SelectMany(s => s.AllTracks).FirstOrDefault(t => t.Id == op.Parent);
        if (parent is null)
        {
            return Unknown(op);
        }

        foreach (Track track in project.Sequences.SelectMany(s => s.AllTracks))
        {
            track.Clips.RemoveAll(c => c.Id == op.Id);
        }

        int index = parent.Clips.FindIndex(c => c.Start > clip.Start);
        parent.Clips.Insert(index < 0 ? parent.Clips.Count : index, clip);
        return null;
    }

    private static void Reorder(Project project, PatchOrder order)
    {
        switch (order.Container)
        {
            case PatchContainers.Media:
                Sort(project.Media, order.Ids, m => m.Id);
                return;
            case PatchContainers.Sequences:
                Sort(project.Sequences, order.Ids, s => s.Id);
                return;
            case PatchContainers.Transcripts:
                Sort(project.Transcripts, order.Ids, t => t.MediaId);
                return;
        }

        int colon = order.Container.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || project.FindSequence(order.Container[(colon + 1)..]) is not { } sequence)
        {
            return;
        }

        switch (order.Container[..colon])
        {
            case "vtracks": Sort(sequence.VideoTracks, order.Ids, t => t.Id); break;
            case "atracks": Sort(sequence.AudioTracks, order.Ids, t => t.Id); break;
            case "markers": Sort(sequence.Markers, order.Ids, m => m.Id); break;
            case "regions": Sort(sequence.Regions, order.Ids, r => r.Id); break;
        }
    }

    // Los que nombra la lista, en ese orden; los demás, detrás y en su orden de antes.
    private static void Sort<T>(List<T> items, IReadOnlyList<string> ids, Func<T, string> id)
    {
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T item in items)
        {
            byId.TryAdd(id(item), item);
        }

        var result = new List<T>(items.Count);
        var used = new HashSet<T>(ReferenceEqualityComparer.Instance as IEqualityComparer<T> ?? EqualityComparer<T>.Default);
        foreach (string wanted in ids)
        {
            if (byId.TryGetValue(wanted, out T? item) && used.Add(item))
            {
                result.Add(item);
            }
        }

        foreach (T item in items)
        {
            if (used.Add(item))
            {
                result.Add(item);
            }
        }

        items.Clear();
        items.AddRange(result);
    }
}
