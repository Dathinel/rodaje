using System.Text;
using System.Text.Json;
using Rodaje.Core.Collab;

namespace Rodaje.App.Services.Collab;

internal sealed record JournalEntry(string Kind, Patch Patch, long Seq = 0, string Reason = "");
internal sealed record JournalState(long Seq, string Snapshot, long? Separation, IReadOnlyList<string> Accepted, IReadOnlyList<JournalEntry> Entries,
    IReadOnlyList<string>? RecoveryHosts = null);

/// <summary>Instantánea JSONL acotada. Nunca se modifica el archivo publicado hasta cerrar y vaciar el temporal.</summary>
internal sealed class CollabJournal(string folder)
{
    internal const int MaxBytes = 80 * 1024 * 1024;
    internal const int MaxEntries = 1024;
    private readonly string _path = Path.Combine(folder, "registro.jsonl");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public JournalState? Load(Action warn)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            using FileStream file = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxBytes) throw new InvalidDataException();
            // La lectura también está acotada si otro proceso cambia el archivo entre Length y Read.
            using MemoryStream buffer = new();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = file.Read(chunk)) > 0)
            {
                if (buffer.Length + read > MaxBytes) throw new InvalidDataException();
                buffer.Write(chunk, 0, read);
            }
            string[] lines = Utf8.GetString(buffer.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length is < 1 or > MaxEntries + 1) throw new InvalidDataException();
            using JsonDocument header = Parse(lines[0]);
            JsonElement h = header.RootElement;
            if (h.GetProperty("format").GetInt32() != 1) throw new InvalidDataException();
            long seq = h.GetProperty("seq").GetInt64();
            long? separation = h.GetProperty("separation").ValueKind == JsonValueKind.Null ? null : h.GetProperty("separation").GetInt64();
            string snapshot = h.GetProperty("snapshot").GetRawText();
            if (seq < 0 || separation < 0 || separation > seq || !ProjectWire.TryParse(snapshot).IsValid) throw new InvalidDataException();
            List<string> accepted = [];
            foreach (JsonElement id in h.GetProperty("accepted").EnumerateArray())
            {
                if (accepted.Count >= 8192 || id.GetString() is not { } value || !Guid.TryParse(value, out _)) throw new InvalidDataException();
                accepted.Add(value);
            }
            List<JournalEntry> entries = [];
            List<string> recoveryHosts = [];
            if (h.TryGetProperty("recoveryHosts", out JsonElement hosts))
                foreach (JsonElement host in hosts.EnumerateArray())
                {
                    if (recoveryHosts.Count >= 16 || host.GetString() is not { } key || !IsMemberKey(key)) throw new InvalidDataException();
                    recoveryHosts.Add(key);
                }
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string line in lines.Skip(1))
            {
                if (Encoding.UTF8.GetByteCount(line) > WireLimits.Default.MaxPatchBytes + 8192) throw new InvalidDataException();
                using JsonDocument document = Parse(line);
                JsonElement root = document.RootElement;
                string kind = root.GetProperty("kind").GetString() ?? "";
                Patch? patch = PatchWire.TryParse(root.GetProperty("patch").GetRawText()).Patch;
                long order = root.GetProperty("seq").GetInt64();
                string reason = root.GetProperty("reason").GetString() ?? "";
                if (kind is not ("pending" or "unsettled" or "branch" or "separated") || patch is null || !Guid.TryParse(patch.Id, out _)
                    || !seen.Add(kind + patch.Id) || order < 0 || order > seq || reason.Length > 4096) throw new InvalidDataException();
                // El texto puede venir de un archivo manipulado: se descartan rutas, claves y controles.
                bool safeReason = reason.Length > 0 && !reason.Any(char.IsControl) && !reason.Contains(':', StringComparison.Ordinal)
                    && !reason.Contains('\\', StringComparison.Ordinal) && !reason.Contains('/', StringComparison.Ordinal)
                    && !reason.Split(' ').Any(word => word.Length >= 64 && word.All(Uri.IsHexDigit));
                if (kind == "separated" && !IsMemberKey(patch.Author)) throw new InvalidDataException();
                entries.Add(new JournalEntry(kind, kind == "separated" ? patch : patch with { Author = "propio" }, order,
                    kind == "unsettled" ? safeReason ? reason : "Este cambio no entró en la sala. Puedes conservarlo como un cambio nuevo o descartarlo." : ""));
            }
            return new JournalState(seq, snapshot, separation, accepted, entries, recoveryHosts);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            warn();
            return null;
        }
    }

    public bool Save(JournalState state)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (state.Entries.Count > MaxEntries) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (Utf8JsonWriter writer = new(file))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("format", 1);
                    writer.WriteNumber("seq", state.Seq);
                    if (state.Separation is { } separation) writer.WriteNumber("separation", separation);
                    else writer.WriteNull("separation");
                    writer.WritePropertyName("snapshot");
                    writer.WriteRawValue(state.Snapshot);
                    writer.WriteStartArray("accepted");
                    foreach (string id in state.Accepted) writer.WriteStringValue(id);
                    writer.WriteEndArray();
                    writer.WriteStartArray("recoveryHosts");
                    foreach (string key in state.RecoveryHosts ?? []) writer.WriteStringValue(key);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                file.WriteByte((byte)'\n');
                foreach (JournalEntry entry in state.Entries)
                {
                    using (Utf8JsonWriter writer = new(file))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("kind", entry.Kind);
                        writer.WriteNumber("seq", entry.Seq);
                        writer.WriteString("reason", entry.Reason);
                        writer.WritePropertyName("patch");
                        writer.WriteRawValue(PatchWire.ToJson(entry.Kind == "separated" ? entry.Patch : entry.Patch with { Author = "propio" }));
                        writer.WriteEndObject();
                    }
                    file.WriteByte((byte)'\n');
                    if (file.Position > MaxBytes) return false;
                }
                if (file.Position > MaxBytes) return false;
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json,
        new JsonDocumentOptions { MaxDepth = WireLimits.Default.MaxDepth + 4, AllowDuplicateProperties = false });

    internal static bool IsMemberKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit);
}
