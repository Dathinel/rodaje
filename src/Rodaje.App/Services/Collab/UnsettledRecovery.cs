using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

internal static class UnsettledRecovery
{
    public static Patch? Create(Patch original, Project live, ElementVersions versions, PatchHeader header, IReadOnlyDictionary<string, string> reservations)
    {
        // TODO B15a: recuperar borrados y cambios que solo reordenan sin inventar una copia de contenido inexistente.
        // Se conservan íntegros en la lista hasta que se rehagan manualmente o se descarten.
        if (original.Ops.Any(o => o.IsDelete) || original.Ops.Count == 0) return null;
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach (PatchOp op in original.Ops)
            ids.TryAdd(op.Id, Guid.NewGuid().ToString("N"));
        // Los ids de efectos, transiciones, buses y sus referencias también tienen que ser nuevos.
        foreach (PatchOp op in original.Ops)
            CollectIds(JsonNode.Parse(op.Content!), ids);

        Project candidate = live.Clone();
        List<PatchOp> addedTracks = [];
        foreach (PatchOp op in original.Ops.Where(o => o.Kind == ElementKind.Clip))
        {
            if (op.Parent is null || ids.ContainsKey(op.Parent)) continue;
            Sequence? sequence = candidate.Sequences.FirstOrDefault(s => s.VideoTracks.Concat(s.AudioTracks).Any(t => t.Id == op.Parent));
            if (sequence is null) return null;
            Track? track = sequence.VideoTracks.Concat(sequence.AudioTracks).FirstOrDefault(t => t.Id == op.Parent);
            if (track is null) return null;
            if (track.Clips.Count == 0 && !track.Locked && !reservations.ContainsKey("pista:" + track.Id)) continue;
            Track copy = new() { Id = Guid.NewGuid().ToString("N"), Kind = track.Kind, Name = "Cambios recuperados" };
            ids.Add(track.Id, copy.Id);
            (copy.Kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks).Add(copy);
            ElementState state = SharedState.Capture(candidate).Elements[new ElementKey(ElementKind.Track, copy.Id)];
            addedTracks.Add(new PatchOp(ElementKind.Track, copy.Id, sequence.Id, 0, state.Content));
        }

        List<PatchOp> ops = [.. addedTracks];
        foreach (PatchOp op in original.Ops)
        {
            string id = ids[op.Id];
            // La transcripción tiene por identificador el medio al que pertenece.
            if (op.Kind == ElementKind.Transcript && !original.Ops.Any(o => o.Kind == ElementKind.Media && o.Id == op.Id)) return null;
            JsonNode? content = JsonNode.Parse(op.Content!);
            Rewrite(content, ids);
            string? parent = op.Parent is { } p ? ids.GetValueOrDefault(p, p) : null;
            ops.Add(op with { Id = id, Parent = parent, BaseVersion = versions.Get(op.Kind, id), Content = content!.ToJsonString() });
        }
        // No se reaplican órdenes antiguas: las copias se añaden al final de su contenedor.
        Patch patch = new(header.Id, header.BaseOrder, header.Author, header.Description, ops, [], original.Blobs);
        return PatchApply.TryApply(live.Clone(), versions.Clone(), patch).IsAccepted ? patch : null;
    }

    private static void CollectIds(JsonNode? node, Dictionary<string, string> ids)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                if (key == "id" && value is JsonValue v && v.TryGetValue(out string? id) && !string.IsNullOrEmpty(id))
                    ids.TryAdd(id, Guid.NewGuid().ToString("N"));
                CollectIds(value, ids);
            }
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) CollectIds(child, ids);
    }

    private static void Rewrite(JsonNode? node, Dictionary<string, string> ids)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj.ToArray())
            {
                if (key is "id" or "mediaId" or "sequenceId" or "from" or "to" or "output" or "bus" or "linkId" or "groupId"
                    && value is JsonValue v && v.TryGetValue(out string? id) && id is not null && ids.TryGetValue(id, out string? replacement)) obj[key] = replacement;
                else Rewrite(value, ids);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue v && v.TryGetValue(out string? id) && id is not null && ids.TryGetValue(id, out string? replacement)) array[i] = replacement;
                else Rewrite(array[i], ids);
            }
        }
    }
}
