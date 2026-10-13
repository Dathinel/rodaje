using System.Diagnostics;
using System.IO.Pipes;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// TEMPORAL (tarea 137): repite en la CI de Windows el escenario de
/// <c>ProtocolDefectTests.Cancel_racing_the_end_of_a_call_does_not_close_the_connection</c> durante unos minutos, de 4 en
/// 4, para cazar el cuelgue con el diagnóstico de <see cref="PendingAwaits"/>. Se quita antes de unir.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class TemporaryHangHuntTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    [Fact]
    public async Task Hunt_the_cancel_race_hang()
    {
        if (!OperatingSystem.IsWindows()) return;
        Stopwatch clock = Stopwatch.StartNew();
        int rounds = 0;
        // Segunda pasada: con la CPU llena por hilos propios (no del pool), como cuando falló junto a otros proyectos.
        using CancellationTokenSource burn = new();
        List<Thread> burners = [];
        for (int i = 0; i < Environment.ProcessorCount; i++)
        {
            Thread t = new(() => { while (!burn.IsCancellationRequested) Thread.SpinWait(1000); }) { IsBackground = true };
            t.Start();
            burners.Add(t);
        }
        try
        {
            await FuzzSupport.RunCasesAsync(100_000, async seed =>
            {
                if (clock.Elapsed > TimeSpan.FromMinutes(5)) return;
                Interlocked.Increment(ref rounds);
                await RoundAsync(seed);
            }, parallel: 2);
        }
        finally
        {
            await burn.CancelAsync();
        }
        Assert.Fail($"TEMPORAL: rondas Dispose {s_roundsDispose} colgadas {s_hangsDispose}, rondas Disconnect {s_roundsDisconnect} colgadas {s_hangsDisconnect}; {rounds} rondas en {clock.Elapsed.TotalSeconds:0} s sin cuelgue.");
    }

    private static int s_hangsDispose, s_hangsDisconnect, s_roundsDispose, s_roundsDisconnect;

    private static async Task RoundAsync(int round)
    {
        bool disconnect = round % 2 == 0;
        Interlocked.Increment(ref disconnect ? ref s_roundsDisconnect : ref s_roundsDispose);
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> host = Support.EchoHost(maxConcurrent: 64).RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await FuzzSupport.ReadFrameAsync(editor);
            Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
            List<byte> burst = [];
            for (int i = 1; i <= 200; i++)
            {
                burst.AddRange(Support.Frame($$"""{"type":"invoke","id":{{i}},"function":"eco"}"""));
                burst.AddRange(Support.Frame($$"""{"type":"cancel","id":{{i}}}"""));
            }
            await editor.WriteAsync(burst.ToArray());
            await Task.Delay(20);
            Assert.False(host.IsCompleted, $"Ronda {round}: el módulo cortó la conexión sin que el editor cerrara.");
            Microsoft.Win32.SafeHandles.SafePipeHandle handle = editor.SafePipeHandle;
            if (disconnect)
            {
                try
                {
                    editor.Disconnect();
                }
#pragma warning disable CA1031
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
            editor.Dispose();
            if (await Task.WhenAny(host, Task.Delay(TimeSpan.FromSeconds(10))) == host) return;
            int hangs = Interlocked.Increment(ref disconnect ? ref s_hangsDisconnect : ref s_hangsDispose);
            string chain = PendingAwaits.Describe(host);
            string drainChain = PendingAwaits.Describe(drain);
            string write;
            try
            {
                using CancellationTokenSource limit = new(TimeSpan.FromSeconds(1));
                await module.WriteAsync(new byte[] { 1 }, limit.Token);
                write = "la escritura del módulo pasó";
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                write = $"la escritura del módulo falló: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
            }
            bool after = await Task.WhenAny(host, Task.Delay(TimeSpan.FromSeconds(2))) == host;
            throw new TimeoutException(
                $"Colgado ({(disconnect ? "Disconnect+Dispose" : "solo Dispose")}, colgadas {hangs}; rondas Dispose {s_roundsDispose} colgadas {s_hangsDispose}, rondas Disconnect {s_roundsDisconnect} colgadas {s_hangsDisconnect}). " +
                $"Asa del editor cerrada: {handle.IsClosed}, inválida: {handle.IsInvalid}. {write}. RunAsync terminó tras escribir: {after}.\nRunAsync:{chain}\nLectura del editor:{drainChain}");
        }
    }
}
