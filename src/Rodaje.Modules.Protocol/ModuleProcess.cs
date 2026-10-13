using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace Rodaje.Modules.Protocol;

/// <summary>
/// Abre un módulo como proceso aparte, le pasa el nombre de una tubería nueva (solo del usuario actual) y se conecta.
/// Si el proceso muere, el cliente falla con <see cref="ModuleExitedException"/>; si el cliente se corta (mensaje
/// hostil, módulo colgado), el proceso se mata. <see cref="DisposeAsync"/> pide <c>shutdown</c> y mata si no sale.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ModuleProcess : IAsyncDisposable
{
    private const int StderrTail = 2048;
    internal const int PipeBufferBytes = 64 * 1024;

    private readonly Process _process;
    private readonly StringBuilder _stderr = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ModuleProcess(Process process, ModuleClient client)
    {
        _process = process;
        Client = client;
    }

    public ModuleClient Client { get; }

    public int ProcessId => _process.Id;

    /// <summary>El código de salida cuando el proceso termina.</summary>
    public Task<int> Exited => _exited.Task;

    /// <param name="fileName">El ejecutable del módulo (<c>module.entry</c> del paquete).</param>
    /// <param name="started">Se llama con el proceso recién creado, antes del saludo (por ejemplo, para meterlo en un Job Object).</param>
    public static async Task<ModuleProcess> StartAsync(string fileName, IEnumerable<string>? arguments = null,
        ModuleClientOptions? options = null, Action<Process>? started = null, CancellationToken cancellationToken = default)
    {
        options ??= new ModuleClientOptions();
        string pipeName = SharedMemoryRegion.NamePrefix + "pipe." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferBytes, PipeBufferBytes);

        ProcessStartInfo start = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(fileName)) ?? "",
        };
        foreach (string a in arguments ?? []) start.ArgumentList.Add(a);
        start.ArgumentList.Add(ModuleHost.PipeArgument);
        start.ArgumentList.Add(pipeName);

        Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        StringBuilder stderr = new();
        TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
                if (stderr.Length > StderrTail) stderr.Remove(0, stderr.Length - StderrTail);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(process));
        try
        {
            if (!process.Start()) throw new ModuleExitedException("No se pudo abrir el módulo.");
            started?.Invoke(process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw new ModuleExitedException("No se pudo abrir el módulo: " + ex.Message, ex);
        }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        ModuleClient client;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.HandshakeTimeout);
            Task connect = pipe.WaitForConnectionAsync(timeout.Token);
            if (await Task.WhenAny(connect, exited.Task).ConfigureAwait(false) == exited.Task)
                throw new ModuleExitedException(exited.Task.Result, Tail(stderr));
            try
            {
                await connect.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModuleProtocolException($"El módulo no se conectó en {options.HandshakeTimeout.TotalSeconds:0.#} s.");
            }
            client = await ModuleClient.ConnectAsync(pipe, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            await KillOffPool(() => Kill(process), TaskScheduler.Default).ConfigureAwait(false);
            process.Dispose();
            throw;
        }

        ModuleProcess module = new(process, client);
        lock (stderr) module._stderr.Append(stderr);
        _ = exited.Task.ContinueWith(t =>
        {
            module._exited.TrySetResult(t.Result);
            client.Fail(new ModuleExitedException(t.Result, Tail(stderr)));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = KillWhenFaulted(client.Completion, () => Kill(process), TaskScheduler.Default);
        return module;
    }

    /// <summary>
    /// Mata el proceso cuando <paramref name="completion"/> falla. Matar el árbol de procesos puede tardar segundos con la
    /// CPU llena: nunca en línea con <c>Fail</c>, para que las llamadas fallen enseguida y el proceso muera después. Sin
    /// ExecuteSynchronously no depende de cómo complete el cliente su Completion. En un hilo propio y no en el pool: si
    /// Fail corre en un hilo del pool, las continuaciones de las llamadas y esta iban a la cola local de ese hilo, que
    /// toma primero la última (LIFO) y se quedaba matando mientras las llamadas esperaban a que otro hilo libre las
    /// robara. Con OnlyOnFaulted, un cierre limpio cancela la continuación sin programarla: no crea ese hilo.
    /// </summary>
    internal static Task KillWhenFaulted(Task completion, Action kill, TaskScheduler scheduler) =>
        completion.ContinueWith(_ => kill(), CancellationToken.None,
            TaskContinuationOptions.LongRunning | TaskContinuationOptions.OnlyOnFaulted, scheduler);

    /// <summary>
    /// Mata el árbol de procesos del módulo sin esperar: lo mata un hilo propio (<see cref="KillOffPool"/>) y vuelve
    /// enseguida. La muerte se ve en <see cref="Exited"/>. Quien llama suele ser una continuación en el pool (la salida
    /// del proceso o el vigilante de ping de la app), que antes se quedaba bloqueada los segundos que tarda matar el árbol
    /// con la CPU llena.
    /// </summary>
    public void Kill()
    {
        Process process = _process;
        _ = KillOffPool(() => Kill(process), TaskScheduler.Default);
    }

    /// <summary>
    /// Corre <paramref name="kill"/> en un hilo propio (<see cref="TaskCreationOptions.LongRunning"/>) y no en el pool:
    /// <see cref="Process.Kill(bool)"/> recorre y mata el árbol de procesos en línea, y con la CPU llena tarda segundos,
    /// que un hilo del pool pasaba bloqueado (en <see cref="DisposeAsync"/>, en el fallo del arranque y en
    /// <see cref="Kill()"/>). Quien necesite esperar la muerte espera la tarea, que nunca falla (<c>Kill</c> traga lo
    /// que lanza un proceso que ya terminó).
    /// </summary>
    internal static Task KillOffPool(Action kill, TaskScheduler scheduler) =>
        Task.Factory.StartNew(kill, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, scheduler);

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        if (await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false) != _exited.Task)
        {
            await KillOffPool(() => Kill(_process), TaskScheduler.Default).ConfigureAwait(false);
            await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
        _process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Ya terminó.
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static string Tail(StringBuilder stderr)
    {
        lock (stderr) return stderr.ToString();
    }
}
