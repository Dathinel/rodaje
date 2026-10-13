using System.Diagnostics;
using System.IO.Pipes;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Los plazos del saludo (tarea 205): el editor daba 10 s a que el módulo saludara y el módulo 30 s a recibir el
/// <c>hello</c>, así que un arranque en frío de más de 10 s fallaba con «El módulo no saludó en 10 s» (CI de Windows,
/// <c>Rodaje.Module.Translate.Tests</c>). Los dos lados salen ahora de <see cref="ModuleClientOptions.DefaultHandshakeTimeout"/>.
/// </summary>
public sealed class HandshakeTimeoutTests
{
    /// <summary>Lo que tardó de más el arranque en frío que destapó el defecto: el plazo viejo del editor.</summary>
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
        Assert.True(client.HandshakeTimeout > OldClientTimeout, "El plazo del saludo no cabe un arranque en frío de más de 10 s.");
    }

    /// <summary>
    /// Un módulo que tarda más de 10 s en saludar (la puerta del anfitrión se abre a los 11 s) conecta con los plazos de
    /// fábrica y atiende una llamada. Tarda 11 s de reloj: solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>.
    /// </summary>
    [LongFact]
    public async Task A_module_that_greets_after_more_than_ten_seconds_connects_with_the_default_options()
    {
        TimeSpan late = OldClientTimeout + TimeSpan.FromSeconds(1);
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> run = Task.Run(async () =>
        {
            await gate.Task;
            return await Support.EchoHost().RunAsync(module);
        });
        Stopwatch watch = Stopwatch.StartNew();
        _ = Task.Delay(late).ContinueWith(_ => gate.TrySetResult(), TaskScheduler.Default);

        await using (ModuleClient client = await ModuleClient.ConnectAsync(editor).WaitAsync(TimeSpan.FromSeconds(60)))
        {
            Assert.True(watch.Elapsed >= late - TimeSpan.FromMilliseconds(50), $"Saludó antes de abrir la puerta: {watch.ElapsedMilliseconds} ms.");
            ModuleResult result = await client.InvokeAsync("eco", Support.Json("""{"a":1}"""));
            Assert.Equal(1, result.Value!.Value.GetProperty("a").GetInt32());
        }
        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}

/// <summary>Solo con <c>RODAJE_PRUEBAS_LARGAS=1</c>, como las demás pruebas largas del repositorio.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") != "1")
            Skip = "Omitida: prueba larga; se activa con RODAJE_PRUEBAS_LARGAS=1 (Probar.bat aceptacion).";
    }
}
