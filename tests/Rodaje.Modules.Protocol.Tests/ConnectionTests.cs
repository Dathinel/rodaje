using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>Cliente y anfitrión en el mismo proceso, por una tubería real.</summary>
public sealed class ConnectionTests
{
    [Fact]
    public async Task Handshake_reports_the_module_and_the_version()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        Assert.Equal(1, client.Protocol);
        Assert.Equal("prueba", client.Module.Id);
        Assert.Equal(4, client.Module.Functions.Count);
        await client.DisposeAsync();
        Assert.True(await host.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Echo_round_trip_keeps_the_parameters()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            JsonElement parameters = Support.Json("""{"texto":"hola, ñandú 🎬","lista":[1,2.5,true,null],"anidado":{"x":-3}}""");
            ModuleResult result = await client.InvokeAsync("eco", parameters);
            Assert.True(JsonElement.DeepEquals(parameters, result.Value!.Value));
            Assert.True((await client.PingAsync()) < TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Progress_arrives_in_order_and_ends_at_one()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            List<ModuleProgress> seen = [];
            SyncProgress progress = new(p => { lock (seen) seen.Add(p); });
            await client.InvokeAsync("contar", Support.Json("""{"pasos":20,"msPorPaso":10}"""), progress: progress);
            lock (seen)
            {
                Assert.NotEmpty(seen);
                Assert.Equal(1.0, seen[^1].Fraction);
                Assert.Equal("contando", seen[^1].Stage);
                for (int i = 1; i < seen.Count; i++) Assert.True(seen[i].Fraction >= seen[i - 1].Fraction);
            }
        }
    }

    [Fact]
    public async Task Cancelling_is_confirmed_quickly_and_the_connection_keeps_working()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));
            Stopwatch watch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""), cancellationToken: cts.Token));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"tardó {watch.ElapsedMilliseconds} ms");
            Assert.Equal("1", (await client.InvokeAsync("eco", Support.Json("1"))).Value!.Value.GetRawText());
        }
    }

    [Fact]
    public async Task Function_errors_do_not_close_the_connection()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            ModuleCallException unknown = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("no-existe"));
            Assert.Equal(ErrorCodes.UnknownFunction, unknown.Code);
            ModuleCallException failed = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("fallar"));
            Assert.Equal(ErrorCodes.Internal, failed.Code);
            Assert.Contains("Fallo a propósito", failed.Message, StringComparison.Ordinal);
            await client.InvokeAsync("eco");
        }
    }

    [Fact]
    public async Task Calls_beyond_the_limit_are_refused_as_busy()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(Support.EchoHost(maxConcurrent: 1));
        await using (client)
        {
            Task first = client.InvokeAsync("contar", Support.Json("""{"pasos":20,"msPorPaso":10}"""));
            await Task.Delay(30);
            ModuleCallException busy = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("eco"));
            Assert.Equal(ErrorCodes.Busy, busy.Code);
            await first;
        }
    }

    [Fact]
    public async Task Concurrent_calls_get_their_own_answers()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            Task<ModuleResult>[] calls = [.. Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        return await client.InvokeAsync("eco", Support.Json(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    catch (ModuleCallException e) when (e.Code == ErrorCodes.Busy)
                    {
                        await Task.Yield();
                    }
                }
            }))];
            ModuleResult[] results = await Task.WhenAll(calls);
            for (int i = 0; i < results.Length; i++) Assert.Equal(i, results[i].Value!.Value.GetInt32());
        }
    }

    [Fact]
    public async Task Module_with_a_newer_protocol_only_is_refused()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost(min: 2, max: 3).RunAsync(module);
        ModuleVersionException ex = await Assert.ThrowsAsync<ModuleVersionException>(() => ModuleClient.ConnectAsync(editor));
        Assert.Contains("2–3", ex.Message, StringComparison.Ordinal);
        Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Editor_with_a_newer_protocol_only_is_refused()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        _ = Support.EchoHost().RunAsync(module);
        await Assert.ThrowsAsync<ModuleVersionException>(() => ModuleClient.ConnectAsync(editor, new ModuleClientOptions { MinProtocol = 2, MaxProtocol = 2 }));
    }

    [Fact]
    public async Task Overlapping_ranges_pick_the_highest_common_version()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        _ = Support.EchoHost(min: 1, max: 3).RunAsync(module);
        await using ModuleClient client = await ModuleClient.ConnectAsync(editor, new ModuleClientOptions { MinProtocol = 1, MaxProtocol = 2 });
        Assert.Equal(2, client.Protocol);
    }

    [Fact]
    public async Task A_welcome_with_a_version_outside_the_range_is_refused()
    {
        string welcome = Support.WelcomeJson.Replace("\"protocol\":1", "\"protocol\":9", StringComparison.Ordinal);
        await Assert.ThrowsAsync<ModuleVersionException>(() => Support.HostileAsync(_ => Task.CompletedTask, welcome));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task A_module_that_never_greets_times_out()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream silent) = await Support.PipePairAsync();
        using (silent)
        {
            Stopwatch watch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<ModuleProtocolException>(() => ModuleClient.ConnectAsync(editor, new ModuleClientOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) }));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        }
    }

    public static TheoryData<string, Type> HostileReplies() => new()
    {
        { "basura", typeof(ModuleProtocolException) },
        { "largo", typeof(ModuleProtocolException) },
        { "cortado", typeof(ModuleProtocolException) },
        { "invoke", typeof(ModuleProtocolException) },
        { "error-sin-id", typeof(ModuleProtocolException) },
        { "bufer-ajeno", typeof(ModuleProtocolException) },
        { "cerrar", typeof(ModuleDisconnectedException) },
    };

    [Theory]
    [MemberData(nameof(HostileReplies))]
    public async Task A_hostile_module_fails_the_call_and_closes_without_hurting_the_editor(string kind, Type expected)
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            string? invoke = await Support.ReadFrameAsync(module);
            long id = JsonDocument.Parse(invoke!).RootElement.GetProperty("id").GetInt64();
            byte[] reply = kind switch
            {
                "basura" => Support.Frame("{esto no es json"),
                "largo" => [0xFF, 0xFF, 0xFF, 0x7F],
                "cortado" => Support.Frame("""{"type":"result","id":1}""")[..10],
                "invoke" => Support.Frame("""{"type":"invoke","id":1,"function":"eco"}"""),
                "error-sin-id" => Support.Frame("""{"type":"error","code":"internal","message":"x"}"""),
                "bufer-ajeno" => Support.Frame($$"""{"type":"result","id":{{id}},"buffers":[{"region":"Rodaje.Mod.ajena","offset":0,"length":16}]}"""),
                _ => [],
            };
            await module.WriteAsync(reply);
            module.Dispose();
        });
        Exception ex = await Assert.ThrowsAnyAsync<ModuleException>(() => client.InvokeAsync("eco").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType(expected, ex);
        await Assert.ThrowsAnyAsync<ModuleException>(() => client.Completion);
        await Assert.ThrowsAnyAsync<ModuleException>(() => client.InvokeAsync("eco"));
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Late_replies_and_progress_for_unknown_calls_are_ignored()
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            string? invoke = await Support.ReadFrameAsync(module);
            long id = JsonDocument.Parse(invoke!).RootElement.GetProperty("id").GetInt64();
            await module.WriteAsync(Support.Frame("""{"type":"progress","id":999,"fraction":0.5}"""));
            await module.WriteAsync(Support.Frame("""{"type":"result","id":998}"""));
            await module.WriteAsync(Support.Frame("""{"type":"desconocido-futuro"}"""));
            await module.WriteAsync(Support.Frame("""{"type":"log","level":"info","text":"hola"}"""));
            await module.WriteAsync(Support.Frame($$"""{"type":"result","id":{{id}},"value":42}"""));
        });
        await using (client)
        {
            List<string> logs = [];
            client.Log += l => logs.Add(l.Text);
            Assert.Equal(42, (await client.InvokeAsync("eco")).Value!.Value.GetInt32());
            Assert.Equal(["hola"], logs);
        }
    }

    [Fact]
    public async Task A_hostile_editor_stops_the_host()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        await editor.WriteAsync(Support.Frame("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}"""));
        Assert.Contains("welcome", await Support.ReadFrameAsync(editor), StringComparison.Ordinal);
        Task<string?> reply = Support.ReadFrameAsync(editor);
        await editor.WriteAsync(Support.Frame("[[[[ basura"));
        Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("malformed", await reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_rejects_anything_but_hello_first()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        await editor.WriteAsync(Support.Frame("""{"type":"invoke","id":1,"function":"eco"}"""));
        Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Closing_the_editor_cancels_running_calls_and_ends_the_host()
    {
        (ModuleClient client, Task<bool> host) = await Support.ConnectInProcessAsync();
        Task call = client.InvokeAsync("contar", Support.Json("""{"pasos":10000,"msPorPaso":10}"""));
        await Task.Delay(50);
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<ModuleException>(() => call);
        Assert.True(await host.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Shared_memory_copy_is_exact_and_bounds_are_checked()
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync();
        await using (client)
        {
            const int size = 1920 * 1080 * 4;
            SharedMemoryRegion region = client.CreateRegion(2L * size);
            VideoLayout layout = new(1920, 1080, 1920 * 4, PixelFormats.Rgba8);
            SharedBufferRef input = region.Describe(0, size, layout);
            SharedBufferRef output = region.Describe(size, size, layout);
            new Random(1).NextBytes(region.GetSpan(input));
            ModuleResult result = await client.InvokeAsync("copiar", buffers: [input, output]);
            Assert.Equal(output, Assert.Single(result.Buffers));
            Assert.True(region.GetSpan(input).SequenceEqual(region.GetSpan(output)));

            SharedBufferRef outside = new(region.Name, 2L * size - 8, 16);
            ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("copiar", buffers: [outside, input]));
            Assert.Equal(ErrorCodes.BufferOutOfRange, ex.Code);
            Assert.Throws<ModuleCallException>(() => region.GetSpan(2L * size - 8, 16));
            await Assert.ThrowsAsync<ArgumentException>(() => client.InvokeAsync("copiar", buffers: [new SharedBufferRef("Rodaje.Mod.otra", 0, 4)]));
        }
    }

    private sealed class SyncProgress(Action<ModuleProgress> report) : IProgress<ModuleProgress>
    {
        public void Report(ModuleProgress value) => report(value);
    }
}
