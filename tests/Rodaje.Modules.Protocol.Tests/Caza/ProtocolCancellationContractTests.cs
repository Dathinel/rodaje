using System.IO.Pipes;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Caza con contrato (tarea 352): una prueba por promesa verificable de la sección «Cancelación» de
/// <c>docs/protocolo-modulos.md</c>, con la frase literal encima.
/// </summary>
public sealed class ProtocolCancellationContractTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    // «El editor manda `cancel` con el `id` de la llamada.»
    [Fact]
    public async Task The_editor_sends_cancel_with_the_call_id()
    {
        await using Hostile hostile = await Hostile.StartAsync();
        (ModuleClient client, Stream wire) = (hostile.Client, hostile.Wire);
        using CancellationTokenSource cancel = new();

        Task<ModuleResult> call = client.InvokeAsync("eco", Support.Json("1"), cancellationToken: cancel.Token);
        string invoke = (await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound))!;
        long id = JsonDocument.Parse(invoke).RootElement.GetProperty("id").GetInt64();
        await cancel.CancelAsync();

        Assert.Equal($$"""{"type":"cancel","id":{{id}}}""", await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound));
        await wire.WriteAsync(Support.Frame($$"""{"type":"error","code":"cancelled","message":"Cancelado.","id":{{id}}}"""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Support.CiBound));
    }

    // «El módulo deja de trabajar y **confirma** con un `error` de código `cancelled` y el mismo `id`. Marcos reales: el
    // editor cancela `contar` a los 130 ms y la conexión sigue sirviendo para la llamada siguiente.» (los marcos del ejemplo)
    [Fact]
    public async Task The_example_frames_go_over_the_wire_as_written()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (editor)
        await using (module)
        {
            Task<bool> run = Support.EchoHost().RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            Assert.Contains("\"welcome\"", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound), StringComparison.Ordinal);

            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":1,"function":"contar","parameters":{"pasos":100,"msPorPaso":50}}"""));
            Assert.Equal("""{"type":"progress","id":1,"fraction":0.01,"stage":"contando"}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
            await editor.WriteAsync(Support.Frame("""{"type":"cancel","id":1}"""));
            string? frame;
            do frame = await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound);
            while (frame is not null && frame.Contains("\"progress\"", StringComparison.Ordinal));
            Assert.Equal("""{"type":"error","code":"cancelled","message":"Cancelado.","id":1}""", frame);

            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":2,"function":"eco","parameters":1}"""));
            Assert.Equal("""{"type":"result","id":2,"value":1}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));

            await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «el editor espera la confirmación hasta `ModuleClientOptions.CancelGrace`, **10 s** por defecto
    // (`ModuleClientOptions.DefaultCancelGrace`, desde #219; `ModuleClient.InvokeAsync`).»
    [Fact]
    public void The_default_grace_is_10_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ModuleClientOptions.DefaultCancelGrace);
        Assert.Equal(ModuleClientOptions.DefaultCancelGrace, new ModuleClientOptions().CancelGrace);
    }

    // «Si no llega, da el módulo por colgado: corta la conexión, falla todas las llamadas en curso con
    // `ModuleDisconnectedException` («El módulo no confirmó la cancelación en 10 s.»)»
    [Fact]
    public async Task Without_confirmation_the_connection_is_cut_and_every_call_fails_disconnected()
    {
        ManualTime time = new();
        Hostile hostile = await Hostile.StartAsync(new ModuleClientOptions { Time = time });
        await time.NextTimerAsync(); // El plazo del saludo, ya cumplido.
        await using (hostile)
        {
            (ModuleClient client, Stream wire) = (hostile.Client, hostile.Wire);
            using CancellationTokenSource cancel = new();
            Task<ModuleResult> cancelled = client.InvokeAsync("eco", Support.Json("1"), cancellationToken: cancel.Token);
            await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound);
            Task<ModuleResult> other = client.InvokeAsync("eco", Support.Json("2"));
            await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound);
            await cancel.CancelAsync();
            Assert.Contains("\"cancel\"", await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound), StringComparison.Ordinal);

            ManualTime.ManualTimer grace = await time.NextTimerAsync();
            Assert.Equal(TimeSpan.FromSeconds(10), grace.Due);
            time.Advance(TimeSpan.FromSeconds(10));

            ModuleDisconnectedException error = await Assert.ThrowsAsync<ModuleDisconnectedException>(() => client.Completion.WaitAsync(Support.CiBound));
            Assert.Equal("El módulo no confirmó la cancelación en 10 s.", error.Message);
            Assert.Same(error, await Assert.ThrowsAsync<ModuleDisconnectedException>(() => other.WaitAsync(Support.CiBound)));
            // La llamada cancelada termina cancelada; la causa es el corte (ver «Pendiente» del PR).
            OperationCanceledException own = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Support.CiBound));
            Assert.Same(error, own.InnerException);
            Assert.Null(await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound));
        }
    }

    // «Si en el plazo llega un `result` en vez de la confirmación, el `ModuleClient` devuelve ese resultado»
    [Fact]
    public async Task A_result_within_the_grace_is_returned()
    {
        await using Hostile hostile = await Hostile.StartAsync();
        (ModuleClient client, Stream wire) = (hostile.Client, hostile.Wire);
        using CancellationTokenSource cancel = new();

        Task<ModuleResult> call = client.InvokeAsync("eco", Support.Json("1"), cancellationToken: cancel.Token);
        await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound);
        await cancel.CancelAsync();
        Assert.Contains("\"cancel\"", await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound), StringComparison.Ordinal);
        await wire.WriteAsync(Support.Frame("""{"type":"result","id":1,"value":42}"""));

        Assert.Equal("42", (await call.WaitAsync(Support.CiBound)).Value!.Value.GetRawText());
        Assert.False(client.Completion.IsCompleted);
    }

    // «si llega cualquier `error`, la llamada termina como cancelada (`OperationCanceledException`).»
    [Theory]
    [InlineData("cancelled")]
    [InlineData("internal")]
    [InlineData("invalid-parameters")]
    [InlineData("out-of-memory")]
    public async Task Any_error_within_the_grace_ends_the_call_as_cancelled(string code)
    {
        await using Hostile hostile = await Hostile.StartAsync();
        (ModuleClient client, Stream wire) = (hostile.Client, hostile.Wire);
        using CancellationTokenSource cancel = new();

        Task<ModuleResult> call = client.InvokeAsync("eco", Support.Json("1"), cancellationToken: cancel.Token);
        await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound);
        await cancel.CancelAsync();
        Assert.Contains("\"cancel\"", await Support.ReadFrameAsync(wire).WaitAsync(Support.CiBound), StringComparison.Ordinal);
        await wire.WriteAsync(Support.Frame($$"""{"type":"error","code":"{{code}}","message":"x","id":1}"""));

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Support.CiBound));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.False(client.Completion.IsCompleted, "Una llamada cancelada no corta la conexión.");
    }

    // «Un `cancel` de una llamada que ya terminó o que no existe se ignora (`ModuleHost.RunAsync`).»
    [Fact]
    public async Task Cancel_of_a_finished_or_unknown_call_is_ignored()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        await using (editor)
        await using (module)
        {
            Task<bool> run = Support.EchoHost().RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound);

            await editor.WriteAsync(Support.Frame("""{"type":"cancel","id":99}"""));
            await editor.WriteAsync(Support.Frame("""{"type":"ping","id":100}"""));
            Assert.Equal("""{"type":"pong","id":100}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));

            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":1,"function":"eco","parameters":7}"""));
            Assert.Equal("""{"type":"result","id":1,"value":7}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
            await editor.WriteAsync(Support.Frame("""{"type":"cancel","id":1}"""));
            await editor.WriteAsync(Support.Frame("""{"type":"ping","id":101}"""));
            Assert.Equal("""{"type":"pong","id":101}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));

            await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":1,"function":"eco","parameters":8}"""));
            Assert.Equal("""{"type":"result","id":1,"value":8}""", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));

            await editor.WriteAsync(Support.Frame("""{"type":"shutdown"}"""));
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «En el anfitrión de C#, `cancel` cancela el `CancellationToken` que recibe la función.»
    [Fact]
    public async Task Cancel_cancels_the_token_the_function_receives()
    {
        TaskCompletionSource<CancellationToken> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = Host(async (_, ct) =>
        {
            received.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            seen.TrySetResult();
            ct.ThrowIfCancellationRequested();
            return new ModuleOutput();
        });
        (ModuleClient client, Task<bool> run) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            using CancellationTokenSource cancel = new();
            Task<ModuleResult> call = client.InvokeAsync("f", cancellationToken: cancel.Token);
            CancellationToken token = await received.Task.WaitAsync(Support.CiBound);
            Assert.False(token.IsCancellationRequested);

            await cancel.CancelAsync();
            await seen.Task.WaitAsync(Support.CiBound);
            Assert.True(token.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Support.CiBound));
        }
        Assert.True(await run.WaitAsync(Support.CiBound));
    }

    // «Si la función lanza `OperationCanceledException` con ese token cancelado, el anfitrión manda el `error` `cancelled`»
    [Fact]
    public async Task Throwing_with_the_cancelled_token_sends_cancelled()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = Host(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ModuleOutput();
        });
        await using RawEditor editor = await RawEditor.StartAsync(host);

        await editor.WriteAsync("""{"type":"invoke","id":5,"function":"f"}""");
        await started.Task.WaitAsync(Support.CiBound);
        await editor.WriteAsync("""{"type":"cancel","id":5}""");

        Assert.Equal("""{"type":"error","code":"cancelled","message":"Cancelado.","id":5}""", await editor.ReadAsync());
    }

    // «si termina antes, manda su `result`.»
    [Fact]
    public async Task A_function_that_returns_despite_the_cancel_sends_its_result()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleHost host = Host(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            return new ModuleOutput(Support.Json("3"));
        });
        await using RawEditor editor = await RawEditor.StartAsync(host);

        await editor.WriteAsync("""{"type":"invoke","id":5,"function":"f"}""");
        await started.Task.WaitAsync(Support.CiBound);
        await editor.WriteAsync("""{"type":"cancel","id":5}""");

        Assert.Equal("""{"type":"result","id":5,"value":3}""", await editor.ReadAsync());
    }

    // «Una `OperationCanceledException` sin que se haya pedido cancelar cuenta como error `internal`.»
    [Fact]
    public async Task An_unrequested_operation_canceled_exception_is_internal()
    {
        ModuleHost host = Host((_, _) => throw new OperationCanceledException("Por su cuenta."));
        await using RawEditor editor = await RawEditor.StartAsync(host);

        await editor.WriteAsync("""{"type":"invoke","id":5,"function":"f"}""");

        using JsonDocument reply = JsonDocument.Parse((await editor.ReadAsync())!);
        Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
        Assert.Equal(ErrorCodes.Internal, reply.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, reply.RootElement.GetProperty("id").GetInt64());
    }

    private static ModuleHost Host(ModuleFunction function)
    {
        ModuleHost host = new(Support.EchoInfo with { Functions = [new FunctionInfo("f", "F")] });
        host.Register("f", function);
        return host;
    }

    /// <summary>Un módulo escrito a mano (<see cref="Support.HostileAsync"/>) que cierra su lado al recibir «shutdown».</summary>
    private sealed class Hostile : IAsyncDisposable
    {
        private Hostile(ModuleClient client, Stream wire) => (Client, Wire) = (client, wire);

        public ModuleClient Client { get; }

        public Stream Wire { get; }

        public static async Task<Hostile> StartAsync(ModuleClientOptions? options = null)
        {
            TaskCompletionSource<Stream> module = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ModuleClient client = await Support.HostileAsync(s => { module.TrySetResult(s); return Task.CompletedTask; }, options: options);
            return new(client, await module.Task.WaitAsync(Support.CiBound));
        }

        public async ValueTask DisposeAsync()
        {
            Task closer = Task.Run(async () =>
            {
                string? frame;
                do frame = await Support.ReadFrameAsync(Wire);
                while (frame is not null && !frame.Contains("\"shutdown\"", StringComparison.Ordinal));
                await Wire.DisposeAsync();
            });
            await Client.DisposeAsync();
            await closer.WaitAsync(Support.CiBound);
        }
    }

    /// <summary>Un editor escrito a mano frente a un <see cref="ModuleHost"/> en el mismo proceso, ya saludado.</summary>
    private sealed class RawEditor : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _editor;
        private readonly NamedPipeClientStream _module;
        private readonly Task<bool> _run;

        private RawEditor(NamedPipeServerStream editor, NamedPipeClientStream module, Task<bool> run) => (_editor, _module, _run) = (editor, module, run);

        public static async Task<RawEditor> StartAsync(ModuleHost host)
        {
            (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
            RawEditor raw = new(editor, module, host.RunAsync(module));
            await raw.WriteAsync(Hello);
            Assert.Contains("\"welcome\"", await raw.ReadAsync(), StringComparison.Ordinal);
            return raw;
        }

        public async Task WriteAsync(string json) => await _editor.WriteAsync(Support.Frame(json));

        public Task<string?> ReadAsync() => Support.ReadFrameAsync(_editor).WaitAsync(Support.CiBound);

        public async ValueTask DisposeAsync()
        {
            await WriteAsync("""{"type":"shutdown"}""");
            Assert.True(await _run.WaitAsync(Support.CiBound));
            await _editor.DisposeAsync();
            await _module.DisposeAsync();
        }
    }
}
