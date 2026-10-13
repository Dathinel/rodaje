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
        await FuzzSupport.RunCasesAsync(100_000, async seed =>
        {
            if (clock.Elapsed > TimeSpan.FromMinutes(4)) return;
            Interlocked.Increment(ref rounds);
            await RoundAsync(seed);
        }, parallel: 4);
        Assert.True(rounds > 0);
    }

    private static async Task RoundAsync(int round)
    {
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
            editor.Dispose();
            try
            {
                await FuzzSupport.Capped(host, $"RunAsync (ronda {round})");
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"{ex.Message}\nLectura del editor:{PendingAwaits.Describe(drain)}", ex);
            }
            await FuzzSupport.Capped(drain, $"la lectura (ronda {round})");
        }
    }
}
