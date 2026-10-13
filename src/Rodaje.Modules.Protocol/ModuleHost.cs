using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Rodaje.Modules.Protocol;

public delegate ValueTask<ModuleOutput> ModuleFunction(ModuleCall call, CancellationToken cancellationToken);

public sealed record ModuleOutput(JsonElement? Value = null, IReadOnlyList<SharedBufferRef>? Buffers = null);

/// <summary>Una llamada en curso dentro del módulo.</summary>
[SupportedOSPlatform("windows")]
public sealed class ModuleCall
{
    private readonly ModuleHost _host;
    private readonly MessageChannel _channel;
    private long _lastProgressTicks;
    private string? _lastStage;

    internal ModuleCall(ModuleHost host, MessageChannel channel, InvokeMessage invoke)
    {
        _host = host;
        _channel = channel;
        Id = invoke.Id;
        Function = invoke.Function;
        Parameters = invoke.Parameters;
        Buffers = invoke.Buffers ?? [];
    }

    public long Id { get; }

    public string Function { get; }

    public JsonElement? Parameters { get; }

    public IReadOnlyList<SharedBufferRef> Buffers { get; }

    /// <summary>La memoria de un búfer de la llamada, con los límites comprobados contra la región real.</summary>
    public Span<byte> Map(SharedBufferRef buffer) => _host.Region(buffer.Region).GetSpan(buffer);

    /// <summary>Avance de 0 a 1. Se envía como mucho cada 50 ms, salvo al cambiar de etapa o al llegar a 1.</summary>
    public async ValueTask ReportProgressAsync(double fraction, string? stage = null, CancellationToken cancellationToken = default)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        long now = Stopwatch.GetTimestamp();
        if (fraction < 1 && stage == _lastStage && _lastProgressTicks != 0 && Stopwatch.GetElapsedTime(_lastProgressTicks, now).TotalMilliseconds < 50)
            return;
        _lastProgressTicks = now;
        _lastStage = stage;
        await _channel.SendAsync(new ProgressMessage(Id, fraction, stage), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// El lado del módulo: atiende al editor por la tubería, ejecuta las funciones registradas en paralelo (hasta
/// <see cref="MaxConcurrentCalls"/>) y convierte excepciones en <see cref="ErrorMessage"/>. Termina cuando el editor
/// cierra la conexión, pide <c>shutdown</c> o manda algo malformado.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ModuleHost(ModuleInfo info, int minProtocol = ProtocolVersion.Current, int maxProtocol = ProtocolVersion.Current)
{
    public const string PipeArgument = "--rodaje-pipe";

    private readonly Dictionary<string, ModuleFunction> _functions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SharedMemoryRegion> _regions = new(StringComparer.Ordinal);

    public ModuleInfo Info { get; } = info.Validate() is { } reason ? throw new ArgumentException(reason, nameof(info)) : info;

    public int MaxConcurrentCalls { get; init; } = 4;

    /// <summary>
    /// Lo que se espera el <c>hello</c> del editor. 30 s (<see cref="ModuleClientOptions.DefaultHandshakeTimeout"/>, el
    /// mismo plazo que el editor da al saludo y que <c>ModuleManagerOptions.StartTimeout</c>): con 10 s, en un equipo
    /// cargado el módulo se rendía antes de recibir el saludo y el editor veía «Se cortó la conexión al saludar».
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = ModuleClientOptions.DefaultHandshakeTimeout;

    /// <summary>
    /// Costura de pruebas: corre cuando una llamada ya dejó libre su id y antes de que salga su respuesta final (el
    /// hueco de la tarea 211).
    /// </summary>
    internal Func<long, Task>? BeforeReplyForTests { get; init; }

    /// <summary>
    /// Costura de pruebas: corre en el hilo de arranque justo antes de arrancar el hilo propio de una llamada, donde
    /// <see cref="Thread.Start()"/> espera a que el hilo nuevo arranque (tareas 235 y 260).
    /// </summary>
    internal Action<long>? BeforeCallThreadStartsForTests { get; init; }

    /// <summary>
    /// Costura de pruebas: corre al final de la tarea de una llamada, cuando ya contestó o decidió no contestar (tarea 302).
    /// </summary>
    internal Action<long>? AfterCallForTests { get; init; }

    /// <summary>
    /// Costura de pruebas: corre en el cierre, ya agotada su espera, cuando <see cref="AnswerUnansweredAsync"/> reclamó las
    /// llamadas sin empezar y antes de enviar sus <c>cancelled</c> y esperar a las empezadas (tarea 303).
    /// </summary>
    internal Action? AfterUnansweredClaimedForTests { get; init; }

    /// <summary>Registra la función <paramref name="id"/>; tiene que estar en <see cref="ModuleInfo.Functions"/>.</summary>
    public void Register(string id, ModuleFunction function)
    {
        if (!Info.Functions.Any(f => f.Id == id)) throw new ArgumentException($"«{id}» no está en ModuleInfo.Functions.", nameof(id));
        _functions[id] = function;
    }

    /// <summary>Punto de entrada de un módulo: se conecta a la tubería que nombra <c>--rodaje-pipe</c>.</summary>
    /// <returns>Código de salida: 0 bien, 2 error de protocolo, 64 sin tubería.</returns>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        int at = Array.IndexOf(args, PipeArgument);
        if (at < 0 || at + 1 >= args.Length || !SharedMemoryRegion.IsValidName(args[at + 1]))
        {
            await Console.Error.WriteLineAsync($"Este programa es un módulo de Rodaje ({Info.Name}); lo abre el editor.").ConfigureAwait(false);
            return 64;
        }
        NamedPipeClientStream pipe = new(".", args[at + 1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            await Console.Error.WriteLineAsync("No se pudo conectar con el editor: " + ex.Message).ConfigureAwait(false);
            return 2;
        }
        return await RunAsync(pipe, cancellationToken).ConfigureAwait(false) ? 0 : 2;
    }

    /// <returns><c>false</c> si terminó por un error de protocolo o de versión.</returns>
    public async Task<bool> RunAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using MessageChannel channel = new(stream);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // «active»: las llamadas en curso por id, para «cancel», el id repetido y «busy»; una llamada sale de aquí al
        // terminar su función, así que su id queda libre antes de que salga la respuesta. «unanswered»: las que aún no
        // escribieron su respuesta final, que es lo que el cierre tiene que esperar (tarea 211).
        ConcurrentDictionary<long, CancellationTokenSource> active = new();
        ConcurrentDictionary<PendingReply, Task> unanswered = new();
        CallStarter starter = new(this);
        try
        {
            if (!await HandshakeAsync(channel, stop.Token).ConfigureAwait(false)) return false;
            while (true)
            {
                ModuleMessage? message = await channel.ReceiveAsync(stop.Token).ConfigureAwait(false);
                switch (message)
                {
                    case null:
                    case ShutdownMessage:
                        return true;
                    case InvokeMessage invoke:
                        await StartAsync(channel, invoke, active, unanswered, starter, stop.Token).ConfigureAwait(false);
                        break;
                    case CancelMessage cancel:
                        if (active.TryGetValue(cancel.Id, out CancellationTokenSource? cts)) await TryCancelAsync(cts).ConfigureAwait(false);
                        break;
                    case PingMessage ping:
                        await channel.SendAsync(new PongMessage(ping.Id), stop.Token).ConfigureAwait(false);
                        break;
                    default:
                        throw new ModuleProtocolException($"El editor no manda «{message.GetType().Name}».");
                }
            }
        }
        catch (ModuleProtocolException ex)
        {
            await TrySendAsync(channel, new ErrorMessage(ErrorCodes.Malformed, ex.Message), CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return true;
        }
        finally
        {
            starter.Complete();
            // Con la misma protección que «cancel»: una llamada que termina ahora ya desechó su CancellationTokenSource
            // (defecto 2 de ProtocolDefectTests). Cerrar nunca lanza; siempre se espera a las llamadas y se liberan las regiones.
            foreach (CancellationTokenSource cts in active.Values) await TryCancelAsync(cts).ConfigureAwait(false);
            bool finished = true;
            try
            {
                // Se espera a «unanswered» y no a «active»: una llamada cuya función ya terminó salió de «active» pero
                // puede no haber escrito aún su respuesta. Sin esperarla, el canal se cerraba debajo y la respuesta se
                // perdía en silencio (tarea 211: «La llamada 2 tuvo 0 respuestas finales», con «shutdown» detrás).
                await Task.WhenAll(unanswered.Values).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Una función que no atiende la cancelación, o un hilo de llamada que aún no arrancó (en la CI de Windows,
                // Thread.Start pasó de 10 s): sus regiones quedan mapeadas y el proceso sale igual.
                finished = false;
                await AnswerUnansweredAsync(channel, unanswered).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Al cerrar, el fallo de una llamada ya no tiene a quién llegar; todas terminaron.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Task.WhenAll solo lanza otra cosa cuando todas las tareas terminaron.
            }
            if (finished)
            {
                foreach (SharedMemoryRegion region in _regions.Values) region.Dispose();
                _regions.Clear();
            }
        }
    }

    internal SharedMemoryRegion Region(string name) => _regions.GetOrAdd(name, SharedMemoryRegion.Open);

    private async Task<bool> HandshakeAsync(MessageChannel channel, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        if (await channel.ReceiveAsync(timeout.Token).ConfigureAwait(false) is not HelloMessage hello)
            throw new ModuleProtocolException("Lo primero tiene que ser «hello».");
        int chosen = Math.Min(maxProtocol, hello.MaxProtocol);
        if (chosen < Math.Max(minProtocol, hello.MinProtocol))
        {
            await channel.SendAsync(new ErrorMessage(ErrorCodes.VersionIncompatible,
                $"El módulo habla el protocolo {minProtocol}–{maxProtocol} y el editor {hello.MinProtocol}–{hello.MaxProtocol}."), cancellationToken).ConfigureAwait(false);
            return false;
        }
        await channel.SendAsync(new WelcomeMessage(chosen, Info), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Lo que queda en <c>unanswered</c> cuando el cierre agota su espera se contesta antes de cerrar el canal (tarea 295).
    /// Antes se cerraba sin más y una llamada cuyo hilo aún no había arrancado, o cuya función no atendía la cancelación, se
    /// quedaba sin respuesta final: «6 de 300 llamadas quedaron sin respuesta final» en la CI de Windows. La que aún no
    /// empezó recibe <c>cancelled</c> y, cuando su hilo arranca, ya no corre su función ni contesta. La que ya empezó
    /// contesta ella (su función recibió la cancelación, y la respuesta sale con el plazo propio de
    /// <see cref="TrySendAsync"/>): se espera a que acabe. Lo decide un solo cambio atómico de <see cref="PendingReply"/>
    /// (tarea 302): antes la llamada miraba si el cierre ya había contestado una vez, antes de la función, y si el cierre
    /// contestaba justo después la función corría igual con el canal cerrado.
    /// <para>
    /// Todo esto tiene un tope total, <see cref="UnansweredReplyBudget"/> (tarea 303). Antes cada <c>cancelled</c> salía en
    /// serie con su propio plazo de 1 s y luego se esperaba sin plazo a las que ya habían empezado: con el editor sin leer y
    /// muchas llamadas sin arrancar, el cierre pasaba de 2 s a 1 s por llamada más, y una función que no atiende la
    /// cancelación lo dejaba colgado. Primero se reclaman todas las que no empezaron (un cambio atómico cada una, sin
    /// esperar), así ninguna corre su función aunque el tope corte los envíos; después se envía y se espera dentro del tope.
    /// </para>
    /// </summary>
    private async ValueTask AnswerUnansweredAsync(MessageChannel channel, ConcurrentDictionary<PendingReply, Task> unanswered)
    {
        List<long> cancelled = [];
        List<Task> replying = [];
        foreach ((PendingReply pending, Task call) in unanswered)
        {
            if (pending.TryCancel())
                cancelled.Add(pending.Id);
            else
                replying.Add(call);
        }
        AfterUnansweredClaimedForTests?.Invoke();
        using CancellationTokenSource budget = new(UnansweredReplyBudget);
        foreach (long id in cancelled)
            await TrySendAsync(channel, new ErrorMessage(ErrorCodes.Cancelled, "El módulo se cerró antes de terminar la llamada.", id), budget.Token).ConfigureAwait(false);
        try
        {
            await Task.WhenAll(replying).WaitAsync(budget.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Al cerrar, el fallo de una llamada ya no tiene a quién llegar; agotado el tope, se cierra igual.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>
    /// Tope total de <see cref="AnswerUnansweredAsync"/> (tarea 303): 2 s, lo mismo que el cierre ya espera a las llamadas
    /// antes de contestarlas, así el cierre entero queda acotado en unos 4 s como mucho. Con el editor leyendo, cada
    /// <c>cancelled</c> se escribe en microsegundos y el tope no corta nada; solo actúa cuando el editor no lee o una
    /// función no atiende la cancelación, y entonces la respuesta ya no llegaría de todos modos.
    /// </summary>
    private static readonly TimeSpan UnansweredReplyBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// La respuesta final de una llamada, que sale una sola vez (tareas 295 y 302). Tres estados, con cambios atómicos:
    /// <list type="bullet">
    /// <item><c>Pending</c> → <c>Started</c> (<see cref="TryStart"/>): la llamada, justo antes de su función. Si falla, el
    /// cierre ya la contestó: ni corre la función ni contesta.</item>
    /// <item><c>Pending</c> → <c>Replied</c> (<see cref="TryCancel"/>): el cierre, que entonces contesta <c>cancelled</c>.
    /// Si la llamada ya empezó, falla y el cierre la espera.</item>
    /// <item><c>Started</c> → <c>Replied</c> (<see cref="TryFinish"/>): la llamada que empezó, antes de escribir su
    /// respuesta.</item>
    /// </list>
    /// </summary>
    private sealed class PendingReply(long id)
    {
        private const int Pending = 0;
        private const int Started = 1;
        private const int Replied = 2;

        private int _state = Pending;

        public long Id { get; } = id;

        public bool TryStart() => Interlocked.CompareExchange(ref _state, Started, Pending) == Pending;

        public bool TryCancel() => Interlocked.CompareExchange(ref _state, Replied, Pending) == Pending;

        public bool TryFinish() => Interlocked.CompareExchange(ref _state, Replied, Started) == Started;
    }

    private async ValueTask StartAsync(MessageChannel channel, InvokeMessage invoke,
        ConcurrentDictionary<long, CancellationTokenSource> active, ConcurrentDictionary<PendingReply, Task> unanswered, CallStarter starter,
        CancellationToken stop)
    {
        if (active.ContainsKey(invoke.Id)) throw new ModuleProtocolException($"La llamada {invoke.Id} ya está en curso.");
        if (!_functions.TryGetValue(invoke.Function, out ModuleFunction? function))
        {
            await channel.SendAsync(new ErrorMessage(ErrorCodes.UnknownFunction, $"El módulo no tiene la función «{invoke.Function}».", invoke.Id), stop).ConfigureAwait(false);
            return;
        }
        if (active.Count >= MaxConcurrentCalls)
        {
            await channel.SendAsync(new ErrorMessage(ErrorCodes.Busy, "El módulo está ocupado.", invoke.Id), stop).ConfigureAwait(false);
            return;
        }
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        // En un hilo propio y no en el pool (tarea 195): una función que bloquea su hilo (cargar un modelo, inferir,
        // sintetizar) ocupaba un hilo del pool por llamada, y con varias a la vez y la CPU llena el pool no sumaba otros.
        // Entonces este bucle, que lee y contesta con E/S asíncrona (sus continuaciones corren en el pool), no contestaba
        // el «ping» ni salían los avances, y el vigilante del editor mataba un módulo sano. La tarea se crea sin arrancar
        // para que esté en «active» y en «unanswered» antes de que pueda terminar y sacarse de ahí.
        PendingReply key = new(invoke.Id);
        Task<Task> start = new(async () =>
        {
            ModuleMessage reply;
            try
            {
                // El cierre ya la contestó con «cancelled» (el hilo arrancó tarde): la conexión se cerró, no hay nada que
                // hacer. Si no, desde aquí el cierre ya no la contesta: la espera (tarea 302).
                if (!key.TryStart()) throw new OperationCanceledException();
                ModuleOutput output = await function(new ModuleCall(this, channel, invoke), cts.Token).ConfigureAwait(false);
                reply = new ResultMessage(invoke.Id, output.Value, output.Buffers);
                if (reply.Validate() is { } reason) reply = new ErrorMessage(ErrorCodes.Internal, "Resultado inválido: " + reason, invoke.Id);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                reply = new ErrorMessage(ErrorCodes.Cancelled, "Cancelado.", invoke.Id);
            }
            catch (ModuleException ex)
            {
                reply = new ErrorMessage(ex.Code, Trim(ex.Message), invoke.Id);
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InsufficientExecutionStackException)
            {
                reply = new ErrorMessage(ErrorCodes.OutOfMemory, Trim(ex.Message), invoke.Id);
            }
#pragma warning disable CA1031 // Toda excepción de una función se devuelve al editor; el módulo sigue atendiendo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                reply = new ErrorMessage(ErrorCodes.Internal, Trim($"{ex.GetType().Name}: {ex.Message}"), invoke.Id);
            }
            // El id queda libre ya (el editor puede reusarlo en cuanto lea la respuesta), pero la llamada sigue en
            // «unanswered» hasta que su respuesta está escrita: el cierre la espera.
            active.TryRemove(invoke.Id, out _);
            cts.Dispose();
            try
            {
                if (BeforeReplyForTests is { } hook) await hook(invoke.Id).ConfigureAwait(false);
                if (key.TryFinish()) await SendReplyAsync(channel, reply).ConfigureAwait(false);
            }
            finally
            {
                unanswered.TryRemove(key, out _);
                AfterCallForTests?.Invoke(invoke.Id);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning);
        active[invoke.Id] = cts;
        unanswered[key] = start.Unwrap();
        // El hilo no se arranca en este bucle (tarea 235) ni en el pool (tarea 260), sino en el hilo de arranque del anfitrión.
        starter.Start(start, invoke.Id);
    }

    /// <summary>
    /// Arranca los hilos propios de las llamadas de una conexión, de uno en uno, en un hilo suyo. <see cref="Thread.Start()"/>
    /// es síncrono: el runtime crea el hilo del sistema y espera, cediendo el procesador, a que el hilo nuevo se dé por
    /// arrancado, y en la CI de Windows esa espera llegó a pasar de 10 s.
    /// <list type="bullet">
    /// <item>En el bucle de <see cref="RunAsync(Stream, CancellationToken)"/> (tarea 235), el bucle se quedaba ahí con la
    /// llamada ya en «active» y en «unanswered» y no leía el «shutdown» que venía detrás ni el cierre del editor.</item>
    /// <item>En un trabajo del pool por llamada (#233, tarea 260), cada arranque lento ocupaba un hilo del pool: con 64
    /// llamadas y la CPU llena, el pool se llenaba de arranques en espera y la continuación de la lectura de la tubería
    /// (el fin de flujo al cerrar el editor) o el plazo del cierre esperaban detrás, en la cola.</item>
    /// </list>
    /// Aquí ni el bucle ni el cierre dependen del arranque: un hilo lento solo retrasa las llamadas que vienen detrás, y el
    /// cierre las espera con su plazo de 2 s y contesta con <c>cancelled</c> las que no llegaron a arrancar (tarea 295). El
    /// hilo de arranque se crea con la primera llamada y lo arranca un único trabajo del pool (el único arranque que espera
    /// ahí); termina cuando la conexión se cierra y ya arrancó lo pendiente.
    /// Si un hilo no se puede crear, la excepción termina el proceso del módulo, como antes salía de RunAsync.
    /// </summary>
    private sealed class CallStarter(ModuleHost host)
    {
        private readonly Queue<(Task<Task> Call, long Id)> _pending = new();
        private bool _completed;
        private bool _created;

        /// <summary>Encola la llamada para arrancarla. Solo desde el bucle, antes de <see cref="Complete"/>.</summary>
        public void Start(Task<Task> call, long id)
        {
            bool create;
            lock (_pending)
            {
                _pending.Enqueue((call, id));
                Monitor.Pulse(_pending);
                create = !_created;
                _created = true;
            }
            if (create)
            {
                Thread thread = new(Run) { IsBackground = true, Name = "Rodaje: arranque de llamadas" };
                ThreadPool.UnsafeQueueUserWorkItem(static t => t.Start(), thread, preferLocal: false);
            }
        }

        /// <summary>No llegan más llamadas: el hilo arranca lo que quede y termina.</summary>
        public void Complete()
        {
            lock (_pending)
            {
                _completed = true;
                Monitor.Pulse(_pending);
            }
        }

        private void Run()
        {
            while (true)
            {
                (Task<Task> Call, long Id) next;
                lock (_pending)
                {
                    while (_pending.Count == 0)
                    {
                        if (_completed) return;
                        Monitor.Wait(_pending);
                    }
                    next = _pending.Dequeue();
                }
                host.BeforeCallThreadStartsForTests?.Invoke(next.Id);
                next.Call.Start(TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Corre un trabajo que bloquea su hilo (inferencia, síntesis, revelado) en un hilo propio y no en el pool. Es lo que
    /// usa un módulo en vez de <see cref="Task.Run(Func{Task})"/> para el trabajo largo: con la CPU llena el pool no suma
    /// hilos, y si el trabajo lo ocupa, el bucle de <see cref="ModuleHost"/> no contesta el <c>ping</c> ni manda los
    /// avances y el editor mata un módulo sano (tarea 195; antes, solo «RAW de cine» en la tarea 138).
    /// </summary>
    /// <remarks>
    /// Si <paramref name="cancellationToken"/> ya está cancelado, el trabajo no empieza. Llamada desde el pool (tras un
    /// <c>await</c>, como las oleadas de Visión), el hilo no se arranca ahí sino en el hilo de arranque de los trabajos
    /// largos (tarea 289), y la tarea se devuelve sin esperar a que arranque; véase <see cref="BlockingStarter"/>.
    /// </remarks>
    public static Task<T> RunBlockingAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        Task<T> task = new(work, cancellationToken, TaskCreationOptions.LongRunning);
        if (BlockingStarter.TryStartHere(task)) return task;

        // Desde el pool la devuelta es un reflejo del trabajo: si su arranque falla de forma imprevista, falla ella
        // (una tarea creada sin arrancar no se puede fallar desde fuera) y el hilo de arranque sigue (tarea 300).
        TaskCompletionSource<Task<T>> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingStarter.Enqueue(task, error =>
        {
            if (error is null) started.TrySetResult(task);
            else started.TrySetException(error);
        });
        return started.Task.Unwrap();
    }

    /// <inheritdoc cref="RunBlockingAsync{T}(Func{T}, CancellationToken)"/>
    public static Task RunBlockingAsync(Action work, CancellationToken cancellationToken = default)
    {
        Task task = new(work, cancellationToken, TaskCreationOptions.LongRunning);
        if (BlockingStarter.TryStartHere(task)) return task;

        TaskCompletionSource<Task> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BlockingStarter.Enqueue(task, error =>
        {
            if (error is null) started.TrySetResult(task);
            else started.TrySetException(error);
        });
        return started.Task.Unwrap();
    }

    /// <summary>
    /// Costura de pruebas: corre justo antes de arrancar el hilo de un trabajo de <see cref="RunBlockingAsync(Action, CancellationToken)"/>,
    /// donde <see cref="Thread.Start()"/> espera a que el hilo nuevo arranque (tarea 289). Por hilo: la lee el hilo que
    /// llama a <c>RunBlockingAsync</c> y la corre el hilo que arranca el trabajo, así cada prueba ve solo sus arranques.
    /// </summary>
    [ThreadStatic]
    internal static Action? BeforeBlockingThreadStartsForTests;

    /// <summary>
    /// Arranca los hilos de <see cref="RunBlockingAsync(Action, CancellationToken)"/> pedidos desde el pool, de uno en uno,
    /// en un hilo suyo (uno por proceso), como <see cref="CallStarter"/> con los hilos de las llamadas (tarea 260) y
    /// <c>FrameBatch</c> de RAW con los de revelado (#262). <see cref="Thread.Start()"/> espera a que el hilo nuevo arranque,
    /// y en la CI de Windows esa espera pasó de 10 s: hecho en el pool, cada trabajo pedido tras un <c>await</c> retenía un
    /// hilo del pool, y con las oleadas de Visión (hasta 4 trabajos por llamada y varias llamadas a la vez) el bucle de
    /// <see cref="ModuleHost"/> no contestaba el <c>ping</c> ni mandaba los avances y el vigilante del editor mataba el
    /// módulo (tarea 289). Desde un hilo que no es del pool (el hilo propio de una llamada, antes de su primer
    /// <c>await</c>) el hilo se arranca allí mismo, como antes: no le quita nada al pool. Un arranque lento solo retrasa a
    /// los trabajos que vienen detrás. El hilo de arranque se crea con el primer trabajo pedido desde el pool y lo arranca
    /// un único trabajo del pool (el único arranque que espera ahí); no termina nunca: una excepción imprevista al arrancar
    /// un trabajo falla solo la tarea de ese trabajo y el hilo sigue con los demás (tarea 300).
    /// </summary>
    private static class BlockingStarter
    {
        private static readonly Queue<(Task Work, Action? BeforeStart, Action<Exception?> Settle)> s_pending = new();
        private static bool s_created;

        /// <summary>
        /// Arranca el trabajo en el hilo que llama si no es del pool, o no hace nada si ya terminó (el token estaba
        /// cancelado). Devuelve <see langword="false"/> si hay que pedirlo al hilo de arranque con <see cref="Enqueue"/>.
        /// </summary>
        public static bool TryStartHere(Task work)
        {
            // Con el token ya cancelado, la tarea nace cancelada: no hay hilo que arrancar.
            if (work.IsCompleted) return true;
            if (Thread.CurrentThread.IsThreadPoolThread) return false;

            BeforeBlockingThreadStartsForTests?.Invoke();
            try
            {
                work.Start(TaskScheduler.Default);
            }
            catch (InvalidOperationException) when (work.IsCompleted)
            {
                // El token se canceló justo antes de arrancar: se devuelve la tarea cancelada, como hacía StartNew (tarea 300).
            }

            return true;
        }

        /// <summary>
        /// Pide el arranque al hilo de arranque. <paramref name="settle"/> recibe <see langword="null"/> cuando el trabajo
        /// arrancó o ya había terminado, o la excepción que impidió arrancarlo.
        /// </summary>
        public static void Enqueue(Task work, Action<Exception?> settle)
        {
            Action? beforeStart = BeforeBlockingThreadStartsForTests;
            bool create;
            lock (s_pending)
            {
                s_pending.Enqueue((work, beforeStart, settle));
                Monitor.Pulse(s_pending);
                create = !s_created;
                s_created = true;
            }

            if (create)
            {
                Thread thread = new(Run) { IsBackground = true, Name = "Rodaje: arranque de trabajos largos" };
                ThreadPool.UnsafeQueueUserWorkItem(static t => t.UnsafeStart(), thread, preferLocal: false);
            }
        }

        private static void Run()
        {
            while (true)
            {
                (Task Work, Action? BeforeStart, Action<Exception?> Settle) next;
                lock (s_pending)
                {
                    while (s_pending.Count == 0) Monitor.Wait(s_pending);
                    next = s_pending.Dequeue();
                }

                Exception? error = null;
                try
                {
                    next.BeforeStart?.Invoke();
                    next.Work.Start(TaskScheduler.Default);
                }
                catch (Exception) when (next.Work.IsCompleted)
                {
                    // Se canceló mientras esperaba en la cola (InvalidOperationException), o el hilo no se pudo crear
                    // (TaskSchedulerException, con la tarea ya fallada): la tarea ya dice cómo terminó.
                }
                catch (Exception e)
                {
                    // Cualquier otra cosa falla solo este trabajo; el hilo sigue con los demás (tarea 300).
                    error = e;
                }

                next.Settle(error);
            }
        }
    }

    /// <summary>
    /// Cancela una llamada que puede estar terminando: su tarea la saca de <c>active</c> y desecha su
    /// <see cref="CancellationTokenSource"/> en su propio hilo, así que entre leerla de <c>active</c> y cancelarla puede
    /// quedar desechada. Entonces ya no hay nada que cancelar; la <see cref="ObjectDisposedException"/> no debe cortar
    /// la conexión (defecto 1 de <c>ProtocolDefectTests</c>).
    /// </summary>
    private static async ValueTask TryCancelAsync(CancellationTokenSource cts)
    {
        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // La llamada ya terminó.
        }
    }

    private static string Trim(string text) => text.Length <= Limits.MaxText ? text : text[..(Limits.MaxText - 1)] + "…";

    /// <summary>
    /// La respuesta final de una llamada. Si un <c>result</c> no se puede escribir (más de 1 MiB, o un valor que no se
    /// puede serializar), la llamada se contesta con un <c>error</c> <c>internal</c>: nunca queda sin respuesta en
    /// silencio (defecto 3 de <c>ProtocolDefectTests</c>).
    /// </summary>
    private static async ValueTask SendReplyAsync(MessageChannel channel, ModuleMessage reply)
    {
        if (await TrySendAsync(channel, reply).ConfigureAwait(false) is { } reason && reply is ResultMessage result)
            await TrySendAsync(channel, new ErrorMessage(ErrorCodes.Internal, Trim("No se pudo enviar el resultado: " + reason), result.Id)).ConfigureAwait(false);
    }

    /// <param name="limit">Un tope de fuera que puede cortar antes del plazo propio de 1 s (el del cierre, tarea 303).</param>
    /// <returns>El motivo si el mensaje no se puede escribir (nada llegó al cable); <c>null</c> si se envió o si el editor ya no escucha.</returns>
    private static async ValueTask<string?> TrySendAsync(MessageChannel channel, ModuleMessage message, CancellationToken limit = default)
    {
        // Con tiempo límite: si el editor no lee, el módulo no se queda esperando para siempre.
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(limit);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await channel.SendAsync(message, timeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is ModuleProtocolException or JsonException or ArgumentException)
        {
            // MessageChannel.SendAsync serializa y mide antes de tomar el candado: el mensaje no salió.
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // El editor ya no escucha.
            return null;
        }
    }
}
