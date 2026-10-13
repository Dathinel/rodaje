using System.IO.Pipes;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// El diagnóstico de los topes del fuzz (<see cref="PendingAwaits"/>): de un <c>ModuleHost.RunAsync</c> parado tiene que
/// llegar hasta la espera de verdad, aquí la lectura del <c>hello</c> que el editor no manda.
/// </summary>
public sealed class PendingAwaitsTests
{
    [Fact]
    public async Task Describes_the_chain_down_to_the_pending_read()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            using CancellationTokenSource stop = new();
            Task<bool> host = Support.EchoHost().RunAsync(module, stop.Token);
            await Task.Delay(50);
            Assert.False(host.IsCompleted);

            string chain = PendingAwaits.Describe(host);

            Assert.Contains("ModuleHost+<RunAsync>", chain, StringComparison.Ordinal);
            Assert.Contains("<HandshakeAsync>", chain, StringComparison.Ordinal);
            Assert.Contains("MessageChannel+<ReceiveAsync>", chain, StringComparison.Ordinal);
            Assert.Contains("MessageChannel+<ReadExactlyOrEndAsync>", chain, StringComparison.Ordinal);
            Assert.Contains("estado 0", chain, StringComparison.Ordinal);
            Assert.Contains("active=", chain, StringComparison.Ordinal);
            Assert.DoesNotContain("no se pudo seguir", chain, StringComparison.Ordinal);

            await stop.CancelAsync();
            await FuzzSupport.Capped(host, "RunAsync cancelado");
        }
    }

    [Fact]
    public async Task Names_a_plain_pending_task_and_never_throws()
    {
        TaskCompletionSource never = new();
        Task waiting = never.Task.WaitAsync(TimeSpan.FromMinutes(1));
        string chain = PendingAwaits.Describe(waiting);
        Assert.Contains("[WaitingForActivation]", chain, StringComparison.Ordinal);
        Assert.DoesNotContain("no se pudo seguir", chain, StringComparison.Ordinal);
        never.SetResult();
        await waiting;
    }
}
