using System.IO.Pipes;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Lo que cuelga de <see cref="ModuleClient.Completion"/> (en <c>ModuleConnection</c>, matar el árbol de procesos) no
/// corre en línea con <c>Fail</c> ni delante de las llamadas: las llamadas en curso fallan aunque esa continuación tarde.
/// </summary>
public sealed class FailPathTests
{
    /// <summary>
    /// Fail en un hilo del pool, como en producción (el bucle de lectura, la salida del proceso). Las continuaciones que
    /// suelta (la de la llamada y la lenta) van a la cola local de ese hilo, que al terminar Fail toma primero la última
    /// (LIFO); otro hilo solo puede robar la primera. Si la lenta era la última, el hilo se bloqueaba en ella con la de la
    /// llamada todavía en su cola: en la CI de Windows, sin hilos libres, la llamada no terminó en 10 s. Fail suelta ahora
    /// la de Completion antes que las de las llamadas, así que, si la lenta empieza en el mismo hilo que Fail, la llamada ya
    /// tiene que haber terminado: ese hilo no se queda colgado en la lenta con la llamada pendiente en su cola.
    /// </summary>
    /// <remarks>
    /// La llamada tiene que estar ya esperando la respuesta cuando corre Fail; si no, su continuación no está en la cola
    /// del hilo de Fail y la vuelta no dice nada. Antes se suponía con una pausa de 50 ms, y en Windows no basta: la
    /// escritura de la tubería termina por el puerto de finalización y lo que sigue en <c>InvokeAsync</c> espera turno en
    /// el pool, que en la CI cargada pasaba de esos 50 ms. Fail encontraba la llamada sin continuación, la lenta era lo
    /// único en la cola de su hilo y empezaba allí con la llamada sin terminar («Vuelta 0», tarea 294), sin defecto en
    /// <see cref="ModuleClient"/>: la llamada terminaba en otro hilo. Ahora el editor escribe de forma síncrona
    /// (<see cref="SyncWrites"/>), así que <c>InvokeAsync</c> vuelve ya esperando la respuesta. Una vuelta en la que otro
    /// hilo roba la lenta no dice nada y se repite; con el orden de Fail invertido, la lenta queda la última en la cola y
    /// falla en las primeras vueltas. Si en las <see cref="Rounds"/> vueltas ninguna comprobó nada, la prueba sigue hasta
    /// <see cref="MaxRounds"/> y, si tampoco, falla: no puede pasar sin haber mirado una sola vuelta (tarea 310).
    /// </remarks>
    [Fact]
    public async Task Fail_does_not_wait_for_a_slow_continuation_on_completion()
    {
        int round = 0;
        int checkedRounds = 0;
        for (; round < MaxRounds && (round < Rounds || checkedRounds == 0); round++)
        {
            bool? callDone = await FailFromAPoolThreadAsync();
            Assert.True(callDone != false, $"Vuelta {round}: la continuación lenta corrió en el hilo de Fail antes que la llamada.");
            if (callDone.HasValue) checkedRounds++;
        }
        Assert.True(checkedRounds > 0,
            $"La prueba no comprobó nada en {round} vueltas: la continuación lenta nunca empezó en el hilo de Fail.");
    }

    /// <summary>Vueltas de la prueba si alguna comprueba algo.</summary>
    private const int Rounds = 20;

    /// <summary>Vueltas como mucho mientras ninguna compruebe nada.</summary>
    private const int MaxRounds = 200;

    /// <returns>Si la continuación lenta corrió en el hilo de Fail, si la llamada ya había terminado; si no, null.</returns>
    private static async Task<bool?> FailFromAPoolThreadAsync()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        ModuleClient client = await ModuleClient.ConnectAsync(new SyncWrites(editor));
        using ManualResetEventSlim gate = new(false);
        try
        {
            Task<ModuleResult>? call = null;
            int failThread = 0;
            bool? callDoneWhenSlowStartedOnFailThread = null;
            TaskCompletionSource slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Como el OnExited de ModuleConnection (mata el árbol de procesos), pero que no termina hasta que la prueba lo
            // suelte. Es la única espera síncrona sobre el pool del proyecto y es a propósito (tarea 137): la prueba
            // necesita una continuación que ocupe el hilo del pool que la toma, para ver si ese hilo es el de Fail. Ocupa
            // un solo hilo y lo suelta el finally de cada vuelta, en cuanto la llamada falló.
            Task slow = client.Completion.ContinueWith(_ =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref failThread))
                    callDoneWhenSlowStartedOnFailThread = call!.IsCompleted;
                slowStarted.TrySetResult();
                gate.Wait(TimeSpan.FromSeconds(60));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Con las escrituras síncronas, InvokeAsync vuelve ya esperando la respuesta: su continuación cuelga de la
            // llamada pendiente y Fail la suelta en la cola de su hilo.
            call = client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));
            Assert.False(call.IsCompleted);

            Task fail = Task.Run(() =>
            {
                Volatile.Write(ref failThread, Environment.CurrentManagedThreadId);
                client.Fail(new ModuleDisconnectedException("Corte de prueba."));
            });
            await fail.WaitAsync(Support.CiBound);
            await Assert.ThrowsAsync<ModuleDisconnectedException>(() => call.WaitAsync(Support.CiBound));
            Assert.False(slow.IsCompleted);
            await slowStarted.Task.WaitAsync(Support.CiBound);
            return callDoneWhenSlowStartedOnFailThread;
        }
        finally
        {
            gate.Set();
            await client.DisposeAsync();
            await host.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// El lado del editor de la tubería, con las escrituras síncronas: <c>SendAsync</c> termina antes de volver y
    /// <c>InvokeAsync</c> llega a esperar la respuesta sin pasar por el pool. Al cerrar desconecta como la tubería de
    /// verdad (<see cref="MessageChannel.Close(Stream)"/>).
    /// </summary>
    private sealed class SyncWrites(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inner.Flush();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inner.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) MessageChannel.Close(inner);
            base.Dispose(disposing);
        }
    }
}
