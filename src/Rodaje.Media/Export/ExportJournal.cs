using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Rodaje.Media.Export;

/// <summary>
/// <c>exportando.json</c>: los temporales de las exportaciones en marcha, con el proceso que las hace. Si Rodaje se cierra
/// de golpe (o se va la luz), al reabrir <see cref="CleanupStale"/> borra los de procesos que ya no existen.
/// </summary>
/// <remarks>Solo se borran rutas con <see cref="ExportOutputGuard.TemporaryMarker"/>; un registro manipulado no puede borrar otra cosa.</remarks>
public sealed class ExportJournal
{
    public const string FileName = "exportando.json";

    private static readonly Lock Gate = new();

    public ExportJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = System.IO.Path.GetFullPath(directory);
        Path = System.IO.Path.Combine(Directory, FileName);
    }

    /// <summary><c>%LOCALAPPDATA%\Rodaje\exportando.json</c>.</summary>
    public static ExportJournal Default { get; } =
        new(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje"));

    public string Directory { get; }

    public string Path { get; }

    /// <summary>Anota (o reemplaza) los temporales de una exportación de este proceso.</summary>
    public void Register(string id, string outputPath, IEnumerable<string> temporaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var current = Process.GetCurrentProcess();
        var entry = new Entry(id, Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks, outputPath, [.. temporaries]);
        Update(entries =>
        {
            entries.RemoveAll(e => e.Id == id);
            entries.Add(entry);
        });
    }

    public void Remove(string id) => Update(entries => entries.RemoveAll(e => e.Id == id));

    /// <summary>Exportaciones anotadas (de este proceso y de otros).</summary>
    public IReadOnlyList<(string Id, int ProcessId, string OutputPath, IReadOnlyList<string> Temporaries)> Entries()
    {
        lock (Gate)
        {
            return [.. Read().Select(e => (e.Id, e.ProcessId, e.OutputPath, (IReadOnlyList<string>)e.Temporaries))];
        }
    }

    /// <summary>
    /// Borra los temporales de exportaciones cuyo proceso ya no vive y las quita del registro. Si algo no se pudo borrar
    /// (un antivirus o el <c>ffmpeg.exe</c> recién muerto aún lo tienen abierto), la entrada se queda para el próximo arranque.
    /// </summary>
    /// <returns>Rutas que se borraron.</returns>
    public IReadOnlyList<string> CleanupStale()
    {
        var deleted = new List<string>();
        Update(entries =>
        {
            foreach (Entry entry in entries.Where(e => !IsAlive(e)).ToList())
            {
                bool left = false;
                foreach (string path in entry.Temporaries.Where(p => p.Contains(ExportOutputGuard.TemporaryMarker, StringComparison.OrdinalIgnoreCase)))
                {
                    bool existed = File.Exists(path) || System.IO.Directory.Exists(path);
                    bool stays = ExportOutputGuard.DeleteTemporary(path);
                    left |= stays;
                    if (!stays && existed)
                    {
                        deleted.Add(path);
                    }
                }

                if (!left)
                {
                    entries.Remove(entry);
                }
            }
        });
        return deleted;
    }

    private static bool IsAlive(Entry entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == entry.ProcessStartTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void Update(Action<List<Entry>> change)
    {
        lock (Gate)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    using var lockFile = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    List<Entry> entries = Read();
                    change(entries);
                    Write(entries);
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }

    private List<Entry> Read()
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var entries = new List<Entry>();
            foreach (JsonElement item in document.RootElement.GetProperty("exportaciones").EnumerateArray())
            {
                entries.Add(new Entry(
                    item.GetProperty("id").GetString()!,
                    item.GetProperty("proceso").GetInt32(),
                    item.GetProperty("inicioProceso").GetInt64(),
                    item.GetProperty("salida").GetString() ?? "",
                    [.. item.GetProperty("temporales").EnumerateArray().Select(t => t.GetString()!).Where(t => t is not null)]));
            }

            return entries;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Un registro roto (apagón a mitad de escribirlo) no debe impedir exportar.
            return [];
        }
    }

    private void Write(List<Entry> entries)
    {
        if (entries.Count == 0)
        {
            File.Delete(Path);
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("exportaciones");
            foreach (Entry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id);
                writer.WriteNumber("proceso", entry.ProcessId);
                writer.WriteNumber("inicioProceso", entry.ProcessStartTicks);
                writer.WriteString("salida", entry.OutputPath);
                writer.WriteStartArray("temporales");
                foreach (string path in entry.Temporaries.Where(p => p.Contains(ExportOutputGuard.TemporaryMarker, StringComparison.OrdinalIgnoreCase)))
                {
                    writer.WriteStringValue(path);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, stream.ToArray());
        File.Move(temporary, Path, overwrite: true);
    }

    private sealed record Entry(string Id, int ProcessId, long ProcessStartTicks, string OutputPath, List<string> Temporaries);
}
