using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Media;

/// <summary>
/// Lo que ofrecen las carpetas vigiladas: la biblioteca lo muestra y lo importa con un clic (o lo descarta). Lo importado o
/// descartado queda anotado en los ajustes y no vuelve a aparecer. Todo en el hilo de la interfaz.
/// </summary>
public sealed class WatchedFolderInbox(EditorSession session, Func<AppSettings> settings)
{
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WatchedFile> Pending { get; private set; } = [];

    /// <summary>Cambió lo que se ofrece.</summary>
    public event EventHandler? Changed;

    /// <summary>Llegaron archivos que no se habían avisado todavía (para la barra de avisos).</summary>
    public event EventHandler<IReadOnlyList<WatchedFile>>? Arrived;

    /// <summary>El resultado de una pasada del <see cref="WatchedFolderScanner"/>.</summary>
    public void Update(IReadOnlyList<WatchedFile> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        HashSet<string> handled = new(settings().HandledWatchedFiles, StringComparer.OrdinalIgnoreCase);
        WatchedFile[] now = [.. found.Where(f => !handled.Contains(f.Path))];
        bool changed = now.Length != Pending.Count || !now.Select(f => f.Path).SequenceEqual(Pending.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        Pending = now;
        WatchedFile[] fresh = [.. now.Where(f => _announced.Add(f.Path))];
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (fresh.Length > 0)
        {
            Arrived?.Invoke(this, fresh);
        }
    }

    /// <summary>Importa todo lo que se ofrece en un paso del historial.</summary>
    public async Task ImportAsync()
    {
        string[] paths = [.. Pending.Select(f => f.Path)];
        if (paths.Length == 0)
        {
            return;
        }

        Forget(paths);
        await session.ImportMediaAsync(paths).ConfigureAwait(true);
    }

    /// <summary>No ofrecer más lo que hay ahora.</summary>
    public void Dismiss() => Forget([.. Pending.Select(f => f.Path)]);

    /// <summary>Texto corto para la biblioteca: «3 medios nuevos en «Cámara»».</summary>
    public static string Describe(IReadOnlyList<WatchedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        string[] folders = [.. files.Select(f => f.FolderName).Distinct(StringComparer.OrdinalIgnoreCase)];
        string where = folders.Length == 1 ? $"«{folders[0]}»" : $"{folders.Length} carpetas vigiladas";
        return files.Count == 1 ? $"«{Path.GetFileName(files[0].Path)}» es nuevo en {where}" : $"{files.Count} medios nuevos en {where}";
    }

    private void Forget(string[] paths)
    {
        WatchedFolderScanner.MarkHandled(settings(), paths);
        Pending = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
