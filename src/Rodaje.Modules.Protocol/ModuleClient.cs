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

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lo que se espera a que el módulo confirme una cancelación antes de darlo por colgado y cortar.</summary>
    public TimeSpan CancelGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Lo que puede tardar en salir un mensaje hacia el módulo una vez que le toca escribir. Si el módulo deja de leer
    /// la tubería, al vencer se corta la conexión, para que ninguna llamada ni <see cref="ModuleClient.DisposeAsync"/>
    /// se quede esperando.
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
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
    /// <see cref="ModuleClientOptions.CancelGrace"/>; si no llega, corta la conexión (el módulo se da por colgado).
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
            if (await Task.WhenAny(pending.Completion.Task, Task.Delay(_options.CancelGrace, CancellationToken.None)).ConfigureAwait(false) != pending.Completion.Task)
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

    /// <summary>Pide al módulo que termine y cierra la conexión.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_failure is null)
        {
            try
            {
                await SendAsync(new ShutdownMessage()).ConfigureAwait(false);
            }
            catch (ModuleException)
            {
                // Ya se había cortado.
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
        _channel.Dispose();
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
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HandshakeTimeout);
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
                    Fail(new ModuleDisconnectedException("El módulo cerró la conexión."));
                    return;
                }
                Dispatch(message);
            }
        }
        catch (ModuleException ex)
        {
            Fail(ex);
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
