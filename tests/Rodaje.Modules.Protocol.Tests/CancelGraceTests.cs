using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// El plazo para confirmar una cancelación (tarea 221): el editor daba 2 s y, con la CPU llena, un módulo sano tarda más
/// en llegar a su siguiente punto de cancelación (una etapa de LibRaw, una inferencia de ONNX Runtime). El cliente lo daba
/// por colgado, cortaba la conexión y lo mataba: «El módulo no confirmó la cancelación en 2 s» en la CI de Windows
/// (<c>Rodaje.Module.Raw.Tests.ProcessTests.Cancel_is_confirmed_quickly</c>). El plazo sale ahora de
/// <see cref="ModuleClientOptions.DefaultCancelGrace"/>. Se ejerce con un reloj manual (<see cref="ManualTime"/>), sin
/// esperarlo (tarea 244). Las pruebas que ejercen el corte con reloj real (<c>ProcessTests</c>, <c>ClientFuzzTests</c>)
/// siguen bajándolo a propósito.
/// </summary>
public sealed class CancelGraceTests
{
    /// <summary>El plazo de fábrica que cortaba módulos sanos.</summary>
    private static readonly TimeSpan OldCancelGrace = TimeSpan.FromSeconds(2);

    [Fact]
    public void The_default_grace_is_not_shorter_than_the_ci_bound_of_the_module_tests()
    {
        ModuleClientOptions options = new();

        Assert.Equal(ModuleClientOptions.DefaultCancelGrace, options.CancelGrace);
        // Las pruebas de cancelación de los módulos (Support.CiBound, ProcessTests.CiLimit de RAW) admiten 10 s en la CI:
        // con una gracia menor, el cliente corta antes de la cota de la propia prueba.
        Assert.True(options.CancelGrace >= Support.CiBound,
            $"El editor da {options.CancelGrace.TotalSeconds} s a la cancelación y las pruebas admiten {Support.CiBound.TotalSeconds} s.");
    }

    /// <summary>
    /// Un módulo que confirma la cancelación más tarde que el plazo viejo (su función no la atiende hasta que se abre una
    /// puerta, con el reloj ya 2,5 s más allá) no se da por colgado con las opciones de fábrica: la llamada termina
    /// cancelada y la conexión sigue sirviendo. Con 2 s, el plazo vencía al avanzar el reloj y el cliente cortaba.
    /// </summary>
    [Fact]
    public async Task A_module_that_confirms_after_the_old_grace_is_not_cut_with_the_default_options()
    {
        ManualTime time = new();
        Gated gated = new();
        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(gated.Host, new ModuleClientOptions { Time = time });
        await time.NextTimerAsync(); // El plazo del saludo, ya cumplido.
        await using (client)
        {
            using CancellationTokenSource cancel = new();
            Task<ModuleResult> call = client.InvokeAsync("lento-al-cancelar", cancellationToken: cancel.Token);
            await gated.Started.Task.WaitAsync(Support.CiBound);
            await cancel.CancelAsync();
            await gated.CancelSeen.Task.WaitAsync(Support.CiBound);
            ManualTime.ManualTimer grace = await time.NextTimerAsync();

            time.Advance(OldCancelGrace + TimeSpan.FromMilliseconds(500));
            Assert.True(grace.Pending, $"La gracia de la cancelación ({grace.Due.TotalSeconds} s) venció antes de la confirmación.");
            gated.Gate.TrySetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Support.CiBound));
            Assert.False(client.Completion.IsCompleted, "El cliente cortó la conexión de un módulo que confirmó la cancelación.");
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
        Assert.True(await run.WaitAsync(Support.CiBound));
    }

    /// <summary>
    /// Un módulo que no confirma nunca se corta justo al vencer la gracia de fábrica: un tick antes la conexión sigue, y al
    /// vencer la llamada termina cancelada y el cliente falla con el plazo en el mensaje.
    /// </summary>
    [Fact]
    public async Task A_module_that_never_confirms_is_cut_when_the_default_grace_runs_out()
    {
        ManualTime time = new();
        Gated gated = new();
        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(gated.Host, new ModuleClientOptions { Time = time });
        await time.NextTimerAsync();
        await using (client)
        {
            using CancellationTokenSource cancel = new();
            Task<ModuleResult> call = client.InvokeAsync("lento-al-cancelar", cancellationToken: cancel.Token);
            await gated.Started.Task.WaitAsync(Support.CiBound);
            await cancel.CancelAsync();
            await gated.CancelSeen.Task.WaitAsync(Support.CiBound);
            ManualTime.ManualTimer grace = await time.NextTimerAsync();

            time.Advance(ModuleClientOptions.DefaultCancelGrace - TimeSpan.FromTicks(1));
            Assert.True(grace.Pending);
            Assert.False(call.IsCompleted);

            time.Advance(TimeSpan.FromTicks(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Support.CiBound));
            ModuleDisconnectedException error = await Assert.ThrowsAsync<ModuleDisconnectedException>(() => client.Completion.WaitAsync(Support.CiBound));
            Assert.Contains($"no confirmó la cancelación en {ModuleClientOptions.DefaultCancelGrace.TotalSeconds:0} s", error.Message, StringComparison.Ordinal);
        }
        gated.Gate.TrySetResult();
        await run.WaitAsync(Support.CiBound);
    }

    /// <summary>Un módulo cuya función ve la cancelación y no la confirma hasta que se abre <see cref="Gate"/>.</summary>
    private sealed class Gated
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancelSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModuleHost Host { get; }

        public Gated()
        {
            Host = new(Support.EchoInfo with { Functions = [.. Support.EchoInfo.Functions, new FunctionInfo("lento-al-cancelar", "Lento al cancelar")] });
            Host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
            Host.Register("lento-al-cancelar", async (_, ct) =>
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                CancelSeen.TrySetResult();
                // El paso que no se interrumpe: la confirmación sale cuando se abre la puerta.
                await Gate.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return new ModuleOutput();
            });
        }
    }
}
