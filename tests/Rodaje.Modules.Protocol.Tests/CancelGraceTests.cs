using System.Diagnostics;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// El plazo para confirmar una cancelación (tarea 221): el editor daba 2 s y, con la CPU llena, un módulo sano tarda más
/// en llegar a su siguiente punto de cancelación (una etapa de LibRaw, una inferencia de ONNX Runtime). El cliente lo daba
/// por colgado, cortaba la conexión y lo mataba: «El módulo no confirmó la cancelación en 2 s» en la CI de Windows
/// (<c>Rodaje.Module.Raw.Tests.ProcessTests.Cancel_is_confirmed_quickly</c>). El plazo sale ahora de
/// <see cref="ModuleClientOptions.DefaultCancelGrace"/>. Las pruebas que ejercen el corte (<c>ProcessTests</c>,
/// <c>ClientFuzzTests</c>) siguen bajándolo a propósito.
/// </summary>
public sealed class CancelGraceTests
{
    /// <summary>El plazo de fábrica que cortaba módulos sanos.</summary>
    private static readonly TimeSpan OldCancelGrace = TimeSpan.FromSeconds(2);

    [Fact]
    public void The_default_grace_fits_a_slow_step_and_the_ci_bound_of_the_module_tests()
    {
        ModuleClientOptions options = new();

        Assert.Equal(ModuleClientOptions.DefaultCancelGrace, options.CancelGrace);
        Assert.True(options.CancelGrace > OldCancelGrace, $"La gracia de la cancelación sigue en {options.CancelGrace.TotalSeconds} s.");
        // Las pruebas de cancelación de los módulos admiten hasta CiBound en la CI: con una gracia menor, el cliente corta
        // antes de la cota de la propia prueba.
        Assert.True(options.CancelGrace >= Support.CiBound,
            $"El editor da {options.CancelGrace.TotalSeconds} s a la cancelación y las pruebas admiten {Support.CiBound.TotalSeconds} s.");
    }

    /// <summary>
    /// Un módulo que confirma la cancelación más tarde que el plazo viejo (su función no la atiende hasta que se abre una
    /// puerta, 2,5 s después de verla) no se da por colgado con las opciones de fábrica: la llamada termina cancelada y la
    /// conexión sigue sirviendo. Con 2 s, el cliente cortaba antes de abrir la puerta y el <c>eco</c> fallaba con
    /// <see cref="ModuleDisconnectedException"/>. Sin reloj fino: solo pide que la puerta se abra después de 2 s y que la
    /// confirmación llegue antes de la gracia de fábrica.
    /// </summary>
    [Fact]
    public async Task A_module_that_confirms_after_the_old_grace_is_not_cut_with_the_default_options()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = new(Support.EchoInfo with { Functions = [.. Support.EchoInfo.Functions, new FunctionInfo("lento-al-cancelar", "Lento al cancelar")] });
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        host.Register("lento-al-cancelar", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            cancelSeen.TrySetResult();
            // El paso que no se interrumpe: la confirmación sale cuando se abre la puerta.
            await gate.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ModuleOutput();
        });

        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            using CancellationTokenSource cancel = new();
            Task<ModuleResult> call = client.InvokeAsync("lento-al-cancelar", cancellationToken: cancel.Token);
            await started.Task.WaitAsync(Support.CiBound);
            Stopwatch watch = Stopwatch.StartNew();
            await cancel.CancelAsync();
            await cancelSeen.Task.WaitAsync(Support.CiBound);
            TimeSpan late = OldCancelGrace + TimeSpan.FromMilliseconds(500);
            _ = Task.Delay(late).ContinueWith(_ => gate.TrySetResult(), TaskScheduler.Default);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.True(watch.Elapsed >= late, $"La puerta se abrió antes de tiempo: {watch.ElapsedMilliseconds} ms.");
            Assert.False(client.Completion.IsCompleted, "El cliente cortó la conexión de un módulo que confirmó la cancelación.");
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
