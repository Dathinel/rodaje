using System.Globalization;
using Rodaje.App.Services.Crashes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Services.Autosave;

/// <summary>
/// Autoguardado del proyecto abierto en <c>%LOCALAPPDATA%\Rodaje\Autoguardado</c> y oferta de recuperarlo tras un cierre
/// inesperado.
/// </summary>
/// <remarks>
/// <para>
/// Se usa desde el hilo de la interfaz. La ventana llama a <see cref="Tick"/> con su temporizador; cuando
/// <see cref="AutosaveSchedule"/> dice que toca, se copia el proyecto (<see cref="Project.Clone"/>, en el hilo de la
/// interfaz, igual que lo que recibe el reproductor) y la copia se escribe en un hilo del pool, así que editar nunca
/// espera al disco. Las escrituras van en fila, en el orden en que se pidieron.
/// </para>
/// <para>
/// Al arrancar, <see cref="OfferRecoveryAsync"/> busca la sesión más reciente que terminó mal con cambios sin guardar y
/// lo avisa con un botón «Recuperar». Recuperar nunca pisa el proyecto original: escribe
/// <c>«nombre» (recuperado).rodaje</c> junto a él (o donde diga la persona si nunca se guardó) y lo abre.
/// </para>
/// <para>No usa WinUI: <c>tests/Rodaje.App.Tests</c> compila esta carpeta. Es <c>partial</c> porque CsWinRT lo pide a toda clase con <see cref="IDisposable"/> (como <see cref="InstanceRegistry"/>).</para>
/// </remarks>
public sealed partial class AutosaveService : IDisposable
{
    private readonly EditorSession _session;
    private readonly IEditorDialogs _dialogs;
    private readonly AutosaveSchedule _schedule;
    private readonly TimeProvider _clock;
    private readonly List<RecoverableSession> _offered = [];
    private readonly HashSet<RecoverableSession> _recovering = [];
    private AutosaveSession? _store;
    private Task _queue = Task.CompletedTask;
    private bool _writing;
    private bool _wasDirty;
    private bool _failureNotified;
    private bool _formatFailureNotified;

    public AutosaveService(EditorSession session, IEditorDialogs dialogs, string root, AutosaveSchedule? schedule = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _session = session;
        _dialogs = dialogs;
        Root = Path.GetFullPath(root);
        _schedule = schedule ?? new AutosaveSchedule();
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary><c>%LOCALAPPDATA%\Rodaje\Autoguardado</c> (o la carpeta de datos de <see cref="AppDataPaths.OverrideVariable"/>).</summary>
    public static string DefaultRoot => Path.Combine(AppDataPaths.Root, AutosaveSession.DefaultFolderName);

    /// <summary>Carpeta de autoguardado.</summary>
    public string Root { get; }

    /// <summary>Si el autoguardado está activo (se pudo crear la carpeta de esta sesión).</summary>
    public bool IsActive => _store is not null;

    /// <summary>Carpeta de esta sesión, o <c>null</c> si no está activo.</summary>
    public string? SessionDirectory => _store?.Directory;

    /// <summary>Crea la carpeta de esta sesión y empieza a seguir los cambios. Si el disco no deja, avisa y sigue sin autoguardado.</summary>
    public bool Start()
    {
        _session.VerifyAccess();
        if (_store is not null)
        {
            return true;
        }

        try
        {
            _store = AutosaveSession.Begin(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _session.Notify(NotificationSeverity.Warning, "Autoguardado desactivado",
                $"No se pudo preparar la carpeta «{Root}»: {ex.Message} Guarda a mano con Ctrl+S.");
            return false;
        }

        _wasDirty = _session.IsDirty;
        if (_wasDirty)
        {
            _schedule.NoteEdit(Now);
        }

        _session.ProjectEdited += OnProjectEdited;
        _session.HistoryChanged += OnHistoryChanged;
        _session.ProjectOpened += OnProjectOpened;
        return true;
    }

    /// <summary>Lo llama el temporizador de la ventana; escribe una copia si toca y no hay otra en curso.</summary>
    public void Tick()
    {
        _session.VerifyAccess();
        if (_store is not { } store || _writing || _session.IsInGesture || _session.IsBusy)
        {
            return;
        }

        DateTimeOffset now = Now;
        if (!_session.IsDirty || !_schedule.IsDue(now))
        {
            return;
        }

        _schedule.NoteAttempt(now);
        UiTask.Run(WriteAsync(store, _session.Project.Clone(), _session.FilePath, now));
    }

    /// <summary>
    /// Busca, fuera del hilo de la interfaz, una sesión anterior que se cerró de forma inesperada con cambios sin guardar
    /// y, si la hay, lo avisa con el botón «Recuperar». De paso borra las sesiones viejas.
    /// </summary>
    public async Task OfferRecoveryAsync()
    {
        _session.VerifyAccess();
        string root = Root;
        IReadOnlyList<RecoverableSession> found = await Task.Run(() =>
        {
            AutosaveRecovery.Prune(root, AutosaveRecovery.DefaultMaxAge);
            return AutosaveRecovery.FindRecoverable(root);
        });
        if (found.Count == 0)
        {
            return;
        }

        // Se ofrece la más reciente; las demás se sueltan y se ofrecen en el próximo arranque.
        RecoverableSession newest = found[0];
        foreach (RecoverableSession other in found.Skip(1))
        {
            other.Dispose();
        }

        _offered.Add(newest);
        DateTime saved = newest.Latest.SavedAt.ToLocalTime().DateTime;
        string when = saved.ToString("dd/MM/yyyy 'a las' HH:mm", CultureInfo.InvariantCulture);
        string more = found.Count > 1 ? $" Hay {found.Count - 1} copia(s) más antiguas; se ofrecerán la próxima vez." : "";
        _session.Notify(NotificationSeverity.Warning, "Rodaje se cerró de forma inesperada",
            $"Hay una copia de «{newest.ProjectName}» del {when} con cambios sin guardar.{more}",
            new NotificationAction("Recuperar", () => UiTask.Run(RecoverAsync(newest))));
    }

    /// <summary>
    /// Escribe la copia más nueva de <paramref name="recoverable"/> como un proyecto aparte y lo abre. Si sale bien, la
    /// sesión ya no se vuelve a ofrecer. Un segundo clic en «Recuperar» mientras va el primero, o después de recuperar,
    /// no hace nada y devuelve <c>false</c>.
    /// </summary>
    public async Task<bool> RecoverAsync(RecoverableSession recoverable)
    {
        ArgumentNullException.ThrowIfNull(recoverable);
        _session.VerifyAccess();
        if (!_recovering.Add(recoverable))
        {
            return false;
        }

        bool recovered = false;
        try
        {
            recovered = await RecoverOnceAsync(recoverable);
            return recovered;
        }
        finally
        {
            if (!recovered)
            {
                // Si no salió (se canceló o falló), se puede volver a intentar.
                _recovering.Remove(recoverable);
            }
        }
    }

    private async Task<bool> RecoverOnceAsync(RecoverableSession recoverable)
    {
        Project project;
        AutosaveSnapshot snapshot;
        try
        {
            (project, snapshot) = await Task.Run(() => recoverable.LoadNewest());
        }
        catch (ProjectFormatException ex)
        {
            _session.Notify(NotificationSeverity.Error, "No se pudo recuperar", ex.Message);
            return false;
        }

        string suggested = $"{recoverable.ProjectName} (recuperado){ProjectSerializer.FileExtension}";
        string? target = RecoveredPathNextTo(recoverable.ProjectPath, recoverable.ProjectName);
        if (target is null)
        {
            string? picked = await _dialogs.PickProjectSavePathAsync(suggested);
            if (picked is null)
            {
                return false;
            }

            target = Path.GetFullPath(ProjectPaths.WithProjectExtension(picked));
        }

        // Si el archivo no existía, es nuestro: si la apertura no sale, se borra para que cada reintento no deje otro
        // «(recuperado N)» huérfano. Uno que la persona eligió pisar en el diálogo se deja como está.
        bool created = !File.Exists(target);
        try
        {
            string folder = Path.GetDirectoryName(target)!;
            await Task.Run(() => ProjectSerializer.Save(ProjectPaths.ForSaving(project, folder), target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ProjectFormatException)
        {
            _session.Notify(NotificationSeverity.Error, "No se pudo recuperar", $"No se pudo escribir «{target}»: {ex.Message}");
            return false;
        }

        // Abrir puede no salir: la persona cancela el aviso de cambios sin guardar, el editor está ocupado o el archivo
        // no se deja leer. La copia sigue en el autoguardado y se puede volver a intentar.
        if (!await _session.OpenProjectAsync(target))
        {
            if (created)
            {
                DeleteQuietly(target);
            }

            return false;
        }

        try
        {
            string directory = recoverable.Directory;
            string name = recoverable.ProjectName;
            string? path = recoverable.ProjectPath;
            await Task.Run(() => AutosaveSession.MarkRecovered(directory, name, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Queda Discard, que quita el bloqueo; solo si fallan los dos se vuelve a ofrecer en el próximo arranque.
        }

        recoverable.Discard();
        _offered.Remove(recoverable);
        _session.Notify(NotificationSeverity.Success, "Proyecto recuperado",
            $"Se guardó como «{Path.GetFileName(target)}» en «{Path.GetDirectoryName(target)}». " +
            $"Es la copia n.º {snapshot.Number} de la sesión que se cerró.");
        return true;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Queda el archivo huérfano, como antes; la copia sigue en el autoguardado.
        }
    }

    /// <summary>
    /// <c>«nombre» (recuperado).rodaje</c> en la carpeta del proyecto original, sin pisar nada que exista; <c>null</c>
    /// si el proyecto nunca se guardó o su carpeta ya no está.
    /// </summary>
    public static string? RecoveredPathNextTo(string? projectPath, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return null;
        }

        string? folder;
        string baseName;
        try
        {
            string full = Path.GetFullPath(projectPath);
            folder = Path.GetDirectoryName(full);
            baseName = Path.GetFileNameWithoutExtension(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (folder is null || !Directory.Exists(folder))
        {
            return null;
        }

        if (baseName.Length == 0)
        {
            baseName = projectName;
        }

        for (int i = 1; i < 1000; i++)
        {
            string suffix = i == 1 ? " (recuperado)" : $" (recuperado {i})";
            string candidate = Path.Combine(folder, baseName + suffix + ProjectSerializer.FileExtension);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Cierre normal de la ventana: borra las copias de esta sesión; lo ofrecido y no decidido se vuelve a ofrecer.</summary>
    public void Complete()
    {
        Unsubscribe();
        _store?.Complete();
        _store = null;
        ReleaseOffered();
    }

    /// <summary>Suelta todo sin borrar: para el próximo arranque, esta sesión terminó mal.</summary>
    public void Dispose()
    {
        Unsubscribe();
        _store?.Dispose();
        _store = null;
        ReleaseOffered();
    }

    /// <summary>Termina cuando terminan las escrituras pedidas hasta ahora, hayan fallado o no (para las pruebas).</summary>
    internal Task WhenWritesDone() =>
        _queue.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private DateTimeOffset Now => _clock.GetUtcNow();

    private async Task WriteAsync(AutosaveSession store, Project copy, string? filePath, DateTimeOffset since)
    {
        _writing = true;
        try
        {
            await Enqueue(() => store.Write(copy, filePath));
            _schedule.NoteCaughtUp(since);
            _failureNotified = false;
            _formatFailureNotified = false;
        }
        catch (ObjectDisposedException)
        {
            // La ventana se cerró mientras tanto.
        }
        catch (ProjectFormatException ex)
        {
            // El proyecto tiene un valor que no se puede escribir (un NaN, por ejemplo): el serializador lo rechaza antes
            // de crear el temporal, así que no queda ningún .tmp. Se avisa una vez, no en cada ciclo; el aviso queda en
            // el registro de la sesión. Se reintenta con el calendario de siempre: si la persona corrige el valor, la
            // próxima copia sale y el aviso se rearma.
            if (!_formatFailureNotified)
            {
                _formatFailureNotified = true;
                _session.Notify(NotificationSeverity.Warning, "No se pudo autoguardar",
                    $"El proyecto tiene un valor que no se puede escribir: {ex.Message} No se harán copias hasta que se corrija.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failureNotified)
            {
                _failureNotified = true;
                _session.Notify(NotificationSeverity.Warning, "No se pudo autoguardar",
                    $"{ex.Message} Se volverá a intentar; mientras tanto, guarda a mano con Ctrl+S.");
            }
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>Pone el trabajo en la fila de escrituras; cada uno empieza cuando termina el anterior, haya fallado o no.</summary>
    private Task Enqueue(Action work)
    {
        Task next = _queue.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _queue = next;
        return next;
    }

    /// <summary>
    /// Cambios de datos de la sesión que no deben interrumpir: si fallan, la próxima copia lo vuelve a intentar. Un fallo
    /// que no sea de disco se informa (UiTask.Run) en vez de quedar en la fila sin observar.
    /// </summary>
    private void EnqueueQuiet(Action<AutosaveSession> work)
    {
        if (_store is not { } store)
        {
            return;
        }

        UiTask.Run(Enqueue(() =>
        {
            try
            {
                work(store);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Quedan los datos anteriores; como mucho se ofrecerá recuperar algo que ya estaba guardado.
            }
        }));
    }

    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e) => _schedule.NoteEdit(Now);

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        bool dirty = _session.IsDirty;
        if (_wasDirty && !dirty)
        {
            // Se guardó (o se deshizo hasta lo guardado): un cierre ahora no tiene nada que ofrecer.
            _schedule.NoteCaughtUp(Now);
            string name = _session.ProjectName;
            string? path = _session.FilePath;
            EnqueueQuiet(store => store.MarkSaved(name, path));
        }

        _wasDirty = dirty;
    }

    private void OnProjectOpened(object? sender, EventArgs e)
    {
        // El proyecto anterior se guardó o se descartó a propósito antes de abrir otro.
        _schedule.NoteCaughtUp(Now);
        _wasDirty = _session.IsDirty;
        string name = _session.ProjectName;
        string? path = _session.FilePath;
        EnqueueQuiet(store => store.Clear(name, path));
    }

    private void Unsubscribe()
    {
        _session.ProjectEdited -= OnProjectEdited;
        _session.HistoryChanged -= OnHistoryChanged;
        _session.ProjectOpened -= OnProjectOpened;
    }

    private void ReleaseOffered()
    {
        foreach (RecoverableSession offered in _offered)
        {
            offered.Dispose();
        }

        _offered.Clear();
    }
}
