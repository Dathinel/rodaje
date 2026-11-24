using System.Text;
using System.Text.Json;

namespace Rodaje.Core.Collab;

/// <summary>Resultado de leer un parche de la red.</summary>
public sealed record PatchParseResult(Patch? Patch, PatchRejection? Rejection)
{
    public bool IsValid => Patch is not null;
}

/// <summary>Un parche como texto JSON de red (<c>docs/propuestas/colaboracion-api-parches.md</c>).</summary>
public static class PatchWire
{
    /// <summary>El JSON del parche, compacto y con claves en orden fijo.</summary>
    public static string ToJson(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", patch.Id);
            writer.WriteNumber("base", patch.BaseOrder);
            writer.WriteString("author", patch.Author);
            writer.WriteString("desc", patch.Description);
            writer.WriteStartArray("ops");
            foreach (PatchOp op in patch.Ops)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", op.Key.WireKind);
                writer.WriteString("id", op.Id);
                if (op.Parent is null)
                {
                    writer.WriteNull("parent");
                }
                else
                {
                    writer.WriteString("parent", op.Parent);
                }

                writer.WriteNumber("v", op.BaseVersion);
                if (op.Content is null)
                {
                    writer.WriteNull("content");
                }
                else
                {
                    writer.WritePropertyName("content");
                    writer.WriteRawValue(op.Content);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("orders");
            foreach (PatchOrder order in patch.Orders)
            {
                writer.WriteStartObject();
                writer.WriteString("container", order.Container);
                writer.WriteNumber("v", order.BaseVersion);
                writer.WriteStartArray("ids");
                foreach (string id in order.Ids)
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("blobs");
            foreach (PatchBlob blob in patch.Blobs)
            {
                writer.WriteStartObject();
                writer.WriteString("sha256", blob.Sha256);
                writer.WriteNumber("size", blob.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>
    /// Las huellas de los blobs (LUT, máscaras) que el contenido del parche referencia, sin repetir y en orden. Quien recibe el parche
    /// pide los que no tenga; <see cref="Patch.Blobs"/> viene vacío porque Core no conoce los tamaños.
    /// </summary>
    public static IReadOnlyList<string> BlobHashes(Patch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (PatchOp op in patch.Ops)
        {
            if (op.Content is null)
            {
                continue;
            }

            int at = 0;
            while ((at = op.Content.IndexOf(ElementCodec.BlobPrefix, at, StringComparison.Ordinal)) >= 0)
            {
                at += ElementCodec.BlobPrefix.Length;
                if (at + ContentHash.HexLength <= op.Content.Length && ContentHash.IsValid(op.Content.Substring(at, ContentHash.HexLength)))
                {
                    found.Add(op.Content.Substring(at, ContentHash.HexLength));
                }
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Lee un parche que llegó de otro equipo: pasa antes por <see cref="WireSanitizer"/> y nunca lanza. Un texto hostil o mal
    /// formado devuelve el rechazo, no una excepción.
    /// </summary>
    /// <exception cref="OperationCanceledException">Si <paramref name="cancellationToken"/> se cancela recorriéndolo.</exception>
    public static PatchParseResult TryParse(string json, WireLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= WireLimits.Default;
        if (WireSanitizer.Check(json, limits) is { } rejection)
        {
            return new PatchParseResult(null, rejection);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = limits.MaxDepth, AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            var ops = new List<PatchOp>();
            foreach (JsonElement op in root.GetProperty("ops").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ElementKinds.TryParse(op.GetProperty("kind").GetString(), out ElementKind kind))
                {
                    return new PatchParseResult(null, WireSanitizer.Malformed("Un cambio del parche tiene una clase que no es válida."));
                }

                string? parent = op.TryGetProperty("parent", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                JsonElement content = op.GetProperty("content");
                ops.Add(new PatchOp(kind, op.GetProperty("id").GetString()!, parent, op.GetProperty("v").GetInt32(),
                    content.ValueKind == JsonValueKind.Null ? null : content.GetRawText()));
            }

            var orders = new List<PatchOrder>();
            if (root.TryGetProperty("orders", out JsonElement orderList))
            {
                foreach (JsonElement order in orderList.EnumerateArray())
                {
                    orders.Add(new PatchOrder(order.GetProperty("container").GetString()!, [.. order.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!)], order.GetProperty("v").GetInt32()));
                }
            }

            var blobs = new List<PatchBlob>();
            if (root.TryGetProperty("blobs", out JsonElement blobList))
            {
                foreach (JsonElement blob in blobList.EnumerateArray())
                {
                    blobs.Add(new PatchBlob(blob.GetProperty("sha256").GetString()!, blob.GetProperty("size").GetInt64()));
                }
            }

            var patch = new Patch(
                root.GetProperty("id").GetString()!,
                root.GetProperty("base").GetInt64(),
                root.GetProperty("author").GetString()!,
                root.GetProperty("desc").GetString()!,
                ops,
                orders,
                blobs);
            return new PatchParseResult(patch, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // El saneador ya lo comprobó: llegar aquí es un defecto, pero un parche ajeno nunca debe tumbar nada.
            return new PatchParseResult(null, WireSanitizer.Malformed("El parche no tiene el formato esperado."));
        }
    }
}
