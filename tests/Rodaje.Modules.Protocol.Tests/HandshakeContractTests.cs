using System.IO.Pipes;
using System.Text.Json;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// El contrato de «Saludo y versión» de <c>docs/protocolo-modulos.md</c> (tarea 350): una prueba por promesa, con su frase
/// encima.
/// </summary>
public sealed class HandshakeContractTests
{
    private static string Hello(int min, int max) =>
        $$"""{"type":"hello","minProtocol":{{min}},"maxProtocol":{{max}},"app":"rodaje","appVersion":"0.7.0"}""";

    /// <summary>
    /// Mismo JSON, campo a campo y en orden. El documento muestra los marcos con «ó» y «« »», y el cable los escapa
    /// (<c>\u00F3</c>); es el mismo texto.
    /// </summary>
    private static void SameJson(string expected, string? actual)
    {
        Assert.NotNull(actual);
        using JsonDocument a = JsonDocument.Parse(expected);
        using JsonDocument b = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(a.RootElement, b.RootElement), $"Esperado {expected}, llegó {actual}.");
        Assert.Equal(a.RootElement.EnumerateObject().Select(p => p.Name), b.RootElement.EnumerateObject().Select(p => p.Name));
    }

    private static async Task<(string Hello, Task<ModuleClient> Connect, NamedPipeClientStream Module)> CaptureHelloAsync(ModuleClientOptions? options = null)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<ModuleClient> connect = ModuleClient.ConnectAsync(editor, options);
        string? hello = await Support.ReadFrameAsync(module).WaitAsync(Support.CiBound);
        Assert.NotNull(hello);
        return (hello, connect, module);
    }

    private static async Task<ModuleClient> ReplyAsync(string reply)
    {
        (_, Task<ModuleClient> connect, NamedPipeClientStream module) = await CaptureHelloAsync();
        await module.WriteAsync(Support.Frame(reply));
        return await connect.WaitAsync(Support.CiBound);
    }

    // «el editor manda `hello` y el módulo contesta `welcome`»
    // «La versión actual es `ProtocolVersion.Current = 1` (`MessageChannel.cs`); el editor manda 1–1 por defecto.»
    // «`app` … Por defecto `rodaje`» · «El valor por defecto de la biblioteca es `0.0.0`» · «`culture` … Puede faltar.»
    [Fact]
    public async Task The_editor_sends_hello_first_with_the_default_values()
    {
        Assert.Equal(1, ProtocolVersion.Current);
        (string hello, Task<ModuleClient> connect, NamedPipeClientStream module) = await CaptureHelloAsync();
        Assert.Equal("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.0.0"}""", hello);
        await module.WriteAsync(Support.Frame(Support.WelcomeJson));
        await using ModuleClient client = await connect.WaitAsync(Support.CiBound);
        Assert.Equal(1, client.Protocol);
    }

    // El ejemplo de `hello` del documento, con `culture`.
    [Fact]
    public async Task The_documented_hello_is_what_the_editor_writes()
    {
        (string hello, Task<ModuleClient> connect, NamedPipeClientStream module) =
            await CaptureHelloAsync(new ModuleClientOptions { AppVersion = "0.7.0", Culture = "es-CO" });
        Assert.Equal("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0","culture":"es-CO"}""", hello);
        module.Dispose();
        await Assert.ThrowsAnyAsync<ModuleException>(() => connect.WaitAsync(Support.CiBound));
    }

    // «`minProtocol` ≥ 1 y `maxProtocol` ≥ `minProtocol`.»
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(2, 1)]
    public void A_hello_with_an_invalid_range_is_malformed(int min, int max)
    {
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(Hello(min, max))));
    }

    [Fact]
    public void A_hello_with_a_valid_range_is_accepted()
    {
        Assert.Equal(new HelloMessage(1, 3, "rodaje", "0.7.0"), MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(Hello(1, 3))));
    }

    // «`app` | texto, hasta 64» · «`appVersion` | texto, hasta 64» · «`culture` | texto, hasta 64, opcional»
    [Theory]
    [InlineData("app")]
    [InlineData("appVersion")]
    [InlineData("culture")]
    public void Hello_texts_take_up_to_64_characters(string field)
    {
        static string With(string field, int length)
        {
            Dictionary<string, object> hello = new()
            {
                ["type"] = "hello", ["minProtocol"] = 1, ["maxProtocol"] = 1, ["app"] = "rodaje", ["appVersion"] = "0.7.0",
                [field] = new string('a', length),
            };
            return JsonSerializer.Serialize(hello);
        }

        Assert.IsType<HelloMessage>(MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(With(field, 64))));
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(With(field, 65))));
    }

    // «`app` | texto … `appVersion` | texto»: los dos son obligatorios.
    [Theory]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"appVersion":"0.7.0"}""")]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje"}""")]
    public void A_hello_without_app_or_app_version_is_malformed(string json)
    {
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    // «El módulo no manda nada antes de recibir `hello`.» Lo primero que escribe el anfitrión es su respuesta al saludo.
    [Fact]
    public async Task The_host_writes_nothing_before_hello()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        Task<string?> first = Support.ReadFrameAsync(editor);
        await Task.Delay(100);
        Assert.False(first.IsCompleted, "El módulo escribió antes de recibir «hello».");
        await editor.WriteAsync(Support.Frame(Hello(1, 1)));
        Assert.StartsWith("""{"type":"welcome","protocol":1,""", await first.WaitAsync(Support.CiBound), StringComparison.Ordinal);
        editor.Dispose();
        Assert.True(await host.WaitAsync(Support.CiBound));
    }

    // «El módulo elige la mayor versión común: `min(maxProtocol del módulo, maxProtocol del editor)`, y la acepta si es al
    // menos `max(minProtocol del módulo, minProtocol del editor)`»
    [Theory]
    [InlineData(1, 3, 1, 2, 2)]
    [InlineData(1, 2, 1, 3, 2)]
    [InlineData(2, 4, 1, 5, 4)]
    [InlineData(1, 3, 3, 5, 3)]
    [InlineData(3, 5, 1, 3, 3)]
    public async Task The_host_picks_the_highest_common_version(int moduleMin, int moduleMax, int editorMin, int editorMax, int expected)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost(moduleMin, moduleMax).RunAsync(module);
        await using (ModuleClient client = await ModuleClient.ConnectAsync(editor, new ModuleClientOptions { MinProtocol = editorMin, MaxProtocol = editorMax }))
            Assert.Equal(expected, client.Protocol);
        Assert.True(await host.WaitAsync(Support.CiBound));
    }

    // «Contesta con esa versión y con la descripción de lo que ofrece.» El editor la guarda en `ModuleClient.Module`.
    [Fact]
    public async Task The_welcome_carries_the_module_description()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        _ = Support.EchoHost().RunAsync(module);
        await using ModuleClient client = await ModuleClient.ConnectAsync(editor);
        Assert.Equal(Support.EchoInfo.Id, client.Module.Id);
        Assert.Equal(Support.EchoInfo.Functions.Select(f => f.Id), client.Module.Functions.Select(f => f.Id));
    }

    private const string EchoWelcome =
        """{"type":"welcome","protocol":1,"module":{"id":"rodaje-eco","version":"0.1.0","name":"Eco","functions":[{"id":"eco","name":"Devolver los parámetros","cancellable":true,"reportsProgress":false},{"id":"contar","name":"Contar con avance","cancellable":true,"reportsProgress":true},{"id":"copiar","name":"Copiar el primer búfer en el segundo","cancellable":true,"reportsProgress":false,"inputs":["video"],"outputs":["video"]},{"id":"morir","name":"Terminar el proceso a mitad de una llamada","cancellable":false,"reportsProgress":false},{"id":"colgar","name":"Ignorar la cancelación","cancellable":false,"reportsProgress":false},{"id":"fallar","name":"Lanzar una excepción","cancellable":true,"reportsProgress":false}],"device":"cpu"}}""";

    // El `welcome` del documento se lee y se vuelve a escribir igual.
    [Fact]
    public async Task The_documented_welcome_is_valid_and_round_trips()
    {
        WelcomeMessage welcome = Assert.IsType<WelcomeMessage>(MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(EchoWelcome)));
        Assert.Equal("cpu", welcome.Module.Device);
        using MemoryStream stream = new();
        using (MessageChannel writer = new(stream)) await writer.SendAsync(welcome); // ToArray vale tras cerrar.
        SameJson(EchoWelcome, await Support.ReadFrameAsync(new MemoryStream(stream.ToArray())));
    }

    private static string Welcome(string module, int protocol = 1) => $$"""{"type":"welcome","protocol":{{protocol}},"module":{{module}}}""";

    private static ModuleMessage? ParseWelcome(string module, int protocol = 1) => MessageChannel.Parse(System.Text.Encoding.UTF8.GetBytes(Welcome(module, protocol)));

    private static string Module(string functions = "[]", string extra = "") =>
        $$"""{"id":"m","version":"1.0.0","name":"M","functions":{{functions}}{{extra}}}""";

    // «`protocol` | entero ≥ 1»
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_welcome_with_a_protocol_below_one_is_malformed(int protocol)
    {
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module(), protocol));
    }

    // «`module.id` | identificador»
    [Theory]
    [InlineData("""{"id":"Mayúsculas","version":"1","name":"M","functions":[]}""")]
    [InlineData("""{"id":"","version":"1","name":"M","functions":[]}""")]
    [InlineData("""{"id":"a--b","version":"1","name":"M","functions":[]}""")]
    public void A_module_id_must_be_an_identifier(string module)
    {
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(module));
    }

    // «`module.version` | texto, hasta 64 | … El protocolo no exige un formato»
    [Fact]
    public void Module_version_is_free_text_up_to_64()
    {
        Assert.IsType<WelcomeMessage>(ParseWelcome($$"""{"id":"m","version":"{{new string('v', 64)}}","name":"M","functions":[]}"""));
        Assert.IsType<WelcomeMessage>(ParseWelcome("""{"id":"m","version":"beta 7 (nocturna)","name":"M","functions":[]}"""));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome($$"""{"id":"m","version":"{{new string('v', 65)}}","name":"M","functions":[]}"""));
    }

    // «`module.name` | texto, hasta 200» · «`module.device` | texto, hasta 200, opcional»
    [Fact]
    public void Module_name_and_device_take_up_to_200()
    {
        Assert.IsType<WelcomeMessage>(ParseWelcome($$"""{"id":"m","version":"1","name":"{{new string('n', 200)}}","functions":[]}"""));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome($$"""{"id":"m","version":"1","name":"{{new string('n', 201)}}","functions":[]}"""));
        Assert.Null(Assert.IsType<WelcomeMessage>(ParseWelcome(Module())).Module.Device);
        Assert.IsType<WelcomeMessage>(ParseWelcome(Module(extra: $$""","device":"{{new string('d', 200)}}" """)));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module(extra: $$""","device":"{{new string('d', 201)}}" """)));
    }

    private static string Functions(int count) => "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{"id":"f{{i}}","name":"F"}""")) + "]";

    // «`module.functions` | lista, hasta 128 | Las funciones, sin ids repetidos ni elementos nulos. Puede estar vacía.»
    [Fact]
    public void Functions_take_up_to_128_without_repeats_or_nulls_and_may_be_empty()
    {
        Assert.Empty(Assert.IsType<WelcomeMessage>(ParseWelcome(Module("[]"))).Module.Functions);
        Assert.Equal(128, Assert.IsType<WelcomeMessage>(ParseWelcome(Module(Functions(128)))).Module.Functions.Count);
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module(Functions(129))));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module("""[{"id":"f","name":"F"},{"id":"f","name":"G"}]""")));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module("""[{"id":"f","name":"F"},null]""")));
    }

    // «Puede estar vacía»: también la acepta el anfitrión.
    [Fact]
    public async Task A_module_without_functions_greets()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        _ = new ModuleHost(new ModuleInfo("vacio", "1.0.0", "Vacío", [])).RunAsync(module);
        await using ModuleClient client = await ModuleClient.ConnectAsync(editor);
        Assert.Empty(client.Module.Functions);
    }

    // «`functions[].id` | identificador» · «`functions[].name` | texto, hasta 200»
    [Fact]
    public void Function_id_is_an_identifier_and_name_takes_up_to_200()
    {
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module("""[{"id":"F","name":"F"}]""")));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module("""[{"id":"f.","name":"F"}]""")));
        Assert.IsType<WelcomeMessage>(ParseWelcome(Module($$"""[{"id":"f","name":"{{new string('n', 200)}}"}]""")));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module($$"""[{"id":"f","name":"{{new string('n', 201)}}"}]""")));
    }

    // «`functions[].cancellable` | booleano | … Si falta vale `true`.» · «`functions[].reportsProgress` | booleano | … Si
    // falta vale `true`.»
    [Fact]
    public void Missing_cancellable_and_reports_progress_are_true()
    {
        FunctionInfo f = Assert.IsType<WelcomeMessage>(ParseWelcome(Module("""[{"id":"f","name":"F"}]"""))).Module.Functions[0];
        Assert.True(f.Cancellable);
        Assert.True(f.ReportsProgress);
        FunctionInfo g = Assert.IsType<WelcomeMessage>(ParseWelcome(Module("""[{"id":"f","name":"F","cancellable":false,"reportsProgress":false}]"""))).Module.Functions[0];
        Assert.False(g.Cancellable);
        Assert.False(g.ReportsProgress);
    }

    // «`functions[].inputs`, `functions[].outputs` | listas opcionales, hasta 16 | … el validador solo exige que cada uno
    // sea un identificador.»
    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    public void Buffer_kinds_are_optional_identifiers_up_to_16(string field)
    {
        static string Kinds(int count, string kind = "k") => "[" + string.Join(",", Enumerable.Repeat($"\"{kind}\"", count)) + "]";

        FunctionInfo f = Assert.IsType<WelcomeMessage>(ParseWelcome(Module("""[{"id":"f","name":"F"}]"""))).Module.Functions[0];
        Assert.Null(field == "inputs" ? f.Inputs : f.Outputs);
        Assert.IsType<WelcomeMessage>(ParseWelcome(Module($$"""[{"id":"f","name":"F","{{field}}":{{Kinds(16)}}}]""")));
        Assert.IsType<WelcomeMessage>(ParseWelcome(Module($$"""[{"id":"f","name":"F","{{field}}":["mascara-3d"]}]""")));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module($$"""[{"id":"f","name":"F","{{field}}":{{Kinds(17)}}}]""")));
        Assert.Throws<ModuleProtocolException>(() => ParseWelcome(Module($$"""[{"id":"f","name":"F","{{field}}":{{Kinds(1, "Video")}}}]""")));
    }

    // «El módulo contesta con un `error` sin `id` y el código `version-incompatible`, cuyo mensaje nombra los dos rangos, y
    // cierra. Marco real, con un editor que pide 2–3 y el módulo de eco que habla 1–1»
    [Fact]
    public async Task Without_a_common_version_the_host_answers_the_documented_error_and_closes()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        await editor.WriteAsync(Support.Frame(Hello(2, 3)));
        SameJson("""{"type":"error","code":"version-incompatible","message":"El módulo habla el protocolo 1–1 y el editor 2–3."}""",
            await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        Assert.False(await host.WaitAsync(Support.CiBound));
        Assert.Null(await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
    }

    // «El editor lo convierte en `ModuleVersionException` con ese mensaje (`ModuleClient.HandshakeAsync`)»
    [Fact]
    public async Task The_editor_raises_the_version_error_with_the_module_message()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        _ = Support.EchoHost().RunAsync(module);
        ModuleVersionException ex = await Assert.ThrowsAsync<ModuleVersionException>(
            () => ModuleClient.ConnectAsync(editor, new ModuleClientOptions { MinProtocol = 2, MaxProtocol = 3 }));
        Assert.Equal("El módulo habla el protocolo 1–1 y el editor 2–3.", ex.Message);
    }

    // «y el anfitrión de C# sale con el código 2»
    [Fact]
    public async Task Without_a_common_version_the_host_exits_with_two()
    {
        string name = "Rodaje.Mod.prueba." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream editor = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            ModuleProcess.PipeBufferBytes, ModuleProcess.PipeBufferBytes);
        Task<int> host = Support.EchoHost().RunAsync([ModuleHost.PipeArgument, name]);
        await editor.WaitForConnectionAsync().WaitAsync(Support.CiBound);
        await editor.WriteAsync(Support.Frame(Hello(2, 3)));
        Assert.Contains("version-incompatible", await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound), StringComparison.Ordinal);
        Assert.Equal(2, await host.WaitAsync(Support.CiBound));
    }

    // «El editor también rechaza un `welcome` cuya versión quede fuera de su rango («El módulo eligió el protocolo …; el
    // editor habla …»»
    [Fact]
    public async Task A_welcome_outside_the_editor_range_names_both()
    {
        ModuleVersionException ex = await Assert.ThrowsAsync<ModuleVersionException>(() => ReplyAsync(Welcome(Module(), protocol: 9)));
        Assert.Equal("El módulo eligió el protocolo 9; el editor habla 1–1.", ex.Message);
    }

    // «El editor espera el `welcome` hasta `HandshakeTimeout` (30 s; `ModuleClient.cs`).» · «El anfitrión de C# espera el
    // `hello` hasta su propio `HandshakeTimeout` (30 s; `ModuleHost.cs`). Los dos lados salen de la misma constante,
    // `ModuleClientOptions.DefaultHandshakeTimeout`»
    [Fact]
    public void Both_sides_wait_thirty_seconds_by_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ModuleClientOptions.DefaultHandshakeTimeout);
        Assert.Equal(ModuleClientOptions.DefaultHandshakeTimeout, new ModuleClientOptions().HandshakeTimeout);
        Assert.Equal(ModuleClientOptions.DefaultHandshakeTimeout, new ModuleHost(Support.EchoInfo).HandshakeTimeout);
    }

    // «Si no llega: «El módulo no saludó en 30 s.» (`ModuleProtocolException`).»
    [Fact]
    public async Task A_missing_welcome_fails_with_the_documented_message()
    {
        ManualTime time = new();
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (module)
        {
            Task<ModuleClient> connect = ModuleClient.ConnectAsync(editor, new ModuleClientOptions { Time = time });
            await time.NextTimerAsync();
            time.Advance(ModuleClientOptions.DefaultHandshakeTimeout);
            ModuleProtocolException ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => connect.WaitAsync(Support.CiBound));
            Assert.Equal("El módulo no saludó en 30 s.", ex.Message);
        }
    }

    // «Si el módulo cierra: `ModuleDisconnectedException`.»
    [Fact]
    public async Task A_module_that_closes_while_greeting_disconnects()
    {
        (_, Task<ModuleClient> connect, NamedPipeClientStream module) = await CaptureHelloAsync();
        module.Dispose();
        await Assert.ThrowsAsync<ModuleDisconnectedException>(() => connect.WaitAsync(Support.CiBound));
    }

    // «Cualquier otra respuesta que no sea `welcome` ni `error` `version-incompatible` es un error de protocolo («El módulo
    // no respondió al saludo con «welcome».»).»
    [Theory]
    [InlineData("""{"type":"error","code":"internal","message":"No."}""")]
    [InlineData("""{"type":"error","code":"malformed","message":"No."}""")]
    [InlineData("""{"type":"pong","id":1}""")]
    [InlineData("""{"type":"log","level":"info","text":"hola"}""")]
    [InlineData("""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"0.7.0"}""")]
    public async Task Any_other_reply_to_hello_is_a_protocol_error(string reply)
    {
        ModuleProtocolException ex = await Assert.ThrowsAsync<ModuleProtocolException>(() => ReplyAsync(reply));
        Assert.Equal("El módulo no respondió al saludo con «welcome».", ex.Message);
    }

    // «Si lo primero que llega no es `hello`, contesta un `error` `malformed` sin id y cierra. Marco real, tras recibir
    // `{"type":"ping","id":1}` como primer mensaje»
    [Fact]
    public async Task Anything_but_hello_first_gets_the_documented_malformed_error_and_closes()
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        Task<bool> host = Support.EchoHost().RunAsync(module);
        await editor.WriteAsync(Support.Frame("""{"type":"ping","id":1}"""));
        SameJson("""{"type":"error","code":"malformed","message":"Lo primero tiene que ser «hello»."}""",
            await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
        Assert.False(await host.WaitAsync(Support.CiBound));
        Assert.Null(await Support.ReadFrameAsync(editor).WaitAsync(Support.CiBound));
    }
}
