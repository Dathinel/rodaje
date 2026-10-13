using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Rodaje.Modules.Protocol;

public sealed record ModuleClientOptions
{
    public string App { get; init; } = "rodaje";

    public string AppVersion { get; init; } = "0.0.0";

    public string? Culture { get; init; }

    public int MinProtocol { get; init; } = ProtocolVersion.Current;

    public int MaxProtocol { get; init; } = ProtocolVersion.Current;

    /// <summary>
    /// El plazo de fábrica del saludo, compartido por los dos lados: lo que el editor espera a que el módulo se conecte y,
    /// después, a que conteste <c>welcome</c> (<see cref="HandshakeTimeout"/>), y lo que el módulo espera el <c>hello</c>
    /// (<c>ModuleHost.HandshakeTimeout</c>). 30 s porque el arranque en frío de un módulo (antivirus con un exe recién
    /// copiado, arranque de .NET, CI cargada) pasa de 10 s; con 10 s en el editor y 30 s en el módulo, un módulo sano se
    /// daba por roto con «El módulo no saludó en 10 s» (tarea 205).
    /// </summary>
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Lo que se espera a que el módulo se conecte (<see cref="ModuleProcess"/>) y, aparte, a que conteste el saludo. Por
    /// omisión <see cref="DefaultHandshakeTimeout"/>, nunca menos que lo que el módulo espera el <c>hello</c>.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = DefaultHandshakeTimeout;

    /// <summary>
    /// El plazo de fábrica para confirmar una cancelación (<see cref="CancelGrace"/>). Una función solo puede confirmarla
    /// al llegar a su siguiente punto de cancelación, y entre dos puntos hay pasos que no se interrumpen: una etapa de
    /// LibRaw al revelar un cuadro RAW (la interpolación AHD de un cuadro grande), una inferencia de ONNX Runtime en Visión
    /// o un tramo de Whisper en Subtítulos. Con la CPU llena esos pasos pasan de 2 s, y con 2 s el editor daba por colgado
    /// un módulo sano: cortaba la conexión, mataba el proceso (con los modelos cargados) y fallaba las demás llamadas en
    /// curso de ese módulo con «El módulo no confirmó la cancelación en 2 s» (tarea 221, CI de Windows en
    /// <c>Rodaje.Module.Raw.Tests.ProcessTests.Cancel_is_confirmed_quickly</c>). 10 s, lo mismo que el vigilante del
    /// editor tolera un módulo sin señal de vida (<c>ModuleManagerOptions.PingTimeout</c>) y la cota de las pruebas de
    /// cancelación de los módulos en la CI. Un módulo de verdad colgado sigue muriendo, solo que a los 10 s.
    /// </summary>
    public static readonly TimeSpan DefaultCancelGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lo que se espera a que el módulo confirme una cancelación antes de darlo por colgado y cortar. Por omisión
    /// <see cref="DefaultCancelGrace"/>; las pruebas que ejercen el corte lo bajan a propósito.
    /// </summary>
    public TimeSpan CancelGrace { get; init; } = DefaultCancelGrace;

    /// <summary>
    /// Lo que puede tardar en salir un mensaje hacia el módulo una vez que le toca escribir. Si el módulo deja de leer
    /// la tubería, al vencer se corta la conexión, para que ninguna llamada ni <see cref="ModuleClient.DisposeAsync"/>
    /// se quede esperando.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lo que <see cref="ModuleClient.DisposeAsync"/> espera, tras mandar <c>shutdown</c>, a que el módulo cierre su lado
    /// de la conexión antes de cerrar el del editor. Más que los 2 s que el módulo da a sus llamadas al cerrar
    /// (<c>ModuleHost.RunAsync</c>), que cierra la tubería después.
    /// </summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// El reloj de <see cref="HandshakeTimeout"/> y <see cref="CancelGrace"/>. Las pruebas ponen uno manual para comprobar
    /// esos plazos de fábrica sin esperarlos (tarea 244).
    /// </summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
}

public readonly record struct ModuleProgress(double Fraction, string? Stage);

public sealed record ModuleResult(JsonElement? Value, IReadOnlyList<SharedBufferRef> Buffers);

/// <summary>
/// El lado del editor. Desconfía del módulo: un mensaje malformado, una respuesta a una llamada que no existe o un búfer
/// fuera de las regiones del editor cortan la conexión y fallan todas las llamadas con <see cref="ModuleException"/>;
/// el editor sigue. Las regiones de memoria compartida se crean aquí (<see cref="CreateRegion"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ModuleClient : IAsyncDisposable
{
    private readonly MessageChannel _channel;
    private readonly ModuleClientOptions _options;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _pings = new();
    private readonly ConcurrentDictionary<string, SharedMemoryRegion> _regions = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private Exception? _failure;
    private long _nextId;
    private long _received;
    private volatile bool _shutdownSent;
    private Task _readLoop = Task.CompletedTask;

    private ModuleClient(MessageChannel channel, ModuleClientOptions options)
    {
        _channel = channel;
        _options = options;
    }

    public int Protocol { get; private set; }

    public ModuleInfo Module { get; private set; } = null!;

    /// <summary>Termina cuando se cierra la conexión; con excepción si fue por un fallo.</summary>
    public Task Completion => _completion.Task;

    public event Action<LogMessage>? Log;

    /// <summary>
    /// Mensajes recibidos del módulo tras el saludo (avances, registros, resultados, errores, <c>pong</c>). Es la señal de
    /// vida del vigilante del editor: un módulo ocupado que sigue avisando no está colgado aunque su <c>pong</c> se atrase.
    /// </summary>
    public long ReceivedMessages => Interlocked.Read(ref _received);

    /// <summary>Saluda y negocia la versión.</summary>
    /// <exception cref="ModuleVersionException">Sin versión común.</exception>
    /// <exception cref="ModuleProtocolException">El módulo no respondió como debe o a tiempo.</exception>
    public static async Task<ModuleClient> ConnectAsync(Stream stream, ModuleClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ModuleClientOptions();
        ModuleClient client = new(new MessageChannel(stream), options);
        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client._channel.Dispose();
            throw;
        }
        client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
        return client;
    }

    /// <summary>Crea una región de memoria compartida que vive mientras viva el cliente (o hasta que se descarte).</summary>
    public SharedMemoryRegion CreateRegion(long capacity)
    {
        SharedMemoryRegion region = SharedMemoryRegion.Create(capacity);
        _regions[region.Name] = region;
        return region;
    }

    public void ReleaseRegion(SharedMemoryRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (_regions.TryRemove(region.Name, out _)) region.Dispose();
    }

    /// <summary>
    /// Ejecuta <paramref name="function"/>. Si se cancela, avisa al módulo y espera su confirmación hasta
    /// <see cref="ModuleClientOptions.CancelGrace"/> (10 s por omisión, <see cref="ModuleClientOptions.DefaultCancelGrace"/>);
    /// si no llega, corta la conexión (el módulo se da por colgado).
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelada.</exception>
    /// <exception cref="ModuleCallException">La función falló; la conexión sigue.</exception>
    /// <exception cref="ModuleDisconnectedException">El módulo se cerró o murió.</exception>
    public async Task<ModuleResult> InvokeAsync(string function, JsonElement? parameters = null, IReadOnlyList<SharedBufferRef>? buffers = null,
        IProgress<ModuleProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        cancellationToken.ThrowIfCancellationRequested();
        long id = Interlocked.Increment(ref _nextId);
        InvokeMessage invoke = new(id, function, parameters, buffers);
        if (invoke.Validate() is { } reason) throw new ArgumentException(reason);
        foreach (SharedBufferRef b in buffers ?? [])
            if (!_regions.ContainsKey(b.Region)) throw new ArgumentException($"La región {b.Region} no es de este cliente.");

        Pending pending = new(progress);
        _pending[id] = pending;
        try
        {
            await SendAsync(invoke, cancellationToken).ConfigureAwait(false);
            try
            {
                return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !pending.Completion.Task.IsCompleted)
            {
            }

            // El token del llamador ya está cancelado: el aviso sale con el de la conexión y el plazo de escritura.
            await SendAsync(new CancelMessage(id), CancellationToken.None).ConfigureAwait(false);
            if (await Task.WhenAny(pending.Completion.Task, Task.Delay(_options.CancelGrace, _options.Time, CancellationToken.None)).ConfigureAwait(false) != pending.Completion.Task)
                Fail(new ModuleDisconnectedException($"El módulo no confirmó la cancelación en {_options.CancelGrace.TotalSeconds:0.#} s."));
            try
            {
                return await pending.Completion.Task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ModuleException or OperationCanceledException)
            {
                throw new OperationCanceledException("Cancelado.", ex, cancellationToken);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <returns>El tiempo de ida y vuelta.</returns>
    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        long id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pings[id] = pong;
        long start = Stopwatch.GetTimestamp();
        try
        {
            await SendAsync(new PingMessage(id), cancellationToken).ConfigureAwait(false);
            await pong.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return Stopwatch.GetElapsedTime(start);
        }
        finally
        {
            _pings.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Costura de pruebas: se llama cuando <see cref="DisposeAsync"/> ya mandó <c>shutdown</c> y empieza a esperar que el
    /// módulo cierre su lado (<c>ShutdownCloseTests</c>).
    /// </summary>
    internal Action? AwaitingModuleClose { get; set; }

    /// <summary>
    /// Si <see cref="DisposeAsync"/> llegó a mandar <c>shutdown</c>. <see cref="ModuleProcess"/> lo mira para dejar en el
    /// registro un módulo que, tras pedírselo, salió mal o hubo que matar (tarea 200).
    /// </summary>
    internal bool ShutdownDelivered { get; private set; }

    /// <summary>
    /// Pide al módulo que termine (<c>shutdown</c>), espera hasta <see cref="ModuleClientOptions.ShutdownGrace"/> a que
    /// cierre su lado y solo entonces cierra el del editor.
    /// </summary>
    /// <remarks>
    /// Cerrar enseguida tras mandar <c>shutdown</c> (como hasta la tarea 189) perdía a veces el mensaje en Windows: con
    /// una lectura propia pendiente (el bucle de lectura) y el <c>shutdown</c> aún sin leer por el módulo, cerrar el asa
    /// del lado servidor deja la lectura del módulo sin completar nunca (lo que mostró la tarea 137), y
    /// <see cref="ModuleProcess"/> lo mataba a los 2 s en vez de verlo salir con 0. Desconectar antes tampoco sirve:
    /// descarta lo que el módulo no leyó, también el <c>shutdown</c>. Esperar al fin de flujo del módulo (lo da el bucle
    /// de lectura, o la salida del proceso en <see cref="ModuleProcess"/>, que corta el cliente) garantiza que lo leyó.
    /// Ese fin de flujo, tras <c>shutdown</c>, es un cierre limpio: <see cref="Completion"/> termina sin excepción.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_failure is null)
        {
            bool sent = false;
            // Antes de mandar: el módulo puede leerlo y cerrar antes de que SendAsync vuelva.
            _shutdownSent = true;
            try
            {
                await SendAsync(new ShutdownMessage()).ConfigureAwait(false);
                sent = true;
                ShutdownDelivered = true;
            }
            catch (ModuleException)
            {
                // Ya se había cortado.
            }
            if (sent)
            {
                // Las llamadas en curso fallan ya, como al cerrar sin esperar: no dependen de lo que el módulo conteste.
                ModuleDisconnectedException closing = new("Conexión cerrada por el editor.");
                foreach (Pending p in _pending.Values) p.Completion.TrySetException(closing);
                foreach (TaskCompletionSource p in _pings.Values) p.TrySetException(closing);
                AwaitingModuleClose?.Invoke();
                try
                {
                    await _readLoop.WaitAsync(_options.ShutdownGrace).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // El módulo no cerró a tiempo: se cierra igual y ModuleProcess lo mata si no sale.
                }
            }
        }
        Fail(null);
        await _readLoop.ConfigureAwait(false);
        foreach (SharedMemoryRegion region in _regions.Values) region.Dispose();
        _regions.Clear();
        _stop.Dispose();
    }

    /// <summary>Corta la conexión desde fuera (por ejemplo, porque el proceso murió).</summary>
    internal void Fail(Exception? reason)
    {
        Exception error = reason ?? new ModuleDisconnectedException("Conexión cerrada por el editor.");
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null) return;
        _stop.Cancel();
        // Un fallo corta sin cierre ordenado: desconectar para que el módulo vea el fin aunque tenga datos sin leer
        // (MessageChannel.Abort, tarea 137). El cierre de DisposeAsync, tras «shutdown», solo cierra.
        if (reason is null) _channel.Dispose();
        else _channel.Abort();
        // Completion antes que las llamadas. Si Fail corre en un hilo del pool (el bucle de lectura, la salida del proceso
        // en ModuleProcess), cada continuación que suelta va a la cola local de ese hilo, que al terminar toma primero la
        // última (LIFO); otro hilo libre solo puede robar la primera. Con Completion al final, el hilo tomaba antes lo que
        // cuelga de ella (en ModuleConnection, matar el árbol de procesos, que tarda segundos) y las llamadas esperaban a
        // que alguien las robara: sin hilos libres, no fallaban. Así el propio hilo termina antes las llamadas.
        if (reason is null) _completion.TrySetResult();
        else _completion.TrySetException(error);
        foreach (Pending p in _pending.Values) p.Completion.TrySetException(error);
        foreach (TaskCompletionSource p in _pings.Values) p.TrySetException(error);
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource limit = new(_options.HandshakeTimeout, _options.Time);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await _channel.SendAsync(new HelloMessage(_options.MinProtocol, _options.MaxProtocol, _options.App, _options.AppVersion, _options.Culture), timeout.Token).ConfigureAwait(false);
            switch (await _channel.ReceiveAsync(timeout.Token).ConfigureAwait(false))
            {
                case WelcomeMessage welcome when welcome.Protocol >= _options.MinProtocol && welcome.Protocol <= _options.MaxProtocol:
                    Protocol = welcome.Protocol;
                    Module = welcome.Module;
                    return;
                case WelcomeMessage welcome:
                    throw new ModuleVersionException($"El módulo eligió el protocolo {welcome.Protocol}; el editor habla {_options.MinProtocol}–{_options.MaxProtocol}.");
                case ErrorMessage { Code: ErrorCodes.VersionIncompatible } error:
                    throw new ModuleVersionException(error.Message);
                case null:
                    throw new ModuleDisconnectedException("El módulo cerró la conexión al saludar.");
                default:
                    throw new ModuleProtocolException("El módulo no respondió al saludo con «welcome».");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModuleProtocolException($"El módulo no saludó en {_options.HandshakeTimeout.TotalSeconds:0.#} s.");
        }
        catch (IOException ex)
        {
            throw new ModuleDisconnectedException("Se cortó la conexión al saludar.", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                ModuleMessage? message = await _channel.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (message is null)
                {
                    // Tras «shutdown», que el módulo cierre es lo esperado (DisposeAsync).
                    Fail(_shutdownSent ? null : new ModuleDisconnectedException("El módulo cerró la conexión."));
                    return;
                }
                Interlocked.Increment(ref _received);
                Dispatch(message);
            }
        }
        catch (ModuleException ex)
        {
            Fail(ex);
        }
        catch (IOException) when (_shutdownSent)
        {
            // El módulo cerró tras «shutdown» y la tubería lo dio como corte en vez de fin de flujo: sigue siendo limpio.
            Fail(null);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fail(new ModuleDisconnectedException("Se cortó la conexión con el módulo.", ex));
        }
    }

    private void Dispatch(ModuleMessage message)
    {
        switch (message)
        {
            case ProgressMessage p:
                if (_pending.TryGetValue(p.Id, out Pending? pp)) pp.Progress?.Report(new ModuleProgress(p.Fraction, p.Stage));
                break;
            case ResultMessage r:
                if (!_pending.TryGetValue(r.Id, out Pending? pr)) break;
                IReadOnlyList<SharedBufferRef> buffers = r.Buffers ?? [];
                foreach (SharedBufferRef b in buffers)
                {
                    if (!_regions.TryGetValue(b.Region, out SharedMemoryRegion? region) || b.Offset > region.Capacity - b.Length)
                        throw new ModuleProtocolException("El módulo devolvió un búfer fuera de las regiones del editor.");
                }
                pr.Completion.TrySetResult(new ModuleResult(r.Value, buffers));
                break;
            case ErrorMessage { Id: { } id } e:
                if (!_pending.TryGetValue(id, out Pending? pe)) break;
                pe.Completion.TrySetException(e.Code == ErrorCodes.Cancelled
                    ? new OperationCanceledException(e.Message)
                    : new ModuleCallException(e.Code, e.Message));
                break;
            case ErrorMessage e:
                throw new ModuleProtocolException("El módulo informó un error de conexión: " + e.Message);
            case PongMessage pong:
                if (_pings.TryGetValue(pong.Id, out TaskCompletionSource? tcs)) tcs.TrySetResult();
                break;
            case LogMessage log:
                Log?.Invoke(log);
                break;
            default:
                throw new ModuleProtocolException($"El módulo no manda «{message.GetType().Name}».");
        }
    }

    /// <summary>
    /// Escribe con el token del llamador enlazado a <c>_stop</c> y con <see cref="ModuleClientOptions.WriteTimeout"/>
    /// (defecto 5 de <c>ProtocolDefectTests</c>). Cancelar mientras se espera el turno no cambia nada; cancelar a mitad de
    /// la escritura deja el marco cortado, así que la conexión falla. Si vence el plazo, el módulo dejó de leer: falla.
    /// </summary>
    private async ValueTask SendAsync(ModuleMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfFailed();
        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            await _channel.SendAsync(message, _options.WriteTimeout, linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            Fail(new ModuleDisconnectedException($"El módulo dejó de leer: un mensaje no salió en {_options.WriteTimeout.TotalSeconds:0.#} s.", ex));
            ThrowIfFailed();
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Quien canceló recibe su cancelación aunque, con el flujo ya cerrado, el bucle de lectura haya fallado antes.
            if (_channel.Broken) Fail(new ModuleDisconnectedException("Se canceló un mensaje a mitad de la escritura; la conexión ya no es fiable.", ex));
            throw new OperationCanceledException("Cancelado.", ex, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Fail(new ModuleDisconnectedException("Se cortó la conexión con el módulo.", ex));
            ThrowIfFailed();
            throw;
        }
        finally
        {
            linked?.Dispose();
        }
    }

    private void ThrowIfFailed()
    {
        if (_failure is { } f) throw f is ModuleException m ? Rethrowable(m) : f;
    }

    // Cada llamada recibe su propia instancia para que la traza no se mezcle entre hilos.
    private static ModuleException Rethrowable(ModuleException e) => e switch
    {
        ModuleExitedException x => x.ExitCode is { } code ? new ModuleExitedException(code, null) : new ModuleExitedException(x.Message),
        ModuleDisconnectedException => new ModuleDisconnectedException(e.Message, e),
        ModuleProtocolException => new ModuleProtocolException(e.Message, e),
        ModuleVersionException => new ModuleVersionException(e.Message, e),
        _ => new ModuleException(e.Code, e.Message, e),
    };

    private sealed class Pending(IProgress<ModuleProgress>? progress)
    {
        public IProgress<ModuleProgress>? Progress { get; } = progress;

        public TaskCompletionSource<ModuleResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
