using System.Text.Json;
using Dathinel.Sala;

namespace Rodaje.App.Services.Collab;

/// <summary>Lo que <see cref="RoomService"/> necesita del equipo.</summary>
/// <param name="RedPath">Ruta de dathinel-red.</param>
/// <param name="IdentityFile">Dónde se guarda la clave de este equipo (cifrada para el usuario de Windows).</param>
/// <param name="AppVersion">La versión de Rodaje: solo entran las iguales.</param>
public sealed record CollabEnvironment(string RedPath, string IdentityFile, string AppVersion)
{
    /// <summary>Cómo sale a la red: los relés públicos de n0 por defecto; las pruebas y la red local usan <see cref="Red.SinRele"/>.</summary>
    public Red Red { get; init; } = Red.N0;

    public int MaxMembers { get; init; } = 8;

    /// <summary>Tiempo máximo para que el anfitrión conteste una carga; el de Sala por defecto es 30 s.</summary>
    public TimeSpan? ValidateTimeout { get; init; }
}

/// <summary>Una invitación recién creada.</summary>
public sealed record CollabInvitation(string Link, DateTimeOffset ExpiresAt, bool OneUse);

/// <summary>
/// Hospeda o se une a una sala de Sala y la deja como un <see cref="RoomState"/> inmutable. No toca el proyecto: las cargas
/// pasan por <see cref="ICollabPayloadHandler"/>. Todos los métodos públicos solo lanzan <see cref="CollabException"/> (con
/// el texto para la persona) o <see cref="OperationCanceledException"/>; lo que llega de la red nunca lanza hacia fuera.
/// </summary>
public sealed class RoomService : IAsyncDisposable
{
    private readonly CollabEnvironment _env;
    private readonly ICollabPayloadHandler _handler;
    private readonly Action<Action> _post;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private RoomState _state = RoomState.Closed;
    private Dathinel.Sala.Sala? _sala;
    private string _folder = "";
    private string _lastAnchored = "";
    private readonly HashSet<string> _offline = new(StringComparer.Ordinal);

    /// <param name="post">
    /// Cómo se avisa a la interfaz de un cambio (en la app, la cola del hilo de interfaz). Por omisión se avisa en el mismo
    /// hilo que lo provoca, que es el de Sala.
    /// </param>
    public RoomService(CollabEnvironment env, ICollabPayloadHandler? handler = null, Action<Action>? post = null, Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(env);
        _env = env;
        _handler = handler ?? new NoEditingPayloadHandler();
        _post = post ?? (a => a());
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public RoomState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>La sala de Sala, para la sesión de edición (reservas, presencia, envío). Nulo sin sala.</summary>
    internal Dathinel.Sala.Sala? Sala
    {
        get { lock (_gate) return _sala; }
    }

    public event Action<RoomState>? StateChanged;

    public event Action<RoomNotice>? Noticed;

    /// <summary>Un mensaje directo de otro miembro (fuera del orden): quién lo manda y su contenido. No debe bloquear.</summary>
    public event Action<string, JsonElement>? DirectReceived;

    /// <summary>Llegó la presencia de otro miembro (cabezal, selección): su clave y el objeto. No debe bloquear.</summary>
    public event Action<string, JsonElement>? PresenceReceived;

    /// <summary>Cambió quién tiene una reserva: la clave (<c>pista:&lt;id&gt;</c>) y el dueño (<c>null</c>: quedó libre).</summary>
    public event Action<string, string?>? ReservationChanged;

    /// <summary>Avance de una descarga de blob: su huella y los bytes recibidos.</summary>
    public event Action<string, long>? BlobProgress;

    /// <summary>La carpeta de la sala de esta copia del proyecto; vacía sin sala.</summary>
    public string RoomFolder => _folder;

    /// <summary>La clave de quien hospeda la sala (la propia, si es este equipo); vacía sin sala.</summary>
    public string HostKey => State.Members.FirstOrDefault(m => m.IsHost)?.Key ?? "";

    // ---------- abrir ----------

    /// <summary>Abre la sala de este proyecto. <paramref name="folder"/> es la carpeta de sala del proyecto (dentro van los anclados).</summary>
    public async Task HostAsync(string alias, string folder, CancellationToken cancellationToken = default)
    {
        string name = CheckAlias(alias);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureFree();
            CheckReady(folder);
            Publish(s => s with { Phase = RoomPhase.Starting, IsHost = true, MyAlias = name, Problem = null });
            try
            {
                string identity = await Task.Run(() => Identidad.CargarOCrear(_env.IdentityFile), cancellationToken).ConfigureAwait(false);
                IReadOnlyList<Miembro> anchored = AnchoredStore.Load(folder);
                _folder = folder;
                await Dathinel.Sala.Sala.HospedarAsync(identity, Options(name, folder), anchored, ValidateAsync, suscribir: Subscribe, ct: cancellationToken).ConfigureAwait(false);
                Publish(s => Refreshed(s) with { Phase = RoomPhase.Hosting });
            }
            catch (Exception ex)
            {
                await CloseAfterFailureAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw Wrap(ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Entra con lo que la persona pegó. Vuelve cuando el anfitrión la deja pasar (hasta 10 minutos) y llegó la bienvenida.</summary>
    public async Task JoinAsync(string inviteText, string alias, string folder, CancellationToken cancellationToken = default)
    {
        InviteProblem problem = CollabInvite.TryParse(inviteText, _now(), out InviteInfo? invite);
        if (problem != InviteProblem.None || invite is null)
        {
            throw new CollabException(CollabMessages.ForInvite(problem));
        }

        string name = CheckAlias(alias);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureFree();
            CheckReady(folder);
            Publish(s => s with { Phase = RoomPhase.Starting, IsHost = false, MyAlias = name, Problem = null });
            try
            {
                string identity = await Task.Run(() => Identidad.CargarOCrear(_env.IdentityFile), cancellationToken).ConfigureAwait(false);
                _folder = folder;
                await Dathinel.Sala.Sala.UnirseAsync(identity, Options(name, folder), invite.Ticket, suscribir: Subscribe, validar: ValidateAsync, ct: cancellationToken).ConfigureAwait(false);
                Publish(s => Refreshed(s) with { Phase = RoomPhase.Connected });
            }
            catch (Exception ex)
            {
                await CloseAfterFailureAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw Wrap(ex);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Sale de la sala (o la cierra, si es el anfitrión). Sin sala no hace nada.</summary>
    public async Task LeaveAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            Dathinel.Sala.Sala? sala;
            lock (_gate)
            {
                sala = _sala;
                _sala = null;
            }

            if (sala is not null)
            {
                await DisposeQuietlyAsync(sala).ConfigureAwait(false);
            }

            Publish(_ => RoomState.Closed);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>La sala se cierra por algo que Rodaje decidió (la copia dejó de coincidir): pasa a «terminada» con el motivo.</summary>
    public async Task EndWithAsync(string text)
    {
        Dathinel.Sala.Sala? sala;
        lock (_gate)
        {
            sala = _sala;
            _sala = null;
        }

        Publish(st => st with { Phase = RoomPhase.Ended, Requests = [], Problem = text });
        Notify(text, true);
        if (sala is not null)
        {
            await DisposeQuietlyAsync(sala).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await LeaveAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    // ---------- anfitrión ----------

    public async Task<CollabInvitation> CreateInviteAsync(TimeSpan validity, bool oneUse = true, CancellationToken cancellationToken = default)
    {
        Dathinel.Sala.Sala sala = HostRoom();
        if (validity < TimeSpan.FromMinutes(1) || validity > TimeSpan.FromDays(7))
        {
            throw new CollabException("La invitación puede durar entre 1 minuto y 7 días.");
        }

        try
        {
            string ticket = await sala.NuevoBilleteAsync(validity, oneUse, cancellationToken).ConfigureAwait(false);
            return new CollabInvitation(CollabInvite.ToLink(ticket), _now() + validity, oneUse);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>Anula todas las invitaciones sin usar. Los que ya entraron siguen dentro.</summary>
    public Task RevokeInvitesAsync(CancellationToken cancellationToken = default) =>
        Guard(() => HostRoom().RevocarAsync(null, cancellationToken));

    public Task ApproveAsync(string key, Rol role, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            Dathinel.Sala.Sala sala = HostRoom();
            RoomRequest? request = State.Requests.FirstOrDefault(r => r.Key == key)
                ?? throw new CollabException("Esa persona ya no está esperando. Si quiere entrar, que pida la invitación otra vez.");
            await sala.AceptarAsync(request.Key, role, cancellationToken).ConfigureAwait(false);
            Publish(s => Refreshed(s with { Requests = [.. s.Requests.Where(r => r.Key != key)] }));
        });

    public Task RejectAsync(string key, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            Dathinel.Sala.Sala sala = HostRoom();
            await sala.RechazarAsync(key, cancellationToken).ConfigureAwait(false);
            Publish(s => s with { Requests = [.. s.Requests.Where(r => r.Key != key)] });
        });

    public Task KickAsync(string key, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await HostRoom().ExpulsarAsync(key, cancellationToken).ConfigureAwait(false);
            Publish(Refreshed);
        });

    public Task SetRoleAsync(string key, Rol role, CancellationToken cancellationToken = default) =>
        Guard(async () =>
        {
            await HostRoom().CambiarRolAsync(key, role, cancellationToken).ConfigureAwait(false);
            Publish(Refreshed);
        });

    // ---------- los dos ----------

    /// <summary>Propone una carga (un parche) a la sala; la respuesta llega a <see cref="ICollabPayloadHandler"/>.</summary>
    public async Task<Guid> SendAsync(JsonElement payload, IEnumerable<string>? reservations = null, CancellationToken cancellationToken = default)
    {
        Dathinel.Sala.Sala sala;
        lock (_gate)
        {
            sala = _sala ?? throw new CollabException("No hay ninguna sala abierta.");
            if (_state.MyRole != Rol.Editar)
            {
                throw new CollabException(CollabMessages.Rejected("rol", null));
            }
        }

        try
        {
            return await sala.EnviarAsync(payload, reservations, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Wrap(ex);
        }
    }

    /// <summary>Manda un mensaje fuera del orden (al anfitrión, o a un miembro si este equipo hospeda).</summary>
    public Task SendDirectAsync(JsonElement payload, string? to = null, CancellationToken cancellationToken = default) =>
        Guard(() => Active().EnviarDirectoAsync(payload, to, cancellationToken));

    /// <summary>Ofrece un archivo propio (dentro de la carpeta de la sala) por su SHA-256 en minúsculas.</summary>
    public Task OfferBlobAsync(string path, string hash, long size, CancellationToken cancellationToken = default) =>
        Guard(() => Active().OfrecerBlobsAsync([(path, hash, size)], cancellationToken));

    public Task RetractBlobAsync(string hash) => Guard(() => Active().RetirarBlobsAsync([hash]));

    /// <summary>Baja un blob que otro miembro ofreció. Sala comprueba tamaño y SHA-256 y lo deja en <paramref name="destination"/>.</summary>
    public Task RequestBlobAsync(string from, string hash, long size, string destination, long limit, CancellationToken cancellationToken = default) =>
        Guard(() => Active().PedirBlobAsync(from, hash, size, destination, limit, cancellationToken));

    /// <summary>Manda la presencia propia (≤ 4 KiB, ≤ 20 por segundo; Sala descarta lo de más).</summary>
    public Task SendPresenceAsync(JsonElement presence, CancellationToken cancellationToken = default) =>
        Guard(() => Active().PresenciaAsync(presence, cancellationToken));

    /// <summary>
    /// Pide la reserva de una pista u otra etiqueta: <c>true</c> si es nuestra (estaba libre o ya la teníamos). Una clave que
    /// Sala no admite devuelve <c>false</c> sin lanzar: la edición sigue, sin candado.
    /// </summary>
    public async Task<bool> ReserveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!Dathinel.Sala.Sala.ClaveDeReservaValida(key))
        {
            return false;
        }

        bool mine = false;
        await Guard(async () => mine = await Active().ReservarAsync(key, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return mine;
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default) =>
        Dathinel.Sala.Sala.ClaveDeReservaValida(key) ? Guard(() => Active().SoltarAsync(key, cancellationToken)) : Task.CompletedTask;

    /// <summary>Quién tiene cada reserva que este equipo conoce (clave → clave del dueño).</summary>
    public IReadOnlyDictionary<string, string> Reservations
    {
        get
        {
            Dathinel.Sala.Sala? sala;
            lock (_gate)
            {
                sala = _sala;
            }

            return sala?.Reservas ?? new Dictionary<string, string>();
        }
    }

    private Dathinel.Sala.Sala Active()
    {
        lock (_gate)
        {
            return _sala ?? throw new CollabException("No hay ninguna sala abierta.");
        }
    }

    // ---------- eventos de Sala ----------

    private void Subscribe(Dathinel.Sala.Sala s)
    {
        lock (_gate)
        {
            _sala = s;
        }

        _ = Task.Run(() => WatchMembersAsync(s));
        s.SolicitudRecibida += r => OnSala(s, () => Publish(st => st with
        {
            Requests = [.. st.Requests.Where(x => x.Key != r.Clave), new RoomRequest(r.Clave, r.Alias, r.Huella)],
        }));
        s.SolicitudRetirada += k => OnSala(s, () => Publish(st => st with { Requests = [.. st.Requests.Where(x => x.Key != k)] }));
        s.MiembroEntra += m => OnSala(s, () =>
        {
            lock (_gate)
            {
                _offline.Remove(m.Clave);
            }

            Publish(Refreshed);
            Notify(CollabMessages.Joined(m.Alias), false);
        });
        s.MiembroSale += (k, motivo) => OnSala(s, () =>
        {
            string alias = State.Members.FirstOrDefault(x => x.Key == k)?.Alias ?? "Alguien";
            lock (_gate)
            {
                _offline.Add(k);
            }

            Publish(Refreshed);
            Notify(CollabMessages.Left(alias, motivo), false);
        });
        s.Ordenada += o => OnSala(s, () => _handler.Apply(o));
        s.Rechazada += r => OnSala(s, () =>
        {
            _handler.Rejected(r);
            Notify(CollabMessages.Rejected(r.Motivo, r.Detalle), true);
        });
        s.DirectoRecibido += d => OnSala(s, () => DirectReceived?.Invoke(d.De, d.Datos));
        s.BlobAvance += (hash, bytes) => OnSala(s, () => BlobProgress?.Invoke(hash, bytes));
        s.PresenciaRecibida += (k, d) => OnSala(s, () => PresenceReceived?.Invoke(k, d));
        s.ReservaCambia += (k, owner) => OnSala(s, () => ReservationChanged?.Invoke(k, owner));
        s.Desconectada += () => OnSala(s, () =>
        {
            if (State.Phase == RoomPhase.Connected)
            {
                Publish(st => st with { Phase = RoomPhase.Reconnecting });
                Notify("Se perdió la conexión con quien hospeda la sala. Tus cambios se guardan aquí y se enviarán al volver. Rodaje reintenta solo.", true);
            }
        });
        s.Reconectada += () => OnSala(s, () =>
        {
            Publish(st => Refreshed(st) with { Phase = RoomPhase.Connected });
            Notify("Se restableció la conexión.", false);
        });
        s.Cerrada += motivo => OnSala(s, () => _ = EndAsync(s, CollabMessages.ForClosed(motivo)));
        // Estuvo fuera tanto que la sala ya no guarda lo que se perdió: hace falta una copia nueva del proyecto.
        s.Desfasada += _ => OnSala(s, () => _ = EndAsync(s, CollabMessages.ForClosed("desfasada")));
        s.Superada += (_, _) => OnSala(s, () => _ = EndAsync(s, CollabMessages.ForClosed("traspaso")));
    }

    /// <summary>
    /// Sala no avisa cuando el anfitrión cambia un rol o la lista de miembros (el invitado la aplica sin evento): se mira
    /// cada medio segundo y solo se avisa si algo cambió. <c>Miembros</c> es una copia de 16 elementos como mucho.
    /// </summary>
    private async Task WatchMembersAsync(Dathinel.Sala.Sala s)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            lock (_gate)
            {
                if (!ReferenceEquals(s, _sala))
                {
                    return;
                }
            }

            OnSala(s, () =>
            {
                RoomState before = State;
                if (before.Phase is RoomPhase.Starting or RoomPhase.Ended or RoomPhase.Closed)
                {
                    return;
                }

                Publish(Refreshed);
                RoomState after = State;
                if (!ReferenceEquals(before, after) && before.Members.Count > 0 && before.MyRole != after.MyRole)
                {
                    Notify(CollabMessages.RoleChanged(after.MyAlias, after.MyRole, me: true), false);
                }
            });
        }
    }

    /// <summary>Todo lo que llega de Sala pasa por aquí: ignora una sala ya cerrada y no deja salir ninguna excepción.</summary>
    private void OnSala(Dathinel.Sala.Sala s, Action action)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(s, _sala))
            {
                return;
            }
        }

        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notify(CollabMessages.ForUnexpected(), true);
        }
    }

    private async Task EndAsync(Dathinel.Sala.Sala s, string text)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(s, _sala))
            {
                return;
            }

            _sala = null;
        }

        Publish(st => st with { Phase = RoomPhase.Ended, Requests = [], Problem = text });
        Notify(text, true);
        await DisposeQuietlyAsync(s).ConfigureAwait(false);
    }

    private async ValueTask<Veredicto> ValidateAsync(Miembro author, JsonElement payload, CancellationToken ct)
    {
        try
        {
            return await _handler.ValidateAsync(author, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Veredicto.No("no-valido", "El cambio no es válido.");
        }
    }

    // ---------- estado ----------

    /// <summary>Vuelve a leer quién hay en la sala; el anfitrión guarda además a los que ya dejó entrar.</summary>
    private RoomState Refreshed(RoomState s)
    {
        Dathinel.Sala.Sala? sala;
        lock (_gate)
        {
            sala = _sala;
        }

        if (sala is null)
        {
            return s;
        }

        IReadOnlyList<Miembro> raw = sala.Miembros;
        string me = sala.Clave;
        string host = sala.Anfitrion;
        List<RoomMember> members =
        [
            .. raw
                .Select(m => new RoomMember(m.Clave, m.Alias, m.Rol, m.Clave == host, m.Clave == me, Huella.Palabras(m.Clave), MemberColors.IndexFor(m.Clave), !_offline.Contains(m.Clave) || m.Clave == me))
                .OrderByDescending(m => m.IsHost).ThenByDescending(m => m.IsMe).ThenBy(m => m.Alias, StringComparer.CurrentCultureIgnoreCase),
        ];
        if (sala.EsAnfitrion)
        {
            SaveAnchored(raw.Where(m => m.Clave != me));
        }

        if (s.IsHost == sala.EsAnfitrion && s.MyKey == me && s.MyRole == sala.Rol && s.Members.SequenceEqual(members))
        {
            return s;
        }

        return s with { IsHost = sala.EsAnfitrion, MyKey = me, MyRole = sala.Rol, Members = members };
    }

    private void SaveAnchored(IEnumerable<Miembro> others)
    {
        List<Miembro> list = [.. others.OrderBy(m => m.Clave, StringComparer.Ordinal)];
        string snapshot = string.Join('\n', list.Select(m => $"{m.Clave}|{m.Alias}|{m.Rol}"));
        if (snapshot == _lastAnchored || _folder.Length == 0)
        {
            return;
        }

        _lastAnchored = snapshot;
        AnchoredStore.Save(_folder, list);
    }

    private void Publish(Func<RoomState, RoomState> change)
    {
        RoomState next;
        lock (_gate)
        {
            next = change(_state);
            if (ReferenceEquals(next, _state))
            {
                return;
            }

            _state = next;
        }

        Raise(() => StateChanged?.Invoke(next));
    }

    private void Notify(string text, bool problem)
    {
        RoomNotice notice = new(_now(), text, problem);
        Raise(() => Noticed?.Invoke(notice));
    }

    private void Raise(Action action)
    {
        try
        {
            _post(action);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // La interfaz ya no está (ventana cerrándose): el estado queda guardado en State.
        }
    }

    // ---------- comprobaciones y errores ----------

    private OpcionesSala Options(string alias, string folder)
    {
        OpcionesSala o = new("rodaje", _env.AppVersion, alias, _env.RedPath, Path.GetFullPath(folder))
        {
            Red = _env.Red,
            MaxMiembros = _env.MaxMembers,
        };
        return _env.ValidateTimeout is { } t ? o with { TopeValidar = t } : o;
    }

    private static string CheckAlias(string alias) =>
        CollabNames.NormalizeAlias(alias)
        ?? throw new CollabException($"Escribe tu nombre en la sala (de 1 a {CollabNames.MaxAlias} letras).");

    private void EnsureFree()
    {
        lock (_gate)
        {
            if (_sala is not null || _state.Phase is RoomPhase.Hosting or RoomPhase.Connected or RoomPhase.Reconnecting)
            {
                throw new CollabException("Ya estás en una sala. Sal de ella antes de empezar otra.");
            }
        }
    }

    private void CheckReady(string folder)
    {
        if (!File.Exists(_env.RedPath))
        {
            throw new CollabException(CollabMessages.For(new SalaException("sin-red", "")));
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new CollabException("No se pudo usar esa carpeta para la sala. Elige otra, por ejemplo una dentro de Documentos.", ex);
        }
    }

    private Dathinel.Sala.Sala HostRoom()
    {
        lock (_gate)
        {
            if (_sala is null || !_sala.EsAnfitrion)
            {
                throw new CollabException("Esto solo lo puede hacer quien hospeda la sala.");
            }

            return _sala;
        }
    }

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Wrap(ex);
        }
    }

    private static CollabException Wrap(Exception ex)
    {
        string text = ex switch
        {
            CollabException c => c.Message,
            SalaException s => CollabMessages.For(s),
            OperationCanceledException => "Quien hospeda la sala no respondió a tiempo. Comprueba que Rodaje sigue abierto allí y vuelve a probar.",
            _ => CollabMessages.ForUnexpected(),
        };
        return ex as CollabException ?? new CollabException(text, ex);
    }

    private async Task CloseAfterFailureAsync()
    {
        Dathinel.Sala.Sala? sala;
        lock (_gate)
        {
            sala = _sala;
            _sala = null;
        }

        if (sala is not null)
        {
            await DisposeQuietlyAsync(sala).ConfigureAwait(false);
        }

        Publish(_ => RoomState.Closed);
    }

    private static async Task DisposeQuietlyAsync(Dathinel.Sala.Sala sala)
    {
        try
        {
            await sala.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cerrar una sala ya rota no tiene nada más que decir.
        }
    }
}
