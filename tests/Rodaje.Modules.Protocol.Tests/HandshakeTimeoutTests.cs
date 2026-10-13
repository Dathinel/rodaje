using System.IO.Pipes;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Los plazos del saludo (tarea 205): el editor daba 10 s a que el módulo saludara y el módulo 30 s a recibir el
/// <c>hello</c>, así que un arranque en frío de más de 10 s fallaba con «El módulo no saludó en 10 s» (CI de Windows,
/// <c>Rodaje.Module.Translate.Tests</c>). Los dos lados salen ahora de <see cref="ModuleClientOptions.DefaultHandshakeTimeout"/>.
/// El plazo se comprueba con un reloj manual (<see cref="ManualTime"/>), sin esperarlo (tarea 244).
/// </summary>
public sealed class HandshakeTimeoutTests
{
    /// <summary>El plazo viejo del editor.</summary>
    private static readonly TimeSpan OldClientTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void The_editor_waits_for_the_greeting_at_least_as_long_as_the_module_waits_for_hello()
    {
        ModuleClientOptions client = new();
        ModuleHost host = new(Support.EchoInfo);

        Assert.Equal(ModuleClientOptions.DefaultHandshakeTimeout, client.HandshakeTimeout);
        Assert.Equal(ModuleClientOptions.DefaultHandshakeTimeout, host.HandshakeTimeout);
        Assert.True(client.HandshakeTimeout >= host.HandshakeTimeout,
            $"El editor da {client.HandshakeTimeout.TotalSeconds} s al saludo y el módulo {host.HandshakeTimeout.TotalSeconds} s.");
    }

    /// <summary>
    /// Un módulo que no saluda hasta pasados 11 s de reloj (su anfitrión espera una puerta) conecta con las opciones de
    /// fábrica y atiende una llamada. Con 10 s, el plazo vencía al avanzar el reloj y la conexión fallaba.
    /// </summary>
    [Fact]
    public async Task A_module_that_greets_after_more_than_ten_seconds_connects_with_the_default_options()
    {
        ManualTime time = new();
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> run = Task.Run(async () =>
        {
            await gate.Task;
            return await Support.EchoHost().RunAsync(module);
        });

        Task<ModuleClient> connect = ModuleClient.ConnectAsync(editor, new ModuleClientOptions { Time = time });
        ManualTime.ManualTimer limit = await time.NextTimerAsync();
        time.Advance(OldClientTimeout + TimeSpan.FromSeconds(1));

        Assert.True(limit.Pending, $"El plazo del saludo ({limit.Due.TotalSeconds} s) venció antes de que el módulo saludara.");
        Assert.False(connect.IsCompleted, "La conexión terminó sin que el módulo saludara.");
        gate.TrySetResult();
        await using (ModuleClient client = await connect.WaitAsync(Support.CiBound))
        {
            ModuleResult result = await client.InvokeAsync("eco", Support.Json("""{"a":1}"""));
            Assert.Equal(1, result.Value!.Value.GetProperty("a").GetInt32());
        }
        Assert.True(await run.WaitAsync(Support.CiBound));
    }

    /// <summary>Un módulo que nunca saluda falla justo al vencer el plazo de fábrica, ni antes ni sin fin.</summary>
    [Fact]
    public async Task A_module_that_never_greets_fails_when_the_default_timeout_runs_out()
    {
        ManualTime time = new();
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<ModuleClient> connect = ModuleClient.ConnectAsync(editor, new ModuleClientOptions { Time = time });
            ManualTime.ManualTimer limit = await time.NextTimerAsync();

            time.Advance(ModuleClientOptions.DefaultHandshakeTimeout - TimeSpan.FromTicks(1));
            Assert.True(limit.Pending);
            Assert.False(connect.IsCompleted);

            time.Advance(TimeSpan.FromTicks(1));
            ModuleProtocolException error = await Assert.ThrowsAsync<ModuleProtocolException>(() => connect.WaitAsync(Support.CiBound));
            Assert.Contains($"no saludó en {ModuleClientOptions.DefaultHandshakeTimeout.TotalSeconds:0} s", error.Message, StringComparison.Ordinal);
        }
    }
}
