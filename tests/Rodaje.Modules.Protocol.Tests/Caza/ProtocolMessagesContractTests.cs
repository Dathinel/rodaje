using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests.Caza;

/// <summary>
/// Lo que promete «Mensajes» de <c>docs/protocolo-modulos.md</c>, comprobado contra el serializador, el anfitrión y el
/// cliente. Cada prueba cita la frase. Las que necesitan regiones de memoria compartida con nombre, el
/// <c>ModuleManager</c> o <c>ModuleProcess</c> no están: solo se comprueban en Windows o fuera de este proyecto.
/// </summary>
public sealed class ProtocolMessagesContractTests
{
    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0","culture":"es-CO"}""";

    private static readonly ModuleInfo Info = new("prueba", "1.0.0", "Prueba",
    [
        new FunctionInfo("eco", "Eco"),
        new FunctionInfo("esperar", "Esperar"),
        new FunctionInfo("contar", "Contar"),
        new FunctionInfo("sin-registrar", "Sin registrar"),
    ]);

    private static string Serialize(ModuleMessage message) => JsonSerializer.Serialize(message, ProtocolJsonContext.Default.ModuleMessage);

    private static ModuleMessage? Parse(string json) => MessageChannel.Parse(Encoding.UTF8.GetBytes(json));

    /// <summary>
    /// Un anfitrión con <c>eco</c>, <c>esperar</c> (hasta que se abra <paramref name="gate"/> o se cancele) y
    /// <c>contar</c>; <c>sin-registrar</c> está en el <c>welcome</c> pero no tiene función.
    /// </summary>
    private static ModuleHost Host(TaskCompletionSource? gate = null, Action<CancellationToken>? onWait = null, int? maxConcurrent = null)
    {
        ModuleHost host = maxConcurrent is { } max ? new(Info) { MaxConcurrentCalls = max } : new(Info);
        host.Register("eco", (call, _) => ValueTask.FromResult(new ModuleOutput(call.Parameters)));
        host.Register("esperar", async (_, ct) =>
        {
            onWait?.Invoke(ct);
            await (gate ?? new()).Task.WaitAsync(ct);
            return new ModuleOutput();
        });
        host.Register("contar", async (call, ct) =>
        {
            for (int i = 1; i <= 3; i++)
            {
                await Task.Delay(1, ct);
                await call.ReportProgressAsync(i / 3.0, "etapa " + i, ct);
            }
            return new ModuleOutput();
        });
        return host;
    }

    /// <summary>Un editor escrito a mano que ya saludó a <paramref name="host"/>; devuelve también el <c>welcome</c>.</summary>
    private static async Task<(NamedPipeServerStream Editor, Task<bool> Run, string Welcome)> RawEditorAsync(ModuleHost host)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> run = host.RunAsync(module);
        await editor.WriteAsync(Support.Frame(Hello));
        string? welcome = await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound);
        return (editor, run, welcome!);
    }

    private static async Task SendAsync(Stream stream, string json) => await stream.WriteAsync(Support.Frame(json));

    private static async Task<string> ReceiveAsync(Stream stream) => (await Support.ReadFrameAsync(stream).WaitAsync(Support.CiBound))!;

    private static JsonElement Root(string json) => Support.Json(json);

    // «Hay 11 tipos (`ModuleMessage` en `Messages.cs`).»
    [Fact]
    public void Hay_11_tipos_de_mensaje()
    {
        string[] table = ["hello", "welcome", "invoke", "progress", "result", "error", "cancel", "ping", "pong", "log", "shutdown"];
        string[] declared = [.. typeof(ModuleMessage).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!)];
        Assert.Equal(table, declared);
        Assert.Equal(table.Order(), ModuleMessage.KnownTypes.Order());
    }

    public static TheoryData<string> EditorTypes() =>
    [
        """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""",
        Support.WelcomeJson,
        """{"type":"invoke","id":1,"function":"eco"}""",
        """{"type":"cancel","id":1}""",
        """{"type":"ping","id":1}""",
        """{"type":"shutdown"}""",
    ];

    // «Cada lado solo puede mandar los suyos: si el editor recibe un tipo que no le toca, corta la conexión («El módulo
    // no manda «…».», `ModuleClient.Dispatch`)». `welcome` va aquí porque, tras el saludo, tampoco le toca.
    // «Solo el editor manda `ping`: un `ping` del módulo corta la conexión.»
    [Theory]
    [MemberData(nameof(EditorTypes))]
    public async Task El_editor_corta_si_el_modulo_manda_un_tipo_que_no_le_toca(string json)
    {
        ModuleClient client = await Support.HostileAsync(module => SendAsync(module, json));
        await using (client)
        {
            ModuleProtocolException ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => client.Completion.WaitAsync(Support.CiBound));
            Assert.StartsWith("El módulo no manda «", ex.Message, StringComparison.Ordinal);
            await Assert.ThrowsAnyAsync<ModuleException>(() => client.InvokeAsync("eco"));
        }
    }

    public static TheoryData<string> ModuleTypes() =>
    [
        Support.WelcomeJson,
        """{"type":"progress","id":1,"fraction":0.5}""",
        """{"type":"result","id":1}""",
        """{"type":"error","code":"internal","message":"x","id":1}""",
        """{"type":"pong","id":1}""",
        """{"type":"log","level":"info","text":"x"}""",
    ];

    // «el anfitrión hace lo mismo («El editor no manda «…».», `ModuleHost.RunAsync`)»
    // «Un `error` sin `id` solo lo manda el módulo […] justo antes de cerrar porque el editor mandó algo inválido
    // (`malformed`).»
    [Theory]
    [MemberData(nameof(ModuleTypes))]
    public async Task El_anfitrion_corta_si_el_editor_manda_un_tipo_que_no_le_toca(string json)
    {
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host());
        await using (editor)
        {
            await SendAsync(editor, json);
            JsonElement error = Root(await ReceiveAsync(editor));
            Assert.False(await run.WaitAsync(Support.CiBound));
            Assert.Equal("error", error.GetProperty("type").GetString());
            Assert.Equal(ErrorCodes.Malformed, error.GetProperty("code").GetString());
            Assert.False(error.TryGetProperty("id", out _));
            Assert.StartsWith("El editor no manda «", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Null(await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        }
    }

    // La tabla: los campos con `?` son opcionales y los demás obligatorios.
    [Theory]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""")]
    [InlineData("""{"type":"invoke","id":1,"function":"eco"}""")]
    [InlineData("""{"type":"progress","id":1,"fraction":0.5}""")]
    [InlineData("""{"type":"result","id":1}""")]
    [InlineData("""{"type":"error","code":"internal","message":"x"}""")]
    [InlineData("""{"type":"cancel","id":1}""")]
    [InlineData("""{"type":"ping","id":1}""")]
    [InlineData("""{"type":"pong","id":1}""")]
    [InlineData("""{"type":"log","level":"info","text":"x"}""")]
    [InlineData("""{"type":"shutdown"}""")]
    public void Sin_los_opcionales_el_mensaje_vale_y_se_escribe_igual(string json)
    {
        Assert.Equal(json, Serialize(Parse(json)!));
    }

    [Theory]
    [InlineData("""{"type":"hello","maxProtocol":1,"app":"rodaje","appVersion":"1"}""")]
    [InlineData("""{"type":"hello","minProtocol":1,"app":"rodaje","appVersion":"1"}""")]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"appVersion":"1"}""")]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje"}""")]
    [InlineData("""{"type":"welcome","module":{"id":"x","version":"1","name":"X","functions":[]}}""")]
    [InlineData("""{"type":"welcome","protocol":1}""")]
    [InlineData("""{"type":"invoke","function":"eco"}""")]
    [InlineData("""{"type":"invoke","id":1}""")]
    [InlineData("""{"type":"progress","fraction":0.5}""")]
    [InlineData("""{"type":"progress","id":1}""")]
    [InlineData("""{"type":"result"}""")]
    [InlineData("""{"type":"error","message":"x"}""")]
    [InlineData("""{"type":"error","code":"internal"}""")]
    [InlineData("""{"type":"cancel"}""")]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"pong"}""")]
    [InlineData("""{"type":"log","text":"x"}""")]
    [InlineData("""{"type":"log","level":"info"}""")]
    public void Sin_un_campo_obligatorio_el_mensaje_no_vale(string json)
    {
        Assert.Throws<ModuleProtocolException>(() => Parse(json));
    }

    // «El editor elige el `id` de cada llamada: un entero de 64 bits mayor que 0»
    [Fact]
    public void El_id_de_una_llamada_es_un_entero_de_64_bits_mayor_que_0()
    {
        Assert.Equal(long.MaxValue, ((InvokeMessage)Parse($$"""{"type":"invoke","id":{{long.MaxValue}},"function":"eco"}""")!).Id);
        Assert.Throws<ModuleProtocolException>(() => Parse("""{"type":"invoke","id":0,"function":"eco"}"""));
        Assert.Throws<ModuleProtocolException>(() => Parse("""{"type":"invoke","id":-1,"function":"eco"}"""));
        Assert.Throws<ModuleProtocolException>(() => Parse("""{"type":"invoke","id":9223372036854775808,"function":"eco"}"""));
        Assert.Throws<ModuleProtocolException>(() => Parse("""{"type":"invoke","id":1.5,"function":"eco"}"""));
    }

    // «(el `ModuleClient` usa un contador que empieza en 1 y comparte con los `ping`)»
    [Fact]
    public async Task El_cliente_numera_desde_1_con_un_contador_compartido_con_los_ping()
    {
        List<string> seen = [];
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            for (int i = 0; i < 3; i++)
            {
                string frame = (await Support.ReadFrameAsync(module))!;
                JsonElement m = Root(frame);
                string type = m.GetProperty("type").GetString()!;
                long id = m.GetProperty("id").GetInt64();
                lock (seen) seen.Add($"{type} {id}");
                await SendAsync(module, type == "ping" ? $$"""{"type":"pong","id":{{id}}}""" : $$"""{"type":"result","id":{{id}}}""");
            }
        });
        await using (client)
        {
            await client.InvokeAsync("eco").WaitAsync(Support.CiBound);
            await client.PingAsync().WaitAsync(Support.CiBound);
            await client.InvokeAsync("eco").WaitAsync(Support.CiBound);
        }
        Assert.Equal(["invoke 1", "ping 2", "invoke 3"], seen);
    }

    // «Llamada real a la función `eco`, que devuelve sus parámetros» y «`value` también es cualquier valor JSON, o
    // falta; un número suelto vale»: los dos ejemplos, byte a byte.
    [Fact]
    public async Task Los_ejemplos_de_eco_salen_tal_cual()
    {
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host());
        await using (editor)
        {
            await SendAsync(editor, """{"type":"invoke","id":1,"function":"eco","parameters":{"texto":"hola","n":3}}""");
            Assert.Equal("""{"type":"result","id":1,"value":{"texto":"hola","n":3}}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"invoke","id":2,"function":"eco","parameters":1}""");
            Assert.Equal("""{"type":"result","id":2,"value":1}""", await ReceiveAsync(editor));
            // Sin `parameters`, la función recibe que faltan y el `result` sale sin `value`.
            await SendAsync(editor, """{"type":"invoke","id":3,"function":"eco"}""");
            Assert.Equal("""{"type":"result","id":3}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «`parameters` es cualquier valor JSON (o falta)»; «`value` también es cualquier valor JSON, o falta».
    [Theory]
    [InlineData("1")]
    [InlineData("-2.5")]
    [InlineData("\"texto\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[1,[2],{}]")]
    [InlineData("{\"a\":{\"b\":[]}}")]
    public async Task Parameters_y_value_son_cualquier_valor_JSON(string json)
    {
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(Host());
        await using (client)
        {
            ModuleResult result = await client.InvokeAsync("eco", Support.Json(json)).WaitAsync(Support.CiBound);
            // Un `null` JSON es como no mandar nada: con DefaultIgnoreCondition no se escribe.
            if (json == "null") Assert.True(result.Value is null || result.Value.Value.ValueKind == JsonValueKind.Null);
            else Assert.Equal(json, result.Value!.Value.GetRawText());
        }
    }

    // El ejemplo de `copiar` con búferes «sale del serializador del protocolo».
    [Fact]
    public void El_ejemplo_con_buferes_sale_del_serializador()
    {
        const string region = "Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e";
        VideoLayout video = new(1920, 1080, 7680, PixelFormats.Rgba8);
        SharedBufferRef first = new(region, 0, 8294400, video);
        SharedBufferRef second = new(region, 8294400, 8294400, video);
        Assert.Equal(
            """{"type":"invoke","id":9,"function":"copiar","buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":0,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}},{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":8294400,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}}]}""",
            Serialize(new InvokeMessage(9, "copiar", Buffers: [first, second])));
        Assert.Equal(
            """{"type":"result","id":9,"buffers":[{"region":"Rodaje.Mod.3f1c0d9a8b7e4c2a9d6f5e4b3a2c1d0e","offset":8294400,"length":8294400,"video":{"width":1920,"height":1080,"stride":7680,"pixelFormat":"rgba8"}}]}""",
            Serialize(new ResultMessage(9, Buffers: [second])));
        Assert.Equal(new InvokeMessage(9, "copiar", Buffers: [first, second]).Buffers, Assert.IsType<InvokeMessage>(Parse(Serialize(new InvokeMessage(9, "copiar", Buffers: [first, second])))).Buffers);
    }

    // «Cada `invoke` recibe **exactamente una** respuesta final: un `result` o un `error` con el mismo `id`. Antes puede
    // haber `progress`.»
    [Fact]
    public async Task Cada_invoke_recibe_una_sola_respuesta_final_y_los_avances_van_antes()
    {
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host());
        await using (editor)
        {
            foreach ((long id, string function, string final) in new[] { (1L, "contar", "result"), (2L, "nada", "error"), (3L, "eco", "result") })
            {
                await SendAsync(editor, $$"""{"type":"invoke","id":{{id}},"function":"{{function}}"}""");
                List<JsonElement> frames = [];
                while (true)
                {
                    JsonElement m = Root(await ReceiveAsync(editor));
                    frames.Add(m);
                    if (m.GetProperty("type").GetString() != "progress") break;
                }
                Assert.All(frames, m => Assert.Equal(id, m.GetProperty("id").GetInt64()));
                Assert.Equal(final, frames[^1].GetProperty("type").GetString());
                if (function == "contar") Assert.True(frames.Count >= 3, "contar avisa al empezar y al llegar a 1");

                // Lo siguiente que llega es el `pong`: no hubo una segunda respuesta final.
                await SendAsync(editor, $$"""{"type":"ping","id":{{100 + id}}}""");
                Assert.Equal($$"""{"type":"pong","id":{{100 + id}}}""", await ReceiveAsync(editor));
            }
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
            Assert.Null(await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        }
    }

    // «el anfitrión de C# atiende hasta `MaxConcurrentCalls` = 4 a la vez; la quinta recibe `error` `busy`»
    [Fact]
    public async Task El_anfitrion_atiende_4_llamadas_y_la_quinta_recibe_busy()
    {
        Assert.Equal(4, new ModuleHost(Info).MaxConcurrentCalls);
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host(gate));
        await using (editor)
        {
            for (int id = 1; id <= 5; id++) await SendAsync(editor, $$"""{"type":"invoke","id":{{id}},"function":"esperar"}""");
            JsonElement busy = Root(await ReceiveAsync(editor));
            Assert.Equal("error", busy.GetProperty("type").GetString());
            Assert.Equal(ErrorCodes.Busy, busy.GetProperty("code").GetString());
            Assert.Equal(5, busy.GetProperty("id").GetInt64());

            gate.SetResult();
            HashSet<long> done = [];
            for (int i = 0; i < 4; i++)
            {
                JsonElement result = Root(await ReceiveAsync(editor));
                Assert.Equal("result", result.GetProperty("type").GetString());
                done.Add(result.GetProperty("id").GetInt64());
            }
            Assert.Equal([1, 2, 3, 4], done.Order());
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «Las respuestas pueden llegar en cualquier orden; el editor las empareja por `id`»
    [Fact]
    public async Task Las_respuestas_llegan_en_cualquier_orden_y_el_editor_las_empareja_por_id()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(Host(gate));
        await using (client)
        {
            Task<ModuleResult> slow = client.InvokeAsync("esperar");
            ModuleResult fast = await client.InvokeAsync("eco", Support.Json("7")).WaitAsync(Support.CiBound);
            Assert.Equal(7, fast.Value!.Value.GetInt32());
            Assert.False(slow.IsCompleted);
            gate.SetResult();
            Assert.Null((await slow.WaitAsync(Support.CiBound)).Value);
        }
    }

    // «Un `invoke` con el `id` de una llamada que sigue en curso es un error de protocolo y el anfitrión cierra.»
    [Fact]
    public async Task Un_invoke_con_el_id_de_una_llamada_en_curso_cierra_el_anfitrion()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host(onWait: _ => started.TrySetResult()));
        await using (editor)
        {
            await SendAsync(editor, """{"type":"invoke","id":1,"function":"esperar"}""");
            await started.Task.WaitAsync(Support.CiBound);
            await SendAsync(editor, """{"type":"invoke","id":1,"function":"eco"}""");
            Assert.False(await run.WaitAsync(Support.CiBound));
            List<JsonElement> frames = [];
            while (await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound) is { } frame) frames.Add(Root(frame));
            JsonElement malformed = Assert.Single(frames, m => m.GetProperty("type").GetString() == "error" && !m.TryGetProperty("id", out _));
            Assert.Equal(ErrorCodes.Malformed, malformed.GetProperty("code").GetString());
            // La llamada en curso no recibe un `result` del `eco` repetido.
            Assert.DoesNotContain(frames, m => m.GetProperty("type").GetString() == "result");
        }
    }

    // «Una función que está en el `welcome` pero que el anfitrión no tiene registrada contesta `unknown-function`, igual
    // que una que no existe.»
    [Fact]
    public async Task Una_funcion_sin_registrar_contesta_unknown_function_como_una_inexistente()
    {
        (NamedPipeServerStream editor, Task<bool> run, string welcome) = await RawEditorAsync(Host());
        await using (editor)
        {
            Assert.Contains("\"sin-registrar\"", welcome, StringComparison.Ordinal);
            foreach ((long id, string function) in new[] { (1L, "sin-registrar"), (2L, "no-existe") })
            {
                await SendAsync(editor, $$"""{"type":"invoke","id":{{id}},"function":"{{function}}"}""");
                JsonElement error = Root(await ReceiveAsync(editor));
                Assert.Equal("error", error.GetProperty("type").GetString());
                Assert.Equal(ErrorCodes.UnknownFunction, error.GetProperty("code").GetString());
                Assert.Equal(id, error.GetProperty("id").GetInt64());
            }
            await SendAsync(editor, """{"type":"invoke","id":3,"function":"eco","parameters":3}""");
            Assert.Equal("""{"type":"result","id":3,"value":3}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «El editor **ignora** `progress`, `result` y `error` de una llamada que no conoce o que ya terminó»
    [Fact]
    public async Task El_editor_ignora_respuestas_de_llamadas_que_ya_terminaron_o_que_no_conoce()
    {
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            long first = Root((await Support.ReadFrameAsync(module))!).GetProperty("id").GetInt64();
            await SendAsync(module, $$"""{"type":"result","id":{{first}},"value":1}""");
            long second = Root((await Support.ReadFrameAsync(module))!).GetProperty("id").GetInt64();
            // Todo esto es de la primera, que ya terminó, o de una que nunca existió.
            await SendAsync(module, $$"""{"type":"progress","id":{{first}},"fraction":0.5}""");
            await SendAsync(module, $$"""{"type":"result","id":{{first}},"value":99}""");
            await SendAsync(module, $$"""{"type":"error","code":"internal","message":"tarde","id":{{first}}}""");
            await SendAsync(module, """{"type":"error","code":"internal","message":"ajena","id":777}""");
            await SendAsync(module, $$"""{"type":"result","id":{{second}},"value":2}""");
        });
        await using (client)
        {
            Assert.Equal(1, (await client.InvokeAsync("eco").WaitAsync(Support.CiBound)).Value!.Value.GetInt32());
            Assert.Equal(2, (await client.InvokeAsync("eco").WaitAsync(Support.CiBound)).Value!.Value.GetInt32());
            Assert.False(client.Completion.IsCompleted);
        }
    }

    // «Si la función del anfitrión de C# devuelve un resultado que no pasa la validación (por ejemplo, más de 16
    // búferes), se manda en su lugar un `error` `internal` «Resultado inválido: …».»
    [Fact]
    public async Task Un_resultado_invalido_se_contesta_con_error_internal()
    {
        ModuleHost host = new(Info);
        host.Register("eco", (_, _) => ValueTask.FromResult(new ModuleOutput(Buffers:
            [.. Enumerable.Range(0, 17).Select(i => new SharedBufferRef("Rodaje.Mod.x", i * 16, 16))])));
        (ModuleClient client, _) = await Support.ConnectInProcessAsync(host);
        await using (client)
        {
            ModuleCallException ex = await Assert.ThrowsAsync<ModuleCallException>(() => client.InvokeAsync("eco").WaitAsync(Support.CiBound));
            Assert.Equal(ErrorCodes.Internal, ex.Code);
            Assert.StartsWith("Resultado inválido: ", ex.Message, StringComparison.Ordinal);
            // La conexión sigue.
            Assert.False(client.Completion.IsCompleted);
        }
    }

    // `ping` y `pong`: el ejemplo, del serializador.
    [Fact]
    public void Los_ejemplos_de_ping_pong_log_y_shutdown_salen_del_serializador()
    {
        Assert.Equal("""{"type":"ping","id":2}""", Serialize(new PingMessage(2)));
        Assert.Equal("""{"type":"pong","id":2}""", Serialize(new PongMessage(2)));
        Assert.Equal("""{"type":"log","level":"info","text":"Modelo cargado en 840 ms."}""", Serialize(new LogMessage("info", "Modelo cargado en 840 ms.")));
        Assert.Equal("""{"type":"shutdown"}""", Serialize(new ShutdownMessage()));
    }

    // «El módulo contesta cada `ping` con un `pong` del mismo `id`, aunque tenga llamadas en curso (el anfitrión de C#
    // contesta desde el bucle de lectura, sin esperar a las funciones).»
    [Fact]
    public async Task El_anfitrion_contesta_el_ping_aunque_tenga_llamadas_en_curso()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host(gate, _ =>
        {
            if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult();
        }));
        await using (editor)
        {
            for (int id = 1; id <= 4; id++) await SendAsync(editor, $$"""{"type":"invoke","id":{{id}},"function":"esperar"}""");
            await allStarted.Task.WaitAsync(Support.CiBound);
            await SendAsync(editor, """{"type":"ping","id":42}""");
            Assert.Equal("""{"type":"pong","id":42}""", await ReceiveAsync(editor));
            gate.SetResult();
            for (int i = 0; i < 4; i++) Assert.Equal("result", Root(await ReceiveAsync(editor)).GetProperty("type").GetString());
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «A diferencia de los demás, el `id` de `ping` y `pong` no se valida (`PingMessage` y `PongMessage` no tienen
    // `Validate`); el editor usa ids positivos.»
    [Fact]
    public async Task El_id_de_ping_y_pong_no_se_valida()
    {
        Assert.Equal(new PingMessage(0), Parse("""{"type":"ping","id":0}"""));
        Assert.Equal(new PongMessage(-3), Parse("""{"type":"pong","id":-3}"""));
        Assert.Throws<ModuleProtocolException>(() => Parse("""{"type":"cancel","id":0}"""));

        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host());
        await using (editor)
        {
            await SendAsync(editor, """{"type":"ping","id":-7}""");
            Assert.Equal("""{"type":"pong","id":-7}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
        }
    }

    // «`level` es un identificador; […] el validador acepta cualquier identificador. `text` tiene hasta 4096
    // caracteres.»
    [Fact]
    public void El_nivel_del_log_es_cualquier_identificador_y_el_texto_hasta_4096()
    {
        foreach (string level in new[] { "info", "warn", "error", "debug", "traza.gpu-1" })
            Assert.Equal(level, ((LogMessage)Parse($$"""{"type":"log","level":"{{level}}","text":"x"}""")!).Level);
        foreach (string level in new[] { "Info", "", "a b", "-x" })
            Assert.Throws<ModuleProtocolException>(() => Parse($$"""{"type":"log","level":"{{level}}","text":"x"}"""));
        Assert.Equal(4096, ((LogMessage)Parse($$"""{"type":"log","level":"info","text":"{{new string('x', 4096)}}"}""")!).Text.Length);
        Assert.Throws<ModuleProtocolException>(() => Parse($$"""{"type":"log","level":"info","text":"{{new string('x', 4097)}}"}"""));
    }

    // «El editor lo pasa al evento `ModuleClient.Log`»
    [Fact]
    public async Task El_editor_pasa_el_log_al_evento()
    {
        TaskCompletionSource<LogMessage> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            long id = Root((await Support.ReadFrameAsync(module))!).GetProperty("id").GetInt64();
            await SendAsync(module, """{"type":"log","level":"debug","text":"Modelo cargado en 840 ms."}""");
            await SendAsync(module, $$"""{"type":"pong","id":{{id}}}""");
        });
        await using (client)
        {
            client.Log += l => received.TrySetResult(l);
            await client.PingAsync().WaitAsync(Support.CiBound);
            Assert.Equal(new LogMessage("debug", "Modelo cargado en 840 ms."), await received.Task.WaitAsync(Support.CiBound));
        }
    }

    // «**El anfitrión de C# no tiene todavía una forma de mandar `log`** (`ModuleCall` no la expone)»
    [Fact]
    public void ModuleCall_no_expone_una_forma_de_mandar_log()
    {
        Assert.DoesNotContain(typeof(ModuleCall).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static),
            m => m.Name.Contains("Log", StringComparison.OrdinalIgnoreCase));
    }

    // «El editor lo manda al cerrar el cliente (`ModuleClient.DisposeAsync`).»
    [Fact]
    public async Task El_editor_manda_shutdown_al_cerrar_el_cliente()
    {
        TaskCompletionSource<string?> shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ModuleClient client = await Support.HostileAsync(async module =>
        {
            shutdown.TrySetResult(await Support.ReadFrameAsync(module));
            module.Dispose();
        });
        await client.DisposeAsync();
        Assert.Equal("""{"type":"shutdown"}""", await shutdown.Task.WaitAsync(Support.CiBound));
        await client.Completion.WaitAsync(Support.CiBound);
    }

    // «El módulo debe cancelar lo que tenga en curso, liberar lo que use y salir.» y «El anfitrión de C# hace lo mismo si
    // el editor cierra la tubería: cancela las llamadas activas, espera hasta 2 s a que terminen […] y sale con 0»
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Con_shutdown_o_al_cerrar_la_tuberia_el_anfitrion_cancela_y_sale_bien(bool sendShutdown)
    {
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (NamedPipeServerStream editor, Task<bool> run, _) = await RawEditorAsync(Host(onWait: ct => started.TrySetResult(ct)));
        await using (editor)
        {
            await SendAsync(editor, """{"type":"invoke","id":1,"function":"esperar"}""");
            CancellationToken token = await started.Task.WaitAsync(Support.CiBound);
            if (sendShutdown) await SendAsync(editor, """{"type":"shutdown"}""");
            else editor.Dispose();
            Assert.True(await run.WaitAsync(Support.CiBound));
            Assert.True(token.IsCancellationRequested);
        }
    }

    // «Una conversación completa, tal como pasó por el cable (saludo, una llamada, un `ping` y el cierre)»
    [Fact]
    public async Task La_conversacion_completa_pasa_tal_cual()
    {
        (NamedPipeServerStream editor, Task<bool> run, string welcome) = await RawEditorAsync(Host());
        await using (editor)
        {
            Assert.StartsWith("""{"type":"welcome","protocol":1,"module":{"id":"prueba",""", welcome, StringComparison.Ordinal);
            await SendAsync(editor, """{"type":"invoke","id":1,"function":"eco","parameters":{"texto":"hola","n":3}}""");
            Assert.Equal("""{"type":"result","id":1,"value":{"texto":"hola","n":3}}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"ping","id":2}""");
            Assert.Equal("""{"type":"pong","id":2}""", await ReceiveAsync(editor));
            await SendAsync(editor, """{"type":"shutdown"}""");
            Assert.True(await run.WaitAsync(Support.CiBound));
            Assert.Null(await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        }
    }
}
