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

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

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
        ConcurrentDictionary<long, (CancellationTokenSource Cts, Task Task)> active = new();
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
                        await StartAsync(channel, invoke, active, stop.Token).ConfigureAwait(false);
                        break;
                    case CancelMessage cancel:
                        if (active.TryGetValue(cancel.Id, out var call)) await call.Cts.CancelAsync().ConfigureAwait(false);
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
            await TrySendAsync(channel, new ErrorMessage(ErrorCodes.Malformed, ex.Message)).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return true;
        }
        finally
        {
            foreach (var call in active.Values) await call.Cts.CancelAsync().ConfigureAwait(false);
            bool finished = true;
            try
            {
                await Task.WhenAll(active.Values.Select(c => c.Task)).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Una función que no atiende la cancelación: sus regiones quedan mapeadas y el proceso sale igual.
                finished = false;
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

    private async ValueTask StartAsync(MessageChannel channel, InvokeMessage invoke,
        ConcurrentDictionary<long, (CancellationTokenSource Cts, Task Task)> active, CancellationToken stop)
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
        TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task = Task.Run(async () =>
        {
            await registered.Task.ConfigureAwait(false);
            ModuleMessage reply;
            try
            {
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
            active.TryRemove(invoke.Id, out _);
            cts.Dispose();
            await TrySendAsync(channel, reply).ConfigureAwait(false);
        }, CancellationToken.None);
        active[invoke.Id] = (cts, task);
        registered.SetResult();
    }

    private static string Trim(string text) => text.Length <= Limits.MaxText ? text : text[..(Limits.MaxText - 1)] + "…";

    private static async ValueTask TrySendAsync(MessageChannel channel, ModuleMessage message)
    {
        // Con tiempo límite: si el editor no lee, el módulo no se queda esperando para siempre.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
        try
        {
            await channel.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ModuleProtocolException or OperationCanceledException)
        {
            // El editor ya no escucha.
        }
    }
}
