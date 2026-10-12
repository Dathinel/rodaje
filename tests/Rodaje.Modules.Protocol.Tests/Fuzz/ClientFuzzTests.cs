using System.IO.Pipes;
using System.Text;
using Xunit.Abstractions;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Un «módulo» hostil escrito a mano contra el <see cref="ModuleClient"/> real, por una tubería real. El módulo saluda
/// (bien o con un <c>welcome</c> raro), lee todo lo que le manda el editor y le responde con marcos al azar: respuestas
/// repetidas o a llamadas ajenas, avances fuera de rango, búferes de regiones que no existen, JSON roto, largos falsos.
/// Luego cierra, se queda callado o sigue mandando basura. Propiedades:
/// <list type="bullet">
/// <item>Ninguna operación del cliente pasa de <see cref="FuzzSupport.CaseCap"/> (llamadas con cancelación, ping,
/// <c>DisposeAsync</c>, <c>Completion</c>).</item>
/// <item>Solo salen <see cref="ModuleException"/> u <see cref="OperationCanceledException"/>.</item>
/// <item>Un resultado aceptado no trae búferes (el cliente no creó regiones) y los avances están en 0–1.</item>
/// </list>
/// El módulo hostil siempre lee lo que le llega: el caso de un módulo que deja de leer está aparte, en
/// <see cref="ProtocolDefectTests"/>. Un cuelgue falla con el estado interno del cliente. Cada caso tiene su semilla (<see cref="FuzzSupport.BaseSeed"/> + n).
/// </summary>
public sealed class ClientFuzzTests(ITestOutputHelper output)
{
    private static readonly ModuleClientOptions Options = new()
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        CancelGrace = TimeSpan.FromMilliseconds(150),
    };

    [Fact]
    public async Task A_hostile_module_never_hangs_or_crashes_the_client()
    {
        Tally tally = new();
        await FuzzSupport.RunCasesAsync(300, seed => RunCaseAsync(seed, tally));
        output.WriteLine(tally.ToString());
        Assert.True(tally.Results > 0 && tally.Failures > 0 && tally.Cancelled > 0, tally.ToString());
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_hostile_module_never_hangs_or_crashes_the_client_long_run()
    {
        Tally tally = new();
        await FuzzSupport.RunCasesAsync(5000, seed => RunCaseAsync(seed, tally), first: 1_000_000);
        output.WriteLine(tally.ToString());
    }

    /// <summary>
    /// Regresión: con estas semillas una operación se quedaba colgada en Windows después de que el cliente fallaba (el
    /// candado de escritura se desechaba con esperas en cola; ver
    /// <see cref="ProtocolDefectTests.Operations_waiting_to_write_finish_when_the_client_fails"/>). Las tres últimas son
    /// de la corrida larga. En Linux no se colgaban; cada una se repite para darle más oportunidades a la carrera.
    /// </summary>
    [Theory]
    [InlineData(20261282)]
    [InlineData(20261032)]
    [InlineData(21261787)]
    [InlineData(21261056)]
    [InlineData(21261076)]
    public async Task Seeds_that_hung_on_windows_finish(int seed)
    {
        Tally tally = new();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => RunCaseAsync(seed, tally))));
        output.WriteLine(tally.ToString());
    }

    private static async Task RunCaseAsync(int seed, Tally tally)
    {
        Random random = new(seed);
        FuzzFrames frames = new(random);
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Ending ending = (Ending)random.Next(4);
        byte[] welcome = random.Next(6) == 0 ? frames.Frame(FuzzDirection.ToEditor) : FuzzSupport.Frame(Encoding.UTF8.GetBytes(Support.WelcomeJson));
        int count = random.Next(0, 14);
        int[] delays = [.. Enumerable.Range(0, count).Select(_ => random.Next(4) == 0 ? random.Next(1, 30) : 0)];
        // Un tercio de los casos manda basura de entrada; el resto, mensajes aceptables con valores al azar y basura rara vez.
        bool mild = random.Next(3) != 0;
        byte[][] script = [.. Enumerable.Range(0, count).Select(_ =>
            mild && random.Next(12) != 0 ? frames.ValidFrame(FuzzDirection.ToEditor) : frames.Frame(FuzzDirection.ToEditor))];
        byte[] tail = frames.Frame(FuzzDirection.ToEditor);
        int calls = random.Next(1, 4);
        int[] cancelAfter = [.. Enumerable.Range(0, calls).Select(_ => random.Next(20, 300))];
        bool ping = random.Next(3) == 0;

        Task moduleSide = Task.Run(async () =>
        {
            if (await FuzzSupport.ReadFrameAsync(module) is null) return;
            if (!await FuzzSupport.TryWriteAsync(module, welcome)) return;
            Task<List<byte[]>> drain = FuzzSupport.DrainAsync(module);
            for (int i = 0; i < script.Length; i++)
            {
                if (delays[i] > 0) await Task.Delay(delays[i]);
                if (!await FuzzSupport.TryWriteAsync(module, script[i])) break;
            }
            switch (ending)
            {
                case Ending.Close:
                    module.Dispose();
                    break;
                case Ending.AnswerEverything:
                    // Responde bien a todas las llamadas que pudo haber hecho el editor (ids 1…4), luego cierra.
                    for (int id = 1; id <= 4; id++)
                        await FuzzSupport.TryWriteAsync(module, Support.Frame($$"""{"type":"result","id":{{id}},"value":{{id}}}"""));
                    await Task.Delay(50);
                    module.Dispose();
                    break;
                case Ending.Garbage:
                    await FuzzSupport.TryWriteAsync(module, tail);
                    break;
            }
            // Silent y Garbage: se queda abierto y leyendo hasta que el editor cierre.
            await drain;
        });

        try
        {
            ModuleClient client;
            try
            {
                client = await FuzzSupport.Capped(ModuleClient.ConnectAsync(editor, Options), "ConnectAsync");
            }
            catch (ModuleException)
            {
                Interlocked.Increment(ref tally.RefusedHandshake);
                return;
            }

            client.Log += _ => Interlocked.Increment(ref tally.Logs);
            List<Task> operations = [];
            List<CancellationTokenSource> tokens = [.. cancelAfter.Select(ms => new CancellationTokenSource(ms))];
            try
            {
                for (int i = 0; i < calls; i++)
                {
                    ProgressCheck progress = new();
                    operations.Add(ObserveCallAsync(client.InvokeAsync("eco", Support.Json("1"), progress: progress, cancellationToken: tokens[i].Token), progress, tally));
                }
                if (ping)
                {
                    tokens.Add(new CancellationTokenSource(200));
                    operations.Add(ObservePingAsync(client.PingAsync(tokens[^1].Token)));
                }
                try
                {
                    foreach (Task operation in operations) await FuzzSupport.Capped(operation, "una llamada");
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException($"{ex.Message} {Describe(client, operations)} módulo={moduleSide.Status} final={ending}", ex);
                }
            }
            finally
            {
                foreach (CancellationTokenSource cts in tokens) cts.Dispose();
            }
            await FuzzSupport.Capped(client.DisposeAsync().AsTask(), "DisposeAsync");
            await FuzzSupport.Capped(client.Completion.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception!.InnerException is not ModuleException)
                    throw new InvalidOperationException("Completion falló con algo que no es ModuleException.", t.Exception);
            }, TaskScheduler.Default), "Completion");
        }
        finally
        {
            editor.Dispose();
            module.Dispose();
            await FuzzSupport.Capped(moduleSide, "el módulo hostil");
        }
    }

    /// <summary>El estado interno del cliente cuando algo se cuelga, para saber dónde (por reflexión: solo diagnóstico).</summary>
    private static string Describe(ModuleClient client, List<Task> operations)
    {
        static object? Field(object target, string name) =>
            target.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target);
        StringBuilder text = new("Estado:");
        text.Append(" operaciones=").AppendJoin(",", operations.Select(o => o.Status));
        text.Append(" failure=").Append(Field(client, "_failure")?.GetType().Name ?? "null");
        text.Append(" completion=").Append(client.Completion.Status);
        text.Append(" stop=").Append((Field(client, "_stop") as CancellationTokenSource)?.IsCancellationRequested);
        text.Append(" readLoop=").Append((Field(client, "_readLoop") as Task)?.Status);
        if (Field(client, "_pending") is System.Collections.IEnumerable pending)
            foreach (object entry in pending)
            {
                object? value = entry.GetType().GetProperty("Value")?.GetValue(entry);
                object? completion = value?.GetType().GetProperty("Completion")?.GetValue(value);
                Task? task = completion?.GetType().GetProperty("Task")?.GetValue(completion) as Task;
                text.Append(" pendiente[").Append(entry.GetType().GetProperty("Key")?.GetValue(entry)).Append("]=").Append(task?.Status);
            }
        if (Field(client, "_pings") is System.Collections.IEnumerable pings)
            foreach (object entry in pings) text.Append(" ping[").Append(entry.GetType().GetProperty("Key")?.GetValue(entry)).Append(']');
        if (Field(client, "_channel") is { } channel && Field(channel, "_writeLock") is SemaphoreSlim writeLock)
        {
            try
            {
                text.Append(" candado=").Append(writeLock.CurrentCount);
            }
            catch (ObjectDisposedException)
            {
                text.Append(" candado=desechado");
            }
        }
        return text.ToString();
    }

    private static async Task ObserveCallAsync(Task<ModuleResult> call, ProgressCheck progress, Tally tally)
    {
        try
        {
            ModuleResult result = await call;
            Assert.Empty(result.Buffers);
            Interlocked.Increment(ref tally.Results);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref tally.Cancelled);
        }
        catch (ModuleException)
        {
            Interlocked.Increment(ref tally.Failures);
        }
        Assert.True(progress.AllInRange, "un avance fuera de 0–1 llegó al llamador");
    }

    private static async Task ObservePingAsync(Task<TimeSpan> ping)
    {
        try
        {
            await ping;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ModuleException)
        {
        }
    }

    private enum Ending
    {
        Close,
        Silent,
        Garbage,
        AnswerEverything,
    }

    private sealed class ProgressCheck : IProgress<ModuleProgress>
    {
        public bool AllInRange { get; private set; } = true;

        public void Report(ModuleProgress value)
        {
            if (!(value.Fraction is >= 0 and <= 1)) AllInRange = false;
        }
    }

    private sealed class Tally
    {
#pragma warning disable CA1051 // Contadores para Interlocked.
        public int RefusedHandshake;
        public int Results;
        public int Cancelled;
        public int Failures;
        public int Logs;
#pragma warning restore CA1051

        public override string ToString() =>
            $"saludos rechazados {RefusedHandshake}, resultados {Results}, canceladas {Cancelled}, fallos {Failures}, registros {Logs}";
    }
}
