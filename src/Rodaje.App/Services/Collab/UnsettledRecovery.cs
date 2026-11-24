using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Collab;

internal static class UnsettledRecovery
{
    public static Patch? Create(Patch original, Project live, ElementVersions versions, PatchHeader header, IReadOnlyDictionary<string, string> reservations)
    {
        SharedState current = SharedState.Capture(live);
        Dictionary<string, string> ids = new(StringComparer.Ordinal);
        foreach (PatchOp op in original.Ops.Where(o => !o.IsDelete))
            ids.TryAdd(op.Id, Guid.NewGuid().ToString("N"));
        // Los ids de efectos, transiciones, buses y sus referencias también tienen que ser nuevos.
        foreach (PatchOp op in original.Ops.Where(o => !o.IsDelete))
            CollectIds(JsonNode.Parse(op.Content!), ids);

        Project candidate = live.Clone();
        List<PatchOp> addedTracks = [];
        foreach (PatchOp op in original.Ops.Where(o => o.Kind == ElementKind.Clip && !o.IsDelete))
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
            if (op.IsDelete)
            {
                // El borrado ya cumplido no necesita una propuesta ni una versión nueva.
                if (current.Elements.TryGetValue(op.Key, out ElementState? existing))
                    ops.Add(op with { Parent = existing.Parent, BaseVersion = versions.Get(op.Key) });
                continue;
            }
            string id = ids[op.Id];
            // La transcripción tiene por identificador el medio al que pertenece.
            if (op.Kind == ElementKind.Transcript && !original.Ops.Any(o => o.Kind == ElementKind.Media && o.Id == op.Id)) return null;
            JsonNode? content = JsonNode.Parse(op.Content!);
            Rewrite(content, ids);
            string? parent = op.Parent is { } p ? ids.GetValueOrDefault(p, p) : null;
            ops.Add(op with { Id = id, Parent = parent, BaseVersion = versions.Get(op.Kind, id), Content = content!.ToJsonString() });
        }
        Patch patch = new(header.Id, header.BaseOrder, header.Author, header.Description, ops, [], original.Blobs);
        candidate = live.Clone();
        if (!PatchApply.TryApply(candidate, versions.Clone(), patch).IsAccepted) return null;
        SharedState afterOps = SharedState.Capture(candidate);
        List<PatchOrder> orders = [];
        foreach (PatchOrder order in original.Orders)
        {
            int colon = order.Container.IndexOf(':', StringComparison.Ordinal);
            string container = colon < 0 ? order.Container
                : order.Container[..(colon + 1)] + ids.GetValueOrDefault(order.Container[(colon + 1)..], order.Container[(colon + 1)..]);
            if (!afterOps.Orders.TryGetValue(container, out IReadOnlyList<string>? present)) continue;
            string[] wanted = [.. order.Ids.Select(id => ids.GetValueOrDefault(id, id))];
            IReadOnlyList<string> merged = RecoverOrder(wanted, present);
            if (!present.SequenceEqual(merged)) orders.Add(new PatchOrder(container, merged, versions.GetOrder(container)));
        }
        patch = patch with { Orders = orders };
        if (PatchReservations.Required(patch, live).Any(key => reservations.TryGetValue(key, out string? owner) && owner != header.Author)) return null;
        return PatchApply.TryApply(live.Clone(), versions.Clone(), patch).IsAccepted ? patch : null;
    }

    // Cada grupo de ids desconocidos sigue a su predecesor conocido actual; el grupo inicial sigue al principio.
    private static List<string> RecoverOrder(IReadOnlyList<string> saved, IReadOnlyList<string> current)
    {
        HashSet<string> present = new(current, StringComparer.Ordinal);
        HashSet<string> known = new(saved, StringComparer.Ordinal);
        Dictionary<string, List<string>> followers = new(StringComparer.Ordinal);
        List<string> result = [];
        List<string> group = result;
        foreach (string id in current)
        {
            if (known.Contains(id)) followers[id] = group = [];
            else group.Add(id);
        }
        foreach (string id in saved.Where(present.Contains).Distinct(StringComparer.Ordinal))
        {
            result.Add(id);
            result.AddRange(followers[id]);
        }
        return result;
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
