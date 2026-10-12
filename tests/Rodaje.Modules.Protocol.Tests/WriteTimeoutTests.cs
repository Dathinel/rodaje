using System.IO.Pipes;
using Rodaje.Modules.Protocol.Tests.Fuzz;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Escrituras del editor hacia un módulo que no lee (defecto 5 de <see cref="ProtocolDefectTests"/>): el plazo de
/// escritura (<see cref="ModuleClientOptions.WriteTimeout"/>) corta la conexión, cancelar mientras se espera el turno no
/// la corta, y cancelar a mitad de una escritura sí, porque el marco pudo quedar cortado. Los plazos son solo topes
/// contra cuelgues (<see cref="FuzzSupport.CaseCap"/>); no se mide el reloj.
/// </summary>
public sealed class WriteTimeoutTests
{
    [Fact]
    public async Task A_module_that_stops_reading_fails_the_connection_after_the_write_timeout()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<bool> host = Support.EchoHost().RunAsync(module);
            GatedWrites stream = new(editor);
            ModuleClient client = await ModuleClient.ConnectAsync(stream, new ModuleClientOptions { WriteTimeout = TimeSpan.FromMilliseconds(300) });
            stream.Stall();

            // Sin token del llamador: solo el plazo de escritura puede destrabarla.
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => FuzzSupport.Capped(client.InvokeAsync("eco"), "la llamada"));
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => FuzzSupport.Capped(client.Completion, "Completion"));
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            await FuzzSupport.Capped(host, "RunAsync");
        }
    }

    [Fact]
    public async Task Cancelling_while_waiting_to_write_keeps_the_connection()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<bool> host = Support.EchoHost().RunAsync(module);
            GatedWrites stream = new(editor);
            ModuleClient client = await ModuleClient.ConnectAsync(stream, new ModuleClientOptions { WriteTimeout = TimeSpan.FromMinutes(5) });
            stream.Stall();

            Task<TimeSpan> ping = client.PingAsync();
            await FuzzSupport.Capped(stream.FirstWaiting, "la escritura del ping");
            using CancellationTokenSource cts = new();
            Task call = client.InvokeAsync("eco", cancellationToken: cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FuzzSupport.Capped(call, "la llamada cancelada"));
            Assert.False(client.Completion.IsCompleted, "Cancelar mientras se espera el turno cortó la conexión.");

            stream.Release();
            await FuzzSupport.Capped(ping, "el ping");
            await FuzzSupport.Capped(client.InvokeAsync("eco", Support.Json("1")), "una llamada después");
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            await FuzzSupport.Capped(host, "RunAsync");
        }
    }

    [Fact]
    public async Task Cancelling_in_the_middle_of_a_write_fails_the_connection()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<bool> host = Support.EchoHost().RunAsync(module);
            GatedWrites stream = new(editor);
            ModuleClient client = await ModuleClient.ConnectAsync(stream, new ModuleClientOptions { WriteTimeout = TimeSpan.FromMinutes(5) });
            stream.Stall();

            using CancellationTokenSource cts = new();
            Task call = client.InvokeAsync("eco", cancellationToken: cts.Token);
            await FuzzSupport.Capped(stream.FirstWaiting, "la escritura de la llamada");
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FuzzSupport.Capped(call, "la llamada cancelada"));
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => FuzzSupport.Capped(client.Completion, "Completion"));
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => FuzzSupport.Capped(client.PingAsync(), "un ping después"));
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            await FuzzSupport.Capped(host, "RunAsync");
        }
    }

    /// <summary>Una tubería cuyas escrituras esperan, mientras está cerrada, como si el módulo no leyera.</summary>
    private sealed class GatedWrites(Stream inner) : Stream
    {
        private readonly TaskCompletionSource _firstWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile TaskCompletionSource? _gate;

        public Task FirstWaiting => _firstWaiting.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Stall() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gate is { } gate)
            {
                _firstWaiting.TrySetResult();
                await gate.Task.WaitAsync(cancellationToken);
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
