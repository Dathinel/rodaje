using System.Reflection;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Views.Export;

namespace Rodaje.App.Services.Collab;

/// <summary>
/// Lo que la ventana tiene de la colaboración: la sala, dónde está <c>dathinel-red</c>, la clave de este equipo y el último
/// nombre que usó la persona. Sin WinUI para poder probarlo; la ventana le pasa cómo volver al hilo de la interfaz.
/// </summary>
public sealed partial class CollabHost : IDisposable
{
    public const string RedFileName = "dathinel-red.exe";
    public const string RedVariable = "RODAJE_RED";

    private readonly string _folder;
    private readonly Action<Action> _post;
    private readonly SwitchHandler _switch = new();

    public CollabHost(string dataRoot, Action<Action> post, string? appVersion = null, string? redPath = null, ICollabPayloadHandler? handler = null)
    {
        _post = post;
        if (handler is not null)
        {
            _switch.SetTarget(handler);
        }

        _folder = Path.Combine(dataRoot, "colaboracion");
        try
        {
            Directory.CreateDirectory(_folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sin carpeta de datos, abrir una sala dirá que no pudo leer la clave del equipo; el resto de Rodaje no se entera.
        }

        Room = new RoomService(
            new CollabEnvironment(redPath ?? FindRed() ?? Path.Combine(AppContext.BaseDirectory, RedFileName), Path.Combine(_folder, "identidad.bin"), appVersion ?? CurrentVersion()),
            _switch,
            post);
        Room.StateChanged += OnRoomState;
        Presence = new CollabPresence(Room);
    }

    public RoomService Room { get; }

    /// <summary>Los cabezales de los demás y las pistas reservadas, para dibujarlos en la línea de tiempo.</summary>
    public CollabPresence Presence { get; }

    private EditorSession? _session;
    private string[] _selection = [];
    private string? _selectionTrack;

    /// <summary>Engancha la sesión del editor: su cabezal y su selección son lo que ven los demás, y las pistas de lo seleccionado se reservan.</summary>
    public void BindSession(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        session.TransportChanged += (_, _) => PublishLocal();
        session.SelectionChanged += (_, _) =>
        {
            RefreshSelection();
            PublishLocal();
        };
    }

    private void RefreshSelection()
    {
        if (_session is not { } session)
        {
            return;
        }

        _selection = [.. session.SelectedClipIds.Take(CollabPresence.MaxSelected)];
        HashSet<string> ids = [.. _selection];
        List<string> tracks = [];
        foreach (Track track in session.Sequence.VideoTracks.Concat(session.Sequence.AudioTracks))
        {
            if (track.Clips.Any(c => ids.Contains(c.Id)))
            {
                tracks.Add(track.Id);
            }
        }

        _selectionTrack = tracks.Count > 0 ? tracks[0] : null;
        Presence.WantTracks(tracks);
    }

    private void PublishLocal()
    {
        if (_session is { } session && Room.State.InRoom)
        {
            Presence.SetLocal(session.ProgramPosition.ToSeconds(), _selection, _selectionTrack);
        }
    }

    /// <summary>Pregunta a la persona antes de dejar la sala (abrir otro proyecto): título, texto y botón. La pone la ventana.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>La edición compartida de esta sala; nula hasta que empieza y tras terminar.</summary>
    public CollabEngine? Engine { get; private set; }

    /// <summary>La cola de exportación del equipo, para «Exportar en el anfitrión»; la pone la ventana.</summary>
    public ExportJobList? ExportQueue { get; set; }

    /// <summary>Aprobar a mano el pedido de un invitado («Luis pide exportar…»); la pone la ventana. Sin ella, todo pedido se rechaza.</summary>
    public Func<HostExportApproval, Task<bool>>? ApproveExport { get; set; }

    /// <summary>El servicio del anfitrión de «Exportar en el anfitrión»; nulo hasta que hospeda y tras terminar.</summary>
    public ExportShareHost? HostExports { get; private set; }

    private ProjectShareHost? _share;

    public void Dispose()
    {
        Room.StateChanged -= OnRoomState;
        StopEditing();
        Presence.Dispose();
    }

    private void OnRoomState(RoomState state)
    {
        if (state.Phase is RoomPhase.Closed or RoomPhase.Ended)
        {
            StopEditing();
        }
    }

    /// <summary>
    /// Empieza la edición compartida de la sala en la que ya se está. El anfitrión (<paramref name="received"/> nulo) comparte el
    /// proyecto abierto; el invitado empieza con la copia que recibió, que ya tiene abierta.
    /// </summary>
    public void StartEditing(ReceivedProject? received)
    {
        if (_session is not { } session || Engine is not null)
        {
            return;
        }

        var engine = new CollabEngine(
            session.Project,
            Room,
            Presence,
            work => UiInvoke.Run<int>(_post, () =>
            {
                work();
                return 0;
            }),
            () =>
            {
                _applyingExternal = true;
                try
                {
                    session.ApplyExternalChange(() => { });
                }
                finally
                {
                    _applyingExternal = false;
                }
            },
            (text, problem) => session.Notify(problem ? NotificationSeverity.Warning : NotificationSeverity.Informational, "Colaboración", text),
            () => session.IsInGesture);
        engine.Desynced += reason => UiInvoke.Run<int>(_post, () =>
        {
            UiTask.Run(Room.EndWithAsync("Tu copia del proyecto dejó de coincidir con la de la sala y no se puede seguir editando así. "
                + "Tu copia sigue en este equipo. Vuelve a entrar con una invitación nueva. (" + reason + ")"));
            return 0;
        });
        Engine = engine;
        if (received is null)
        {
            engine.StartAsHost();
            _share = new ProjectShareHost(Room, new SessionProjectSource(
                session,
                work => UiInvoke.Run(_post, work),
                engine.CaptureForGuest), Room.RoomFolder);
            if (ExportQueue is { } queue)
            {
                HostExports = new ExportShareHost(Room, queue, ConfirmedProjectAsync, Room.RoomFolder,
                    work => UiInvoke.Run<int>(_post, () =>
                    {
                        work();
                        return 0;
                    }))
                {
                    // La aprobación abre un diálogo: va por el hilo de la interfaz como cualquier diálogo de la ventana.
                    Approve = ask => ApproveExport is { } approve
                        ? UiInvoke.Run(_post, () => approve(ask)).Unwrap()
                        : Task.FromResult(false),
                };
            }
        }
        else
        {
            engine.StartAsGuest(received.Project, received.Versions, received.Seq);
        }

        _switch.SetTarget(engine);
        session.CollabHistory = engine;
        session.BeforeProjectReplaced = ConfirmLeaveRoomAsync;
        session.HistoryChanged += OnHistoryChanged;
    }

    private void StopEditing()
    {
        if (_session is { } session)
        {
            session.HistoryChanged -= OnHistoryChanged;
            session.CollabHistory = null;
            session.BeforeProjectReplaced = null;
        }

        _share?.Dispose();
        _share = null;
        HostExports?.Dispose();
        HostExports = null;
        _switch.SetTarget(null);
        Engine?.Dispose();
        Engine = null;
    }

    private bool _applyingExternal;

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        if (_applyingExternal || _session is not { } session || Engine is not { } engine || session.IsInGesture)
        {
            return;
        }

        engine.OnLocalChange(session.History.UndoDescription ?? "Cambio");
    }

    /// <summary>
    /// La copia confirmada del proyecto del anfitrión para «Exportar en el anfitrión»: la instantánea de
    /// <see cref="CollabEngine.CaptureForGuest"/> con las rutas reales de sus medios (las del proyecto abierto, que se leen
    /// en el hilo de la interfaz); la de un medio que solo existe en la copia compartida queda como marcador y el medio
    /// saldrá «sin conexión» al exportar.
    /// </summary>
    private async Task<ConfirmedExportProject> ConfirmedProjectAsync(CancellationToken cancellationToken)
    {
        CollabEngine engine = Engine ?? throw new CollabException("La edición compartida ya terminó.");
        (string json, _) = engine.CaptureForGuest();
        ProjectParseResult parsed = ProjectWire.TryParse(json);
        if (parsed.Project is not { } project)
        {
            throw new CollabException("La copia confirmada del proyecto no se pudo leer en este equipo.");
        }

        return await UiInvoke.Run(_post, () =>
        {
            Project live = _session?.Project ?? new Project();
            for (int i = 0; i < project.Media.Count; i++)
            {
                project.Media[i].Path = live.FindMedia(project.Media[i].Id)?.Path
                    ?? MediaPlaceholders.PlaceholderFor(project.Media[i], i);
            }

            string? directory = _session?.FilePath is { } file ? Path.GetDirectoryName(file) : null;
            return new ConfirmedExportProject(project, directory);
        });
    }

    private async Task<bool> ConfirmLeaveRoomAsync()
    {
        if (!Room.State.InRoom || Confirm is not { } confirm)
        {
            return true;
        }

        bool host = Room.State.IsHost;
        bool ok = await confirm(
            host ? "¿Quieres cerrar la sala?" : "¿Quieres salir de la sala?",
            host
                ? "Si cambias de proyecto, la sala se cierra para todos. Cada persona conserva su copia del proyecto."
                : "Si cambias de proyecto, sales de la sala. Tu copia del proyecto se queda en tu equipo.",
            host ? "Cerrar la sala" : "Salir de la sala");
        if (ok)
        {
            await Room.LeaveAsync();
        }

        return ok;
    }

    /// <summary>
    /// Lo que la sala le pide a Rodaje sobre las cargas mientras la edición no ha empezado (el invitado aún recibe el proyecto):
    /// lo que llega se guarda y se entrega al empezar; nada se acepta todavía.
    /// </summary>
    private sealed class SwitchHandler : ICollabPayloadHandler
    {
        private readonly Lock _lock = new();
        private readonly List<Ordenado> _waiting = [];
        private ICollabPayloadHandler? _target;

        public void SetTarget(ICollabPayloadHandler? target)
        {
            List<Ordenado> replay;
            lock (_lock)
            {
                _target = target;
                replay = target is null ? [] : [.. _waiting];
                if (target is not null)
                {
                    _waiting.Clear();
                }
            }

            foreach (Ordenado o in replay)
            {
                target!.Apply(o);
            }
        }

        public ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken cancellationToken)
        {
            ICollabPayloadHandler? target;
            lock (_lock)
            {
                target = _target;
            }

            return target?.ValidateAsync(author, payload, cancellationToken)
                ?? ValueTask.FromResult(Veredicto.No("sin-edicion", "La sala todavía no está lista."));
        }

        public void Apply(Ordenado ordered)
        {
            ICollabPayloadHandler? target;
            lock (_lock)
            {
                target = _target;
                if (target is null && _waiting.Count < 4096)
                {
                    _waiting.Add(ordered);
                }
            }

            target?.Apply(ordered);
        }

        public void Rejected(Rechazo rejection)
        {
            ICollabPayloadHandler? target;
            lock (_lock)
            {
                target = _target;
            }

            target?.Rejected(rejection);
        }
    }

    /// <summary>El permiso que se propone al aprobar a quien pide entrar (el que se eligió al invitar).</summary>
    public Rol DefaultRole { get; set; } = Rol.Editar;

    /// <summary>La versión de Rodaje sin el commit (<c>1.0.4+abc123</c> → <c>1.0.4</c>): solo entran las iguales.</summary>
    public static string CurrentVersion()
    {
        string v = typeof(CollabHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CollabHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    /// <summary>Dónde está <c>dathinel-red</c>: la variable, junto a Rodaje, en <c>modulos\red</c> o, en desarrollo, en el repositorio.</summary>
    public static string? FindRed()
    {
        if (Environment.GetEnvironmentVariable(RedVariable) is { Length: > 0 } fromEnv && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        string exe = OperatingSystem.IsWindows() ? RedFileName : "dathinel-red";
        string here = AppContext.BaseDirectory;
        foreach (string candidate in new[] { Path.Combine(here, exe), Path.Combine(here, "modulos", "red", exe) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (DirectoryInfo? d = new(here); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "Rodaje.slnx")))
            {
                continue;
            }

            foreach (string profile in new[] { "release", "debug" })
            {
                string dev = Path.Combine(d.FullName, "externo", "sala", "red", "target", profile, exe);
                if (File.Exists(dev))
                {
                    return dev;
                }
            }
        }

        return null;
    }

    /// <summary>La carpeta de la sala de un proyecto: <c>Mi proyecto.sala</c> junto a <c>Mi proyecto.rodaje</c>.</summary>
    public static string RoomFolderFor(string projectFile) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectFile))!, Path.GetFileNameWithoutExtension(projectFile) + ".sala");

    /// <summary>El último nombre que usó la persona en una sala, para no pedírselo cada vez.</summary>
    public string Alias
    {
        get
        {
            try
            {
                string path = Path.Combine(_folder, "nombre.txt");
                return File.Exists(path) ? CollabNames.NormalizeAlias(File.ReadAllText(path)) ?? "" : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }

        set
        {
            if (CollabNames.NormalizeAlias(value) is not { } alias)
            {
                return;
            }

            try
            {
                AppDataPaths.WriteAllTextAtomic(Path.Combine(_folder, "nombre.txt"), alias);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No recordar el nombre no impide colaborar.
            }
        }
    }

    /// <summary>Un nombre para proponer la primera vez: el del usuario de Windows.</summary>
    public static string SuggestedAlias() => CollabNames.NormalizeAlias(Environment.UserName) ?? "";
}
