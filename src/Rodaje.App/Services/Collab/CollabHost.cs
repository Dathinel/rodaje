using System.Reflection;
using System.Text.Json;
using Dathinel.Sala;
using Rodaje.Core.Model;
using Rodaje.App.Services.Crashes;
using Rodaje.Media.Proxies;

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
            _switch.Attach(handler);
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
            new CollabEnvironment(redPath ?? FindRed() ?? Path.Combine(AppContext.BaseDirectory, RedFileName), Path.Combine(_folder, "identidad.bin"), appVersion ?? CurrentVersion())
            {
                Locate = redPath is null ? FindRed : null,
            },
            _switch,
            post);
        Room.StateChanged += OnRoomState;
        Presence = new CollabPresence(Room);
        TrackRequests = new TrackRequests(Room, Presence, id => _trackNames.GetValueOrDefault(id), () => _session?.IsInGesture == true);
        TrackRequests.Requested += request => _post(() => ShowTrackRequest(request));
        TrackRequests.Told += text => _post(() => _session?.Notify(NotificationSeverity.Informational, "Colaboración", text));
    }

    public RoomService Room { get; }

    /// <summary>Los cabezales de los demás y las pistas reservadas, para dibujarlos en la línea de tiempo.</summary>
    public CollabPresence Presence { get; }

    /// <summary>«Pedir la pista» a quien la tiene reservada, y la respuesta cuando se la piden a este equipo.</summary>
    public TrackRequests TrackRequests { get; }

    private EditorSession? _session;
    private string[] _selection = [];
    private string? _selectionTrack;

    // Las pistas del proyecto (id → nombre) para validar lo que llega por la sala sin leer el proyecto fuera del hilo de la interfaz.
    private volatile Dictionary<string, string> _trackNames = new(StringComparer.Ordinal);

    /// <summary>Engancha la sesión del editor: su cabezal y su selección son lo que ven los demás, y las pistas de lo seleccionado se reservan.</summary>
    public void BindSession(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        RefreshTrackNames();
        session.HistoryChanged += (_, _) => RefreshTrackNames();
        session.ProjectOpened += (_, _) => RefreshTrackNames();
        session.TransportChanged += (_, _) => PublishLocal();
        session.SelectionChanged += (_, _) =>
        {
            RefreshSelection();
            PublishLocal();
        };
    }

    private void RefreshTrackNames()
    {
        if (_session is not { } session)
        {
            return;
        }

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (Track track in session.Project.Sequences.SelectMany(s => s.AllTracks))
        {
            names.TryAdd(track.Id, track.Name.Length > 0 ? track.Name : "sin nombre");
        }

        if (names.Count != _trackNames.Count || names.Any(kv => _trackNames.GetValueOrDefault(kv.Key) != kv.Value))
        {
            _trackNames = names;
        }
    }

    /// <summary>Quién tiene la pista, si se le puede pedir (para el menú de la cabecera); <c>null</c> si no.</summary>
    public string? TrackOwnerToAsk(string trackId) => TrackRequests.OwnerToAsk(trackId);

    /// <summary>«Pedir la pista»: manda el pedido y dice a la persona qué pasó.</summary>
    public void AskTrack(string trackId) => UiTask.Run(AskTrackAsync(trackId));

    private async Task AskTrackAsync(string trackId)
    {
        string text;
        bool problem = false;
        try
        {
            text = await TrackRequests.AskAsync(trackId);
        }
        catch (CollabException ex)
        {
            text = ex.Message;
            problem = true;
        }

        _session?.Notify(problem ? NotificationSeverity.Warning : NotificationSeverity.Informational, "Colaboración", text);
    }

    private void ShowTrackRequest(TrackRequest request) =>
        _session?.Notify(NotificationSeverity.Informational, "Colaboración", $"{request.FromAlias} te pide la pista {request.TrackName}.",
            new NotificationAction("Soltar la pista", () => UiTask.Run(TrackRequests.YieldAsync(request))),
            new NotificationAction("Ahora no", () => UiTask.Run(TrackRequests.DeclineAsync(request))));

    /// <summary>Un aviso de la edición compartida; si es el de un cambio que chocó con una pista ajena, lleva «Pedir la pista».</summary>
    private void NotifyCollab(EditorSession session, string text, bool problem)
    {
        NotificationAction? ask = Presence.TakeLastBlock() is { TrackId.Length: > 0 } block && text.Contains(block.Describe(), StringComparison.Ordinal)
            && TrackRequests.OwnerToAsk(block.TrackId) is not null
            ? new NotificationAction("Pedir la pista", () => AskTrack(block.TrackId))
            : null;
        session.Notify(problem ? NotificationSeverity.Warning : NotificationSeverity.Informational, "Colaboración", text, ask);
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

    /// <summary>La vista puede conectar o soltar los eventos del motor al empezar o terminar la edición.</summary>
    public event Action? EditingChanged;

    private ProjectShareHost? _share;
    private ProxyShareHost? _proxyShare;
    private RoomProxyMaker? _proxyMaker;

    /// <summary>Las copias ligeras que el invitado puede pedir a quien hospeda; nulo en el anfitrión y fuera de la sala.</summary>
    public RoomProxies? Proxies { get; private set; }

    public void Dispose()
    {
        Room.StateChanged -= OnRoomState;
        StopEditing();
        TrackRequests.Dispose();
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
    /// proyecto abierto; el invitado empieza con la copia que recibió, que ya tiene abierta. Devuelve <see langword="false"/>
    /// si mientras llegaba esa copia se perdió algún cambio retenido: entonces no empieza nada y hay que pedir otra copia
    /// inicial, que ya trae lo perdido.
    /// </summary>
    public bool StartEditing(ReceivedProject? received)
    {
        if (_session is not { } session || Engine is not null)
        {
            return true;
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
            (text, problem) => NotifyCollab(session, text, problem),
            () => session.IsInGesture);
        engine.Desynced += reason => UiInvoke.Run<int>(_post, () =>
        {
            UiTask.Run(Room.EndWithAsync("Tu copia del proyecto dejó de coincidir con la de la sala y no se puede seguir editando así. "
                + "Tu copia sigue en este equipo. Vuelve a entrar con una invitación nueva. (" + reason + ")"));
            return 0;
        });
        if (received is null)
        {
            engine.StartAsHost();
            IRoomProxyMaker maker = ProxyMakerFor(session) is { } real ? real : new MissingFfmpeg();
            _proxyShare = new ProxyShareHost(Room, maker, Room.RoomFolder);
        }
        else
        {
            engine.StartAsGuest(received.Project, received.Versions, received.Seq);
            _share = new ProjectShareHost(Room, new SessionProjectSource(
                session,
                work => UiInvoke.Run(_post, work),
                engine.CaptureForGuest), Room.RoomFolder);
            Proxies = new RoomProxies(Room, Room.RoomFolder, received.Media);
        }

        if (!_switch.TryAttach(engine))
        {
            engine.Dispose();
            return false;
        }

        Engine = engine;
        if (received is null)
        {
            _share = new ProjectShareHost(Room, new SessionProjectSource(
                session,
                work => UiInvoke.Run(_post, work),
                engine.CaptureForGuest), Room.RoomFolder);
        }

        session.CollabHistory = engine;
        session.BeforeProjectReplaced = ConfirmLeaveRoomAsync;
        session.HistoryChanged += OnHistoryChanged;
        EditingChanged?.Invoke();
        return true;
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
        _proxyShare?.Dispose();
        _proxyShare = null;
        _proxyMaker?.Dispose();
        _proxyMaker = null;
        Proxies = null;
        _switch.Detach();
        Engine?.Dispose();
        Engine = null;
        EditingChanged?.Invoke();
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

    /// <summary>El generador de copias ligeras del anfitrión; <c>null</c> sin FFmpeg (el invitado oye por qué).</summary>
    private RoomProxyMaker? ProxyMakerFor(EditorSession session)
    {
        if (FfmpegExecutable.TryLocate(out _) is not { } ffmpeg)
        {
            return null;
        }

        string projectDirectory = session.FilePath is { Length: > 0 } file ? Path.GetDirectoryName(file)! : Room.RoomFolder;
        return _proxyMaker = new RoomProxyMaker(ffmpeg, () => session.Project.Media, m => m.ResolvePath(projectDirectory));
    }

    /// <summary>Responde al invitado con el porqué cuando este equipo no puede codificar nada.</summary>
    private sealed class MissingFfmpeg : IRoomProxyMaker
    {
        public Task<RoomProxy?> MakeAsync(string mediaHash, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
            => Task.FromException<RoomProxy?>(new CollabException(
                "Este equipo no puede hacer copias ligeras: le falta el componente de video. Pídela otra vez cuando quien hospeda tenga Rodaje completo."));
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
        private readonly EarlyPayloadQueue _early = new();
        private ICollabPayloadHandler? _target;

        /// <summary>Destino desde la construcción: todavía no puede haber cola retenida.</summary>
        public void Attach(ICollabPayloadHandler target)
        {
            lock (_lock)
            {
                _target = target;
            }
        }

        /// <summary>
        /// Instala el destino y le entrega lo retenido, en orden y dentro del candado, para que nada que llegue a la vez
        /// se le cuele por delante. Devuelve <see langword="false"/> si se perdió alguna carga retenida: no instala nada
        /// (la cola queda limpia) y hay que pedir otra copia inicial, que ya trae lo perdido.
        /// </summary>
        public bool TryAttach(CollabEngine target)
        {
            lock (_lock)
            {
                IReadOnlyList<Ordenado>? replay = _early.Drain();
                if (replay is null)
                {
                    return false;
                }

                _target = target;
                foreach (Ordenado o in replay)
                {
                    target.Apply(o);
                }

                return true;
            }
        }

        /// <summary>Quita el destino y tira lo retenido (la sala terminó: nada de otra sesión vale).</summary>
        public void Detach()
        {
            lock (_lock)
            {
                _target = null;
                _ = _early.Drain();
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
                if (target is null)
                {
                    _early.Add(ordered);
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
