namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Regresión de la tarea 189 (lo pendiente de la 137): en el cierre normal, <see cref="ModuleClient.DisposeAsync"/> mandaba
/// <c>shutdown</c> y enseguida cerraba el asa del editor. En Windows, cerrar el lado servidor con una lectura propia
/// pendiente (el bucle de lectura del cliente) y datos que el módulo aún no leyó deja a veces la lectura del módulo sin
/// completar nunca (lo que mostró <see cref="EditorCloseTests"/>): el módulo no veía el <c>shutdown</c> y
/// <see cref="ModuleProcess"/> lo mataba a los 2 s en vez de verlo salir con 0. Desconectar antes tampoco vale: descarta
/// el <c>shutdown</c> sin leer. Ahora el cliente espera a que el módulo cierre su lado y solo después cierra el suyo.
/// </summary>
/// <remarks>
/// La condición depende de cómo programe Windows las lecturas de la tubería y no se da en Linux (allí la tubería es un
/// socket). Para que la prueba sea determinista en las dos, el editor y el módulo hablan por <see cref="WindowsLikePipe"/>,
/// que hace siempre lo peor que hace Windows: lo que escribe el editor no le llega al módulo hasta que el editor se queda
/// esperando (la costura <c>ModuleClient.AwaitingModuleClose</c>), y si el editor cierra antes, se pierde y la lectura del
/// módulo no termina nunca. Sin el arreglo, cada ronda se cuelga (el tope de <see cref="FuzzSupport.Capped(Task, string)"/>);
/// con él, el módulo lee el <c>shutdown</c>, termina limpio y el editor cierra después. Corre con la CPU llena, como en la CI.
/// La versión con el proceso de verdad es <c>ProcessTests.Echo_module_exits_with_0_on_shutdown_with_the_cpu_full</c>.
/// </remarks>
[Collection(FuzzGroup.Name)]
public sealed class ShutdownCloseTests
{
    private const int Rounds = 20;

    [Fact]
    public async Task The_module_reads_the_shutdown_and_exits_cleanly_before_the_editor_closes()
    {
        using CancellationTokenSource burn = new();
        List<Thread> burners = [];
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            Thread burner = new(() =>
            {
                while (!burn.IsCancellationRequested) Thread.SpinWait(1000);
            }) { IsBackground = true, Name = "Carga de CPU" };
            burner.Start();
            burners.Add(burner);
        }
        try
        {
            await FuzzSupport.RunCasesAsync(Rounds, RoundAsync, first: 189_000, parallel: 2);
        }
        finally
        {
            await burn.CancelAsync();
            foreach (Thread burner in burners) burner.Join();
        }
    }

    [Fact]
    public async Task A_module_that_does_not_close_after_shutdown_is_closed_after_the_grace()
    {
        TaskCompletionSource<string?> shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            // Lee el «shutdown» y no cierra: el editor no puede esperarlo para siempre.
            shutdown.TrySetResult(await Support.ReadFrameAsync(module));
            await Support.ReadFrameAsync(module);
        }, options: new ModuleClientOptions { ShutdownGrace = TimeSpan.FromMilliseconds(200) });
        await client.DisposeAsync().AsTask().WaitAsync(Support.CiBound);
        Assert.Contains("\"shutdown\"", await shutdown.Task.WaitAsync(Support.CiBound), StringComparison.Ordinal);
        Assert.True(client.Completion.IsCompletedSuccessfully);
    }

    private static async Task RoundAsync(int seed)
    {
        WindowsLikePipe pipe = new();
        using CancellationTokenSource hostStop = new();
        Task<bool> host = Support.EchoHost().RunAsync(pipe.Module, hostStop.Token);
        try
        {
            ModuleClient client = await FuzzSupport.Capped(ModuleClient.ConnectAsync(pipe.Editor), $"el saludo (semilla {seed})");
            await FuzzSupport.Capped(client.InvokeAsync("eco", Support.Json("{\"a\":1}")), $"la llamada (semilla {seed})");

            pipe.HoldEditorWrites();
            client.AwaitingModuleClose = pipe.ReleaseEditorWrites;
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), $"DisposeAsync (semilla {seed})");

            // El módulo terminó por el «shutdown» (en esta tubería, un cierre del editor nunca le da fin de flujo).
            Assert.True(await FuzzSupport.Capped(host, $"RunAsync tras «shutdown» (semilla {seed}): el módulo no lo leyó"));
            Assert.True(pipe.ModuleClosedFirst, "el editor cerró su lado antes que el módulo");
            // Cierre limpio: ModuleProcess no mata al módulo (KillWhenFaulted solo mata si Completion falla).
            Assert.True(client.Completion.IsCompletedSuccessfully, $"Completion: {client.Completion.Status} {client.Completion.Exception?.InnerException?.Message}");
        }
        finally
        {
            await hostStop.CancelAsync();
        }
    }
}

/// <summary>
/// Una tubería en memoria que hace lo peor de una tubería con nombre de Windows al cerrar el editor (véase
/// <see cref="ShutdownCloseTests"/>). Mientras <see cref="HoldEditorWrites"/> está activo, lo que escribe el editor queda
/// retenido hasta <see cref="ReleaseEditorWrites"/>. Si el editor cierra su lado, lo que el módulo no leyó se pierde y sus
/// lecturas no terminan nunca (solo con su token de cancelación). Si el módulo cierra el suyo, el editor lee fin de flujo.
/// </summary>
internal sealed class WindowsLikePipe
{
    private readonly object _lock = new();
    private readonly Queue<byte> _toModule = new();
    private readonly List<byte> _held = [];
    private readonly Queue<byte> _toEditor = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _holding;
    private bool _editorClosed;
    private bool _moduleClosed;

    public WindowsLikePipe()
    {
        Editor = new End(this, isEditor: true);
        Module = new End(this, isEditor: false);
    }

    public Stream Editor { get; }

    public Stream Module { get; }

    /// <summary>El módulo cerró su lado antes que el editor.</summary>
    public bool ModuleClosedFirst { get; private set; }

    public void HoldEditorWrites()
    {
        lock (_lock) _holding = true;
    }

    public void ReleaseEditorWrites()
    {
        lock (_lock)
        {
            _holding = false;
            if (!_editorClosed)
            {
                foreach (byte b in _held) _toModule.Enqueue(b);
            }
            _held.Clear();
            Pulse();
        }
    }

    private void Pulse()
    {
        TaskCompletionSource changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    private void Write(bool fromEditor, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(fromEditor ? _editorClosed : _moduleClosed, this);
            if (fromEditor ? _moduleClosed : _editorClosed) throw new IOException("Pipe is broken.");
            if (fromEditor && _holding) _held.AddRange(data);
            else
            {
                Queue<byte> to = fromEditor ? _toModule : _toEditor;
                foreach (byte b in data) to.Enqueue(b);
            }
            Pulse();
        }
    }

    private async ValueTask<int> ReadAsync(bool byEditor, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(byEditor ? _editorClosed : _moduleClosed, this);
                Queue<byte> from = byEditor ? _toEditor : _toModule;
                // El editor cerró: la lectura del módulo se queda pendiente para siempre, como en Windows.
                bool blocked = !byEditor && _editorClosed;
                if (!blocked && from.Count > 0)
                {
                    int n = Math.Min(buffer.Length, from.Count);
                    Span<byte> span = buffer.Span;
                    for (int i = 0; i < n; i++) span[i] = from.Dequeue();
                    return n;
                }
                if (byEditor && _moduleClosed) return 0;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Close(bool editor)
    {
        lock (_lock)
        {
            if (editor)
            {
                if (_editorClosed) return;
                _editorClosed = true;
                // Lo que el módulo no leyó se pierde.
                _toModule.Clear();
                _held.Clear();
            }
            else
            {
                if (_moduleClosed) return;
                _moduleClosed = true;
                if (!_editorClosed) ModuleClosedFirst = true;
            }
            Pulse();
        }
    }

    private sealed class End(WindowsLikePipe pipe, bool isEditor) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            pipe.ReadAsync(isEditor, buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pipe.Write(isEditor, buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => pipe.Write(isEditor, buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            pipe.Close(isEditor);
            base.Dispose(disposing);
        }
    }
}
