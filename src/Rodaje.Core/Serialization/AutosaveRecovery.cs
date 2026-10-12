using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.Core.Model;

namespace Rodaje.Core.Serialization;

/// <summary>Una copia de autoguardado: su archivo, su número de versión y cuándo se escribió.</summary>
public sealed record AutosaveSnapshot(string Path, int Number, DateTimeOffset SavedAt);

/// <summary>
/// Una sesión de autoguardado que terminó con un cierre inesperado y tiene cambios sin guardar. Mientras este objeto
/// vive tiene su bloqueo, así que otra ejecución que arranque a la vez no la ofrece también.
/// </summary>
public sealed class RecoverableSession : IDisposable
{
    private FileStream? _lock;

    internal RecoverableSession(string directory, FileStream claimedLock, string projectName, string? projectPath,
        IReadOnlyList<AutosaveSnapshot> snapshots)
    {
        Directory = directory;
        _lock = claimedLock;
        ProjectName = projectName;
        ProjectPath = projectPath;
        Snapshots = snapshots;
    }

    public string Directory { get; }

    /// <summary>Nombre del proyecto en la última copia.</summary>
    public string ProjectName { get; }

    /// <summary>El <c>.rodaje</c> del proyecto, o <c>null</c> si nunca se guardó.</summary>
    public string? ProjectPath { get; }

    /// <summary>Las copias, de la más nueva a la más vieja; al menos una.</summary>
    public IReadOnlyList<AutosaveSnapshot> Snapshots { get; }

    public AutosaveSnapshot Latest => Snapshots[0];

    /// <summary>
    /// Abre la copia más nueva que se pueda leer; si la última está dañada, prueba con las anteriores.
    /// </summary>
    /// <exception cref="ProjectFormatException">Si ninguna copia se puede abrir; el mensaje es el de la más nueva.</exception>
    public (Project Project, AutosaveSnapshot Snapshot) LoadNewest()
    {
        Exception? first = null;
        foreach (AutosaveSnapshot snapshot in Snapshots)
        {
            try
            {
                return (ProjectSerializer.Load(snapshot.Path), snapshot);
            }
            catch (Exception ex) when (ex is ProjectFormatException or IOException or UnauthorizedAccessException)
            {
                first ??= ex;
            }
        }

        throw new ProjectFormatException(
            $"No se pudo abrir ninguna copia de autoguardado de «{ProjectName}». {first?.Message}", first);
    }

    /// <summary>
    /// No se va a recuperar (o ya se recuperó): quita el bloqueo para que no se vuelva a ofrecer. Las copias quedan
    /// como historial hasta que la limpieza de arranque las borre por antiguas.
    /// </summary>
    public void Discard()
    {
        Dispose();
        try
        {
            File.Delete(System.IO.Path.Combine(Directory, AutosaveSession.LockFileName));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Suelta el bloqueo sin decidir: el siguiente arranque la vuelve a ofrecer.</summary>
    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}

/// <summary>Busca, al arrancar, las sesiones de autoguardado que terminaron con un cierre inesperado, y limpia las viejas.</summary>
public static class AutosaveRecovery
{
    /// <summary>Lo que se guardan las sesiones que nadie recuperó.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    // Una carpeta recién creada todavía puede no tener bloqueo: no se toca durante este tiempo.
    private static readonly TimeSpan CreationGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Las sesiones de <paramref name="root"/> que terminaron mal con cambios sin guardar, de la más reciente a la más
    /// antigua. Cada una queda bloqueada por quien la recibe hasta que la descarte o la suelte.
    /// </summary>
    /// <remarks>
    /// Una sesión terminó mal si todavía tiene su archivo de bloqueo y este se puede abrir para escribir (su proceso
    /// ya no existe). Las que están vivas (otra ventana de Rodaje abierta), las cerradas bien y las que no tienen
    /// cambios sin guardar o ninguna copia no se devuelven.
    /// </remarks>
    public static IReadOnlyList<RecoverableSession> FindRecoverable(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var found = new List<RecoverableSession>();
        foreach (string directory in EnumerateSessions(root))
        {
            IReadOnlyList<AutosaveSnapshot> snapshots = ListSnapshots(directory);
            if (snapshots.Count == 0 || ReadInfo(directory) is not { UnsavedChanges: true } info)
            {
                continue;
            }

            if (TryClaim(directory) is not { } claimed)
            {
                continue;
            }

            found.Add(new RecoverableSession(directory, claimed, info.ProjectName ?? "Proyecto", info.ProjectPath, snapshots));
        }

        found.Sort((a, b) => b.Latest.SavedAt.CompareTo(a.Latest.SavedAt));
        return found;
    }

    /// <summary>
    /// Borra las sesiones que ya no sirven: las que no tienen proceso vivo y llevan más de <paramref name="maxAge"/> sin
    /// cambios, y las que no tienen proceso vivo ni ninguna copia. Nunca toca una sesión viva. Devuelve cuántas borró.
    /// </summary>
    public static int Prune(string root, TimeSpan maxAge) => Prune(root, maxAge, DateTimeOffset.UtcNow);

    internal static int Prune(string root, TimeSpan maxAge, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        int deleted = 0;
        foreach (string directory in EnumerateSessions(root))
        {
            DateTimeOffset lastActivity = LastActivity(directory);
            if (now - lastActivity < CreationGrace)
            {
                continue;
            }

            string lockPath = Path.Combine(directory, AutosaveSession.LockFileName);
            FileStream? claimed = null;
            if (File.Exists(lockPath))
            {
                claimed = TryClaim(directory);
                if (claimed is null)
                {
                    // Viva, o la tiene otra ejecución que la está ofreciendo.
                    continue;
                }
            }

            using (claimed)
            {
                // Sin copias no hay nada que guardar; con copias se quedan como historial hasta que sean viejas.
                if (now - lastActivity < maxAge && ListSnapshots(directory).Count > 0)
                {
                    continue;
                }
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    /// <summary>Las copias completas de una carpeta de sesión, de la más nueva a la más vieja (los temporales no cuentan).</summary>
    public static IReadOnlyList<AutosaveSnapshot> ListSnapshots(string sessionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        var snapshots = new List<AutosaveSnapshot>();
        IEnumerable<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(sessionDirectory, AutosaveSession.SnapshotPrefix + "*" + ProjectSerializer.FileExtension)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return snapshots;
        }

        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            // Solo copias completas: el temporal de una escritura cortada («.rodaje.tmp») no cuenta.
            if (!string.Equals(Path.GetExtension(file), ProjectSerializer.FileExtension, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(name.AsSpan(AutosaveSession.SnapshotPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                continue;
            }

            DateTimeOffset savedAt;
            try
            {
                savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            snapshots.Add(new AutosaveSnapshot(file, number, savedAt));
        }

        snapshots.Sort((a, b) => b.Number.CompareTo(a.Number));
        return snapshots;
    }

    private static IEnumerable<string> EnumerateSessions(string root)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(Path.GetFullPath(root))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Abre el bloqueo para escribir sin compartir; solo se puede si su proceso ya no existe.</summary>
    private static FileStream? TryClaim(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, AutosaveSession.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException)
        {
            // Lo tiene abierto un proceso vivo, o no existe (cerrada bien o descartada).
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset LastActivity(string directory)
    {
        DateTime latest = DateTime.MinValue;
        try
        {
            latest = Directory.GetLastWriteTimeUtc(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                DateTime written = File.GetLastWriteTimeUtc(file);
                if (written > latest)
                {
                    latest = written;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Si no se puede leer, cuenta como reciente: no se borra lo que no se entiende.
            return DateTimeOffset.MaxValue;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(latest, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private sealed record SessionInfo(string? ProjectName, string? ProjectPath, bool UnsavedChanges);

    private static SessionInfo? ReadInfo(string directory)
    {
        try
        {
            string text = File.ReadAllText(Path.Combine(directory, AutosaveSession.InfoFileName));
            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return null;
            }

            return new SessionInfo(
                ReadString(root, "projectName"),
                ReadString(root, "projectPath"),
                root["unsavedChanges"] is JsonValue value && value.TryGetValue(out bool unsaved) && unsaved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject root, string name) =>
        root[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
