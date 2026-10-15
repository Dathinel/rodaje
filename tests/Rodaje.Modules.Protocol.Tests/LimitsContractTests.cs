using System.Buffers.Binary;
using System.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Modules.Protocol.Tests;

/// <summary>
/// Contrato de la sección «Límites» de <c>docs/protocolo-modulos.md</c>: una prueba por promesa, con su frase encima.
/// </summary>
public sealed class LimitsContractTests
{
    private static ModuleMessage Ok(string json) => MessageChannel.Parse(Encoding.UTF8.GetBytes(json))!;

    private static void Invalid(string json) =>
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse(Encoding.UTF8.GetBytes(json)));

    private static string Q(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string Welcome(string? id = "eco", string version = "1.0.0", string name = "Eco", string? device = null, string functions = """[{"id":"f","name":"F"}]""") =>
        $$"""{"type":"welcome","protocol":1,"module":{"id":{{Q(id!)}},"version":{{Q(version)}},"name":{{Q(name)}}{{(device is null ? "" : ",\"device\":" + Q(device))}},"functions":{{functions}}""" + "}}";

    private static string Hello(string app = "rodaje", string appVersion = "1.0.0", string? culture = null, int min = 1, int max = 1) =>
        $$"""{"type":"hello","minProtocol":{{min}},"maxProtocol":{{max}},"app":{{Q(app)}},"appVersion":{{Q(appVersion)}}{{(culture is null ? "" : ",\"culture\":" + Q(culture))}}}""";

    private static string Buffers(int n) =>
        "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"region":"Rodaje.Mod.r","offset":{{i}},"length":1}""")) + "]";

    private static async Task<ModuleMessage?> ReceiveFrameAsync(byte[] payload, int? declaredLength = null)
    {
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, declaredLength ?? payload.Length);
        payload.CopyTo(frame, 4);
        using MessageChannel reader = new(new MemoryStream(frame));
        return await reader.ReceiveAsync();
    }

    /// <summary>Rellena un <c>ping</c> con espacios hasta <paramref name="bytes"/> bytes de JSON.</summary>
    private static byte[] PaddedPing(int bytes)
    {
        byte[] head = """{"type":"ping","id":1}"""u8.ToArray();
        byte[] payload = new byte[bytes];
        payload.AsSpan().Fill((byte)' ');
        head.CopyTo(payload, 0);
        return payload;
    }

    // «Un mensaje (JSON, sin la cabecera) | 1 a 1 048 576 bytes (`ProtocolVersion.MaxFrameBytes`, `MessageChannel.cs`)»
    [Fact]
    public async Task A_message_of_exactly_the_maximum_is_accepted()
    {
        Assert.Equal(1_048_576, ProtocolVersion.MaxFrameBytes);
        Assert.Equal(new PingMessage(1), await ReceiveFrameAsync(PaddedPing(1_048_576)));
    }

    // «Un mensaje (JSON, sin la cabecera) | 1 a 1 048 576 bytes»
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_048_577)]
    public async Task A_frame_length_outside_the_range_cuts_the_connection(int declared)
    {
        await Assert.ThrowsAsync<ModuleProtocolException>(() => ReceiveFrameAsync([], declared));
    }

    // «Un mensaje (JSON, sin la cabecera) | 1 a 1 048 576 bytes»: al enviar, uno más largo no se escribe.
    [Fact]
    public async Task Sending_a_message_over_the_maximum_fails_without_writing()
    {
        // Un mensaje válido de más de 1 MiB solo cabe en parameters.
        using MemoryStream big = new();
        using MessageChannel bigWriter = new(big);
        InvokeMessage invoke = new(1, "eco", Support.Json(Q(new string('a', 1_048_576))));
        await Assert.ThrowsAsync<ModuleProtocolException>(() => bigWriter.SendAsync(invoke).AsTask());
        Assert.Equal(0, big.Length);
    }

    // «Anidamiento del JSON | 32 niveles (`MaxDepth`, `MessageChannel.cs`)»
    [Fact]
    public void Json_nesting_up_to_32_levels_is_accepted_and_33_is_not()
    {
        static string Invoke(int levels) =>
            """{"type":"invoke","id":1,"function":"eco","parameters":""" + new string('[', levels - 1) + new string(']', levels - 1) + "}";
        Assert.IsType<InvokeMessage>(Ok(Invoke(32)));
        Invalid(Invoke(33));
    }

    // «Anidamiento del JSON | 32 niveles»: también en un `type` desconocido, que no se salta.
    [Fact]
    public void An_unknown_type_nested_past_32_levels_is_not_skipped()
    {
        Assert.Null(MessageChannel.Parse(Encoding.UTF8.GetBytes("""{"type":"futuro","x":""" + new string('[', 31) + new string(']', 31) + "}")));
        Invalid("""{"type":"futuro","x":""" + new string('[', 32) + new string(']', 32) + "}");
    }

    // «Identificadores (...) | 1 a 64 caracteres: minúsculas ASCII, dígitos, `.` y `-`; sin empezar ni terminar con `.` o
    // `-` y sin dos seguidos (`Limits.Identifier`). Valen `rodaje-eco`, `modulo.vision`, `v2`; no valen `Eco`, `-eco`,
    // `eco-`, `a..b`, `../eco`»
    [Theory]
    [InlineData("rodaje-eco")]
    [InlineData("modulo.vision")]
    [InlineData("v2")]
    [InlineData("a")]
    public void The_documented_valid_identifiers_are_accepted(string id)
    {
        Assert.IsType<WelcomeMessage>(Ok(Welcome(id)));
        Assert.IsType<InvokeMessage>(Ok($$"""{"type":"invoke","id":1,"function":{{Q(id)}}}"""));
        Assert.IsType<ErrorMessage>(Ok($$"""{"type":"error","code":{{Q(id)}},"message":"m"}"""));
        Assert.IsType<LogMessage>(Ok($$"""{"type":"log","level":{{Q(id)}},"text":"t"}"""));
        Assert.IsType<WelcomeMessage>(Ok(Welcome(functions: $$"""[{"id":{{Q(id)}},"name":"F","inputs":[{{Q(id)}}],"outputs":[{{Q(id)}}]}]""")));
        if (id != "a") Assert.Null(MessageChannel.Parse(Encoding.UTF8.GetBytes($$"""{"type":{{Q(id)}}}""")));
    }

    // Misma frase: los que no valen, en cada campo que la tabla nombra.
    [Theory]
    [InlineData("Eco")]
    [InlineData("-eco")]
    [InlineData("eco-")]
    [InlineData("a..b")]
    [InlineData("../eco")]
    [InlineData(".eco")]
    [InlineData("eco.")]
    [InlineData("a.-b")]
    [InlineData("a_b")]
    [InlineData("eco ")]
    [InlineData("ecó")]
    [InlineData("")]
    public void The_documented_invalid_identifiers_are_rejected(string id)
    {
        Invalid(Welcome(id));
        Invalid($$"""{"type":"invoke","id":1,"function":{{Q(id)}}}""");
        Invalid($$"""{"type":"error","code":{{Q(id)}},"message":"m"}""");
        Invalid($$"""{"type":"log","level":{{Q(id)}},"text":"t"}""");
        Invalid(Welcome(functions: $$"""[{"id":{{Q(id)}},"name":"F"}]"""));
        Invalid(Welcome(functions: $$"""[{"id":"f","name":"F","inputs":[{{Q(id)}}]}]"""));
        Invalid(Welcome(functions: $$"""[{"id":"f","name":"F","outputs":[{{Q(id)}}]}]"""));
        Invalid($$"""{"type":{{Q(id)}}}""");
    }

    // «1 a 64 caracteres»
    [Fact]
    public void Identifiers_of_64_characters_are_accepted_and_65_are_not()
    {
        string ok = new('a', 64), tooLong = new('a', 65);
        Assert.IsType<WelcomeMessage>(Ok(Welcome(ok)));
        Assert.IsType<InvokeMessage>(Ok($$"""{"type":"invoke","id":1,"function":{{Q(ok)}}}"""));
        Assert.Null(MessageChannel.Parse(Encoding.UTF8.GetBytes($$"""{"type":{{Q(ok)}}}""")));
        Invalid(Welcome(tooLong));
        Invalid($$"""{"type":"invoke","id":1,"function":{{Q(tooLong)}}}""");
        Invalid($$"""{"type":"error","code":{{Q(tooLong)}},"message":"m"}""");
        Invalid($$"""{"type":"log","level":{{Q(tooLong)}},"text":"t"}""");
        Invalid($$"""{"type":{{Q(tooLong)}}}""");
    }

    // «`app`, `appVersion`, `culture`, `module.version` | hasta 64 caracteres (`MaxId`)»
    [Fact]
    public void Short_texts_allow_64_characters()
    {
        string ok = new('x', 64), tooLong = new('x', 65);
        Assert.IsType<HelloMessage>(Ok(Hello(app: ok, appVersion: ok, culture: ok)));
        Assert.IsType<WelcomeMessage>(Ok(Welcome(version: ok)));
        Invalid(Hello(app: tooLong));
        Invalid(Hello(appVersion: tooLong));
        Invalid(Hello(culture: tooLong));
        Invalid(Welcome(version: tooLong));
    }

    // «`module.name`, `module.device`, `functions[].name`, `progress.stage` | hasta 200 caracteres (`MaxName`)»
    [Fact]
    public void Names_allow_200_characters()
    {
        string ok = new('x', 200), tooLong = new('x', 201);
        Assert.IsType<WelcomeMessage>(Ok(Welcome(name: ok, device: ok, functions: $$"""[{"id":"f","name":{{Q(ok)}}}]""")));
        Assert.IsType<ProgressMessage>(Ok($$"""{"type":"progress","id":1,"fraction":0.5,"stage":{{Q(ok)}}}"""));
        Invalid(Welcome(name: tooLong));
        Invalid(Welcome(device: tooLong));
        Invalid(Welcome(functions: $$"""[{"id":"f","name":{{Q(tooLong)}}}]"""));
        Invalid($$"""{"type":"progress","id":1,"fraction":0.5,"stage":{{Q(tooLong)}}}""");
    }

    // «`error.message`, `log.text` | hasta 4096 caracteres (`MaxText`)»
    [Fact]
    public void Long_texts_allow_4096_characters()
    {
        string ok = new('x', 4096), tooLong = new('x', 4097);
        Assert.IsType<ErrorMessage>(Ok($$"""{"type":"error","code":"internal","message":{{Q(ok)}}}"""));
        Assert.IsType<LogMessage>(Ok($$"""{"type":"log","level":"info","text":{{Q(ok)}}}"""));
        Invalid($$"""{"type":"error","code":"internal","message":{{Q(tooLong)}}}""");
        Invalid($$"""{"type":"log","level":"info","text":{{Q(tooLong)}}}""");
    }

    // «Los largos de texto se cuentan en unidades UTF-16 de .NET (`string.Length`), no en bytes.»
    [Fact]
    public void Lengths_count_utf16_units_not_bytes()
    {
        // «ñ» son 2 bytes en UTF-8 y 1 unidad UTF-16; «😀» son 4 bytes y 2 unidades.
        Assert.IsType<LogMessage>(Ok($$"""{"type":"log","level":"info","text":{{Q(new string('ñ', 4096))}}}"""));
        Invalid($$"""{"type":"log","level":"info","text":{{Q(new string('ñ', 4097))}}}""");
        string emoji = string.Concat(Enumerable.Repeat("😀", 32));
        Assert.Equal(64, emoji.Length);
        Assert.IsType<HelloMessage>(Ok(Hello(app: emoji)));
        Invalid(Hello(app: emoji + "a"));
    }

    // «Funciones en `welcome` | hasta 128, ids sin repetir (`MaxFunctions`)»
    [Fact]
    public void Welcome_allows_128_distinct_functions()
    {
        static string Functions(int n) =>
            "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"id":"f{{i}}","name":"F"}""")) + "]";
        Assert.Equal(128, ((WelcomeMessage)Ok(Welcome(functions: Functions(128)))).Module.Functions.Count);
        Invalid(Welcome(functions: Functions(129)));
        Invalid(Welcome(functions: """[{"id":"f","name":"A"},{"id":"f","name":"B"}]"""));
    }

    // «Búferes por mensaje; `inputs` y `outputs` por función | hasta 16 (`MaxBuffers`)»
    [Fact]
    public void Sixteen_buffers_per_message_and_per_function_list()
    {
        Assert.IsType<InvokeMessage>(Ok($$"""{"type":"invoke","id":1,"function":"eco","buffers":{{Buffers(16)}}}"""));
        Assert.IsType<ResultMessage>(Ok($$"""{"type":"result","id":1,"buffers":{{Buffers(16)}}}"""));
        Invalid($$"""{"type":"invoke","id":1,"function":"eco","buffers":{{Buffers(17)}}}""");
        Invalid($$"""{"type":"result","id":1,"buffers":{{Buffers(17)}}}""");

        static string Kinds(int n) => "[" + string.Join(",", Enumerable.Repeat("\"video\"", n)) + "]";
        Assert.IsType<WelcomeMessage>(Ok(Welcome(functions: $$"""[{"id":"f","name":"F","inputs":{{Kinds(16)}},"outputs":{{Kinds(16)}}}]""")));
        Invalid(Welcome(functions: $$"""[{"id":"f","name":"F","inputs":{{Kinds(17)}}}]"""));
        Invalid(Welcome(functions: $$"""[{"id":"f","name":"F","outputs":{{Kinds(17)}}}]"""));
    }

    // «`id` de `invoke`, `progress`, `result`, `error`, `cancel` | entero de 64 bits mayor que 0»
    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("-9223372036854775808", false)]
    [InlineData("1", true)]
    [InlineData("9223372036854775807", true)]
    public void Call_ids_are_positive_64_bit_integers(string id, bool valid)
    {
        string[] messages =
        [
            $$"""{"type":"invoke","id":{{id}},"function":"eco"}""",
            $$"""{"type":"progress","id":{{id}},"fraction":0}""",
            $$"""{"type":"result","id":{{id}}}""",
            $$"""{"type":"error","id":{{id}},"code":"internal","message":"m"}""",
            $$"""{"type":"cancel","id":{{id}}}""",
        ];
        foreach (string json in messages)
            if (valid) Assert.NotNull(Ok(json));
            else Invalid(json);
    }

    // Misma frase: «entero de 64 bits»; uno que no cabe en un long o con decimales es malformado.
    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    public void Call_ids_outside_a_64_bit_integer_are_rejected(string id)
    {
        Invalid($$"""{"type":"cancel","id":{{id}}}""");
        Invalid($$"""{"type":"ping","id":{{id}}}""");
    }

    // «`id` de `ping` y `pong` | cualquier entero de 64 bits (no se valida)»
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void Ping_and_pong_take_any_64_bit_id(long id)
    {
        Assert.Equal(new PingMessage(id), Ok($$"""{"type":"ping","id":{{id}}}"""));
        Assert.Equal(new PongMessage(id), Ok($$"""{"type":"pong","id":{{id}}}"""));
    }

    // «`progress.fraction` | finito, de 0 a 1»
    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("0.25", true)]
    [InlineData("-0.0001", false)]
    [InlineData("1.0001", false)]
    [InlineData("1e400", false)]
    [InlineData("\"NaN\"", false)]
    [InlineData("\"Infinity\"", false)]
    public void Progress_fraction_is_finite_between_0_and_1(string fraction, bool valid)
    {
        string json = $$"""{"type":"progress","id":1,"fraction":{{fraction}}}""";
        if (valid) Assert.IsType<ProgressMessage>(Ok(json));
        else Invalid(json);
    }

    // «`hello.minProtocol`, `welcome.protocol` | ≥ 1; `maxProtocol` ≥ `minProtocol`»
    [Fact]
    public void Protocol_versions_start_at_1_and_max_is_not_below_min()
    {
        Assert.IsType<HelloMessage>(Ok(Hello(min: 1, max: 1)));
        Assert.IsType<HelloMessage>(Ok(Hello(min: 2, max: 5)));
        Invalid(Hello(min: 0, max: 1));
        Invalid(Hello(min: -1, max: 1));
        Invalid(Hello(min: 3, max: 2));
        Invalid(Welcome().Replace("\"protocol\":1", "\"protocol\":0"));
        Invalid(Welcome().Replace("\"protocol\":1", "\"protocol\":-1"));
    }

    // «Nombre de región o de tubería | 1 a 128 caracteres: letras y dígitos ASCII, `.`, `-`, `_` (`Limits.RegionName`)»
    [Theory]
    [InlineData("a", true)]
    [InlineData("Rodaje.Mod.ABC_def-01", true)]
    [InlineData("..", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("región", false)]
    [InlineData("a:b", false)]
    public void Region_and_pipe_names_use_a_restricted_alphabet(string name, bool valid)
    {
        Assert.Equal(valid, SharedMemoryRegion.IsValidName(name));
        string json = $$"""{"type":"invoke","id":1,"function":"eco","buffers":[{"region":{{Q(name)}},"offset":0,"length":1}]}""";
        if (valid) Assert.IsType<InvokeMessage>(Ok(json));
        else Invalid(json);
    }

    // Misma frase: «1 a 128 caracteres».
    [Fact]
    public void Region_names_allow_128_characters()
    {
        Assert.True(SharedMemoryRegion.IsValidName(new string('R', 128)));
        Assert.False(SharedMemoryRegion.IsValidName(new string('R', 129)));
        Assert.False(SharedMemoryRegion.IsValidName(null));
        Invalid($$"""{"type":"result","id":1,"buffers":[{"region":{{Q(new string('R', 129))}},"offset":0,"length":1}]}""");
    }

    // Misma frase, del lado del anfitrión: una tubería con un nombre inválido no se abre.
    [Theory]
    [InlineData("")]
    [InlineData("../tubo")]
    [InlineData("tubo con espacios")]
    public async Task The_host_rejects_an_invalid_pipe_name(string name)
    {
        ModuleHost host = new(Support.EchoInfo);
        Assert.Equal(64, await host.RunAsync([ModuleHost.PipeArgument, name]));
    }

    // «Todo lo que llega se valida; un valor fuera de rango hace inválido el mensaje y **corta la conexión**»
    [Fact]
    public async Task An_invalid_message_ends_the_reading_with_a_protocol_error()
    {
        await Assert.ThrowsAsync<ModuleProtocolException>(() =>
            ReceiveFrameAsync(Encoding.UTF8.GetBytes("""{"type":"cancel","id":0}""")));
    }

    // «Conectarse a la tubería (editor) | 30 s (`HandshakeTimeout`)», «Contestar el saludo con `welcome` (editor) | 30 s»,
    // «Recibir `hello` (anfitrión) | 30 s (`HandshakeTimeout`)», «Confirmar un `cancel` | 10 s (`CancelGrace`;
    // `DefaultCancelGrace`, desde #219)», «Escribir un mensaje hacia el módulo (...) | 10 s (`WriteTimeout`)» y
    // «Llamadas a la vez en el anfitrión de C# | 4 (`MaxConcurrentCalls`)». El comportamiento al vencer lo cubren
    // HandshakeTimeoutTests, CancelGraceTests y WriteTimeoutTests; aquí, los valores por defecto.
    [Fact]
    public void Default_timeouts_and_counts_match_the_table()
    {
        ModuleClientOptions options = new();
        Assert.Equal(TimeSpan.FromSeconds(30), options.HandshakeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), ModuleClientOptions.DefaultCancelGrace);
        Assert.Equal(TimeSpan.FromSeconds(10), options.CancelGrace);
        Assert.Equal(TimeSpan.FromSeconds(10), options.WriteTimeout);
        ModuleHost host = new(Support.EchoInfo);
        Assert.Equal(TimeSpan.FromSeconds(30), host.HandshakeTimeout);
        Assert.Equal(4, host.MaxConcurrentCalls);
    }
}
