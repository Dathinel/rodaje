using System.Globalization;
using System.IO.Pipes;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Rodaje.Modules.Protocol.Tests.Fuzz;

/// <summary>
/// Más mensajes hostiles del protocolo de módulos (tarea 78), además del fuzz de la tarea 25: tipos y nulos donde no
/// van, metadatos repetidos, Unicode roto en campos desconocidos, desbordes en las formas de los búferes, mensajes de
/// tipo desconocido que no son JSON, y secuencias contra el <see cref="ModuleHost"/> real (un segundo <c>hello</c>, una
/// llamada repetida, mensajes que solo manda el módulo). Semillas desde <see cref="Seed"/>.
/// </summary>
[Collection(FuzzGroup.Name)]
public sealed class HostileMessageTests(ITestOutputHelper output)
{
    /// <summary>Semilla base de esta tarea (la del fuzz de la tarea 25 es <see cref="FuzzSupport.BaseSeed"/>).</summary>
    private const int Seed = 20261012;

    private const string Hello = """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""";

    private static ModuleMessage? Parse(string json) => MessageChannel.Parse(Encoding.UTF8.GetBytes(json));

    private static void Rejected(string json) => Assert.Throws<ModuleProtocolException>(() => Parse(json));

    private static string Region(string extra = "", long length = 4096) =>
        $$"""{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Rodaje.Mod.a","offset":0,"length":{{length}}{{extra}}}]}""";

    // ---------------------------------------------------------------- Mensajes sueltos

    public static TheoryData<string> Malformed() =>
    [
        // Nulos y tipos donde no van.
        """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":null,"appVersion":"1"}""",
        """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje"}""",
        """{"type":"hello","minProtocol":"1","maxProtocol":1,"app":"rodaje","appVersion":"1"}""",
        """{"type":"hello","minProtocol":1.0,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""",
        """{"type":"hello","minProtocol":0,"maxProtocol":1,"app":"rodaje","appVersion":"1"}""",
        """{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"rodaje","appVersion":"1","culture":1}""",
        """{"type":"welcome","protocol":1,"module":null}""",
        """{"type":"welcome","protocol":1}""",
        """{"type":"welcome","protocol":0,"module":{"id":"x","version":"1","name":"X","functions":[]}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":null}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X"}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[{"id":"a","name":"A","cancellable":"true"}]}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[{"id":"a","name":"A","inputs":[null]}]}}""",
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[{"id":"a","name":"A","inputs":["Video"]}]}}""",
        """{"type":"invoke","id":1,"function":null}""",
        """{"type":"invoke","id":true,"function":"eco"}""",
        """{"type":"invoke","id":1.0,"function":"eco"}""",
        """{"type":"invoke","id":9223372036854775808,"function":"eco"}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":{}}""",
        """{"type":"progress","id":1,"fraction":"0.5"}""",
        """{"type":"progress","id":1,"fraction":null}""",
        """{"type":"progress","id":1,"fraction":1e400}""",
        """{"type":"progress","id":1,"fraction":0.5,"stage":5}""",
        """{"type":"result","id":0}""",
        """{"type":"error","code":"internal"}""",
        """{"type":"error","code":"Internal","message":"x"}""",
        """{"type":"error","code":"internal","message":"x","id":-1}""",
        """{"type":"log","level":"info"}""",
        """{"type":"log","level":"","text":"x"}""",
        """{"type":"ping"}""",
        """{"type":"ping","id":"1"}""",
        // El tipo mal escrito o en otro sitio.
        """{"Type":"ping","id":1}""",
        """{"TYPE":"ping","id":1}""",
        """{"type":"Ping","id":1}""",
        """{"type":"ping ","id":1}""",
        """{"type":" ping","id":1}""",
        """{"type":"","id":1}""",
        """{"type":1,"id":1}""",
        """{"type":null,"id":1}""",
        """{"type":["ping"],"id":1}""",
        """{"id":1}""",
        """{"x":{"type":"ping"},"id":1}""",
        // JSON que no es uno solo, o con extensiones que el protocolo no admite.
        """{"type":"ping","id":1} {"type":"ping","id":2}""",
        """{"type":"ping","id":1}x""",
        """{"type":"ping","id":1,}""",
        """{"type":"ping",/*c*/"id":1}""",
        """{'type':'ping','id':1}""",
        """{"type":"ping","id":01}""",
        """{"type":"ping","id":0x1}""",
        """{"type":"ping","id":NaN}""",
        """[{"type":"ping","id":1}]""",
        "\"ping\"",
        "1",
        "true",
        // Búferes al borde.
        Region(length: 0),
        Region(length: 2147483648),
        Region("""
            ,"video":{"width":0,"height":1,"stride":4,"pixelFormat":"rgba8"}
            """.Trim()),
        Region(""","video":{"width":16385,"height":1,"stride":65540,"pixelFormat":"rgba8"}""", 65540),
        Region(""","video":{"width":1,"height":1,"stride":-4,"pixelFormat":"rgba8"}"""),
        Region(""","video":{"width":1,"height":1,"stride":4,"pixelFormat":"RGBA8"}"""),
        Region(""","video":{"width":1,"height":1,"stride":4,"pixelFormat":null}"""),
        Region(""","video":{"width":16384,"height":16384,"stride":2147483647,"pixelFormat":"gray8"}""", 2147483647),
        Region(""","video":{"width":1,"height":1,"stride":4,"pixelFormat":"rgba8"},"audio":{"sampleRate":48000,"channels":1,"frames":1}"""),
        Region(""","audio":{"sampleRate":7999,"channels":1,"frames":1}"""),
        Region(""","audio":{"sampleRate":384001,"channels":1,"frames":1}"""),
        Region(""","audio":{"sampleRate":48000,"channels":0,"frames":1}"""),
        Region(""","audio":{"sampleRate":48000,"channels":33,"frames":1}"""),
        Region(""","audio":{"sampleRate":48000,"channels":1,"frames":-1}"""),
        Region(""","audio":{"sampleRate":48000,"channels":1,"frames":1025}"""),
        Region(""","audio":{"sampleRate":48000,"channels":1,"frames":1,"sampleFormat":"s16"}"""),
        Region(""","audio":{"sampleRate":48000,"channels":1,"frames":1,"sampleFormat":"F32"}"""),
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"","offset":0,"length":1}]}""",
        $$"""{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"{{new string('r', 129)}}","offset":0,"length":1}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Rodaje.Mod.a/b","offset":0,"length":1}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Rodaje.Mod.ñ","offset":0,"length":1}]}""",
        """{"type":"invoke","id":1,"function":"eco","buffers":[{"region":"Rodaje.Mod.a","offset":9223372036854775807,"length":1}]}""",
        // Unicode roto dentro de textos conocidos y de parameters.
        """{"type":"log","level":"info","text":"\ud800"}""",
        """{"type":"log","level":"info","text":"a\udc00b"}""",
        """{"type":"invoke","id":1,"function":"eco","parameters":{"\ud800":1}}""",
        """{"type":"invoke","id":1,"function":"eco","parameters":[[[["\udfff"]]]]}""",
        """{"type":"result","id":1,"value":{"a":{"b":"\ud83d"}}}""",
        // Anidamiento justo pasado el máximo de 32 en todo el mensaje.
        """{"type":"invoke","id":1,"function":"eco","parameters":""" + new string('[', 32) + new string(']', 32) + "}",
    ];

    private static readonly string[] MalformedList = [.. Malformed().Cast<object[]>().Select(row => (string)row[0])];

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Los_mensajes_mal_hechos_dan_un_error_de_protocolo(string json) => Rejected(json);

    public static TheoryData<string, Type> Accepted() => new()
    {
        { """{"type":"invoke","id":9223372036854775807,"function":"eco"}""", typeof(InvokeMessage) },
        { """{"type":"invoke","id":1,"function":"eco","parameters":"😀"}""", typeof(InvokeMessage) },
        { """{"type":"invoke","id":1,"function":"eco","parameters":null}""", typeof(InvokeMessage) },
        { """{"type":"invoke","id":1,"function":"eco","parameters":""" + new string('[', 30) + new string(']', 30) + "}", typeof(InvokeMessage) },
        { """{"type":"invoke","id":1,"function":"eco","buffers":[]}""", typeof(InvokeMessage) },
        { Region(""","video":{"width":16384,"height":1,"stride":65536,"pixelFormat":"rgba8"}""", 65536), typeof(InvokeMessage) },
        { Region(""","audio":{"sampleRate":384000,"channels":32,"frames":0}""", 1), typeof(InvokeMessage) },
        { Region(""","audio":{"sampleRate":8000,"channels":1,"frames":1024}""", 4096), typeof(InvokeMessage) },
        { """{"type":"progress","id":1,"fraction":-0.0}""", typeof(ProgressMessage) },
        { """{"type":"progress","id":1,"fraction":4.9e-324}""", typeof(ProgressMessage) },
        { """{"type":"progress","id":1,"fraction":1E0}""", typeof(ProgressMessage) },
        { """{"type":"ping","id":-9223372036854775808}""", typeof(PingMessage) },
        { """{"type":"pong","id":0}""", typeof(PongMessage) },
        { """{"type":"log","level":"warn","text":""}""", typeof(LogMessage) },
        { """{"type":"error","code":"futuro.codigo","message":""}""", typeof(ErrorMessage) },
        { """{"type":"hello","minProtocol":1,"maxProtocol":2147483647,"app":"rodaje","appVersion":"1","culture":null}""", typeof(HelloMessage) },
        { """  {"type":"shutdown"}  """, typeof(ShutdownMessage) },
        { """{"type":"shutdown","id":"lo que sea"}""", typeof(ShutdownMessage) },
        { """{"id":1,"function":"eco","type":"invoke"}""", typeof(InvokeMessage) },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Los_mensajes_en_el_borde_se_aceptan_y_vuelven_a_escribirse_igual(string json, Type type)
    {
        ModuleMessage? message = Parse(json);
        Assert.NotNull(message);
        Assert.IsType(type, message);
        byte[] again = JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.ModuleMessage);
        Assert.Equal(again, JsonSerializer.SerializeToUtf8Bytes(MessageChannel.Parse(again), ProtocolJsonContext.Default.ModuleMessage));
    }

    [Theory]
    [InlineData("""{"type":"ping","type":"cancel","id":1}""")]
    [InlineData("""{"type":"futuro","type":"ping","id":1}""")]
    [InlineData("""{"type":"ping","type":"futuro","id":1}""")]
    [InlineData("""{"type":"invoke","id":1,"id":2,"function":"eco"}""")]
    [InlineData("""{"type":"invoke","id":1,"function":"eco","function":"Eco"}""")]
    [InlineData("""{"type":"invoke","id":1,"function":"eco","function":"eco"}""")]
    public void Las_propiedades_repetidas_dan_un_mensaje_valido_o_un_error_de_protocolo(string json)
    {
        ModuleMessage? message;
        try
        {
            message = Parse(json);
        }
        catch (ModuleProtocolException)
        {
            return;
        }

        // Si se acepta, cumple las reglas (o es un tipo desconocido que se salta).
        Assert.Null(message?.Validate());
    }

    [Fact]
    public void Los_textos_en_el_limite_valen_y_uno_mas_no()
    {
        static string Log(int length) => $$"""{"type":"log","level":"info","text":"{{new string('x', length)}}"}""";
        static string Hello(int length) => $$"""{"type":"hello","minProtocol":1,"maxProtocol":1,"app":"{{new string('a', length)}}","appVersion":"1"}""";
        static string Stage(int length) => $$"""{"type":"progress","id":1,"fraction":1,"stage":"{{new string('s', length)}}"}""";
        static string Welcome(int functions) => """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":["""
            + string.Join(",", Enumerable.Range(0, functions).Select(i => $$"""{"id":"f{{i}}","name":"F"}""")) + "]}}";
        static string Inputs(int count) => """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[{"id":"f","name":"F","inputs":["""
            + string.Join(",", Enumerable.Repeat("\"video\"", count)) + "]}]}}";
        static string Buffers(int count) => $$"""{"type":"invoke","id":1,"function":"eco","buffers":[{{string.Join(",", Enumerable.Repeat("""{"region":"r","offset":0,"length":1}""", count))}}]}""";
        static string Ident(int length) => $$"""{"type":"invoke","id":1,"function":"{{new string('e', length)}}"}""";

        foreach ((Func<int, string> build, int limit) in new (Func<int, string>, int)[] { (Log, 4096), (Hello, 64), (Stage, 200), (Welcome, 128), (Inputs, 16), (Buffers, 16), (Ident, 64) })
        {
            Assert.NotNull(Parse(build(limit)));
            Rejected(build(limit + 1));
        }

        // Los límites cuentan unidades UTF-16: 2048 emojis caben en 4096, 2049 no.
        Assert.NotNull(Parse($$"""{"type":"log","level":"info","text":"{{string.Concat(Enumerable.Repeat("🎬", 2048))}}"}"""));
        Rejected($$"""{"type":"log","level":"info","text":"{{string.Concat(Enumerable.Repeat("🎬", 2049))}}"}""");
    }

    [Fact]
    public void Un_bom_al_principio_del_marco_no_rompe_nada()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. """{"type":"ping","id":1}"""u8];
        try
        {
            Assert.IsType<PingMessage>(MessageChannel.Parse(json));
        }
        catch (ModuleProtocolException)
        {
        }
    }

    /// <summary>
    /// Formas de búfer al azar con números grandes: si el mensaje se acepta, el cuadro o el audio caben de verdad en el
    /// búfer (cuentas en <see cref="BigInteger"/>, sin desbordes). La de audio está omitida por el defecto de abajo.
    /// </summary>
    [Fact]
    public void Las_formas_de_video_aceptadas_caben_en_su_bufer()
    {
        var random = new Random(Seed);
        int accepted = 0;
        for (int i = 0; i < 20_000; i++)
        {
            long length = Big(random, int.MaxValue);
            long width = Big(random, 20_000), height = Big(random, 20_000), stride = Big(random, int.MaxValue);
            string format = new[] { "rgba8", "bgra8", "rgba16f", "gray8" }[random.Next(4)];
            string json = Region(string.Create(CultureInfo.InvariantCulture, $$""","video":{"width":{{width}},"height":{{height}},"stride":{{stride}},"pixelFormat":"{{format}}"}"""), length);
            InvokeMessage? message = TryParse(json);
            if (message is null)
            {
                continue;
            }

            accepted++;
            VideoLayout v = message.Buffers![0].Video!;
            BigInteger bpp = PixelFormats.BytesPerPixel(v.PixelFormat);
            Assert.True(v.Width is >= 1 and <= 16384 && v.Height is >= 1 and <= 16384, json);
            Assert.True(v.Stride >= v.Width * bpp && (BigInteger)v.Stride * v.Height <= message.Buffers[0].Length, json);
        }

        output.WriteLine($"{accepted} formas de video aceptadas.");
        Assert.True(accepted > 0);
    }

    /// <summary>
    /// Defecto: <c>SharedBufferRef.Validate</c> comprueba el audio con <c>a.Frames * a.Channels * 4L &gt; Length</c> en
    /// <c>long</c> sin control de desborde. Con <c>frames</c> = 2⁶² y un canal el producto da 2⁶⁴, que en 64 bits es 0, y
    /// el mensaje pasa como si el audio cupiera en un búfer de 1 byte; con 2⁶² + 1 da 4. Lo aceptan los dos lados: el
    /// editor en un <c>result</c> de un módulo hostil y el módulo en un <c>invoke</c>. Quien confíe en <c>frames</c> para
    /// recorrer el búfer se sale de él (en C#, <c>ModuleCall.Map</c> limita el <c>Span</c> al largo y lanzaría; en otro
    /// lenguaje, lee o escribe fuera). El producto también puede salir negativo: con la semilla 20261013 del bucle de
    /// abajo se aceptan 7175 de 20 000 formas que no caben; la primera, el caso 2 (frames = 9223372036854775805, 23
    /// canales, largo 2147483647). Los ejemplos fijos lo reproducen siempre. Lo que debería pasar: rechazarlo como «el audio no cabe en el búfer»
    /// (por ejemplo, comparando <c>Frames &gt; Length / (Channels * 4)</c>).
    /// </summary>
    [Fact(Skip = "Defecto: SharedBufferRef.Validate desborda frames × channels × 4 y acepta audio que no cabe (frames = 4611686018427387904, 1 canal, largo 1).")]
    public void Las_formas_de_audio_aceptadas_caben_en_su_bufer()
    {
        Rejected(Region(""","audio":{"sampleRate":48000,"channels":1,"frames":4611686018427387904}""", 1));
        Rejected(Region(""","audio":{"sampleRate":48000,"channels":2,"frames":2305843009213693952}""", 1));
        Rejected(Region(""","audio":{"sampleRate":48000,"channels":1,"frames":4611686018427387905}""", 4));
        Rejected("""{"type":"result","id":1,"buffers":[{"region":"Rodaje.Mod.a","offset":0,"length":1,"audio":{"sampleRate":48000,"channels":32,"frames":144115188075855872}}]}""");

        var random = new Random(Seed + 1);
        for (int i = 0; i < 20_000; i++)
        {
            long length = Big(random, int.MaxValue);
            long frames = random.Next(3) == 0 ? (1L << random.Next(55, 63)) + random.Next(4) : Big(random, long.MaxValue);
            int channels = random.Next(1, 33);
            string json = Region(string.Create(CultureInfo.InvariantCulture, $$""","audio":{"sampleRate":48000,"channels":{{channels}},"frames":{{frames}}}"""), length);
            if (TryParse(json) is { } message)
            {
                AudioLayout a = message.Buffers![0].Audio!;
                Assert.True((BigInteger)a.Frames * a.Channels * 4 <= message.Buffers[0].Length, $"[semilla {Seed + 1}, caso {i}] {json}");
            }
        }
    }

    /// <summary>
    /// Defecto: un mensaje de <c>type</c> desconocido se salta en cuanto el lector ve el <c>type</c>, sin comprobar que el
    /// resto sea JSON ni UTF-8 válido. <c>{"type":"futuro",</c> (cortado) o <c>{"type":"futuro","x":</c> seguido de
    /// basura se saltan como compatibilidad hacia delante, aunque docs/protocolo-modulos.md dice que un marco «que no es
    /// JSON válido en UTF-8» corta la conexión y que solo se salta un mensaje de tipo desconocido. Un módulo roto puede
    /// así mandar basura sin que el editor lo note mientras empiece por un <c>type</c> nuevo. Lo que debería pasar: un
    /// marco que no es JSON completo y válido es malformado aunque su <c>type</c> sea desconocido.
    /// </summary>
    [Fact(Skip = "Defecto: MessageChannel.Parse salta como tipo desconocido un marco que no es JSON válido ({\"type\":\"futuro\", cortado).")]
    public void Un_tipo_desconocido_que_no_es_json_valido_es_malformado()
    {
        Rejected("""{"type":"futuro",""");
        Rejected("""{"type":"futuro","x":}}}}""");
        Rejected("""{"type":"futuro"} basura""");
        Assert.Throws<ModuleProtocolException>(() => MessageChannel.Parse([.. """{"type":"futuro","x":" """u8, 0xFF, 0xFE, .. "\"}"u8]));
    }

    /// <summary>
    /// Lo que sí se salta: un tipo desconocido bien escrito, con cualquier contenido JSON válido. Y un tipo que no es
    /// identificador no se salta.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"futuro"}""", true)]
    [InlineData("""{"x":[1,{"type":"ping"}],"type":"v2.mensaje-nuevo","y":null}""", true)]
    [InlineData("""{"type":"futuro","id":"no es un número","fraction":99}""", true)]
    [InlineData("""{"type":"a..b"}""", false)]
    [InlineData("""{"type":"-futuro"}""", false)]
    [InlineData("""{"type":"futuro\u0000"}""", false)]
    [InlineData("""{"type":"\ud800"}""", false)]
    public void Los_tipos_desconocidos_validos_se_saltan(string json, bool skipped)
    {
        if (skipped)
        {
            Assert.Null(Parse(json));
        }
        else
        {
            Rejected(json);
        }
    }

    /// <summary>
    /// Observación (no se omite, pasa): un sustituto suelto en un campo desconocido de un mensaje conocido no hace
    /// inválido el mensaje, porque el campo se ignora sin leerse. docs/protocolo-modulos.md dice «en cualquier texto o
    /// nombre de propiedad»; aquí se fija lo que pasa hoy para notar si cambia. Nunca llega a la app: el campo se descarta.
    /// </summary>
    [Fact]
    public void Un_sustituto_suelto_en_un_campo_desconocido_se_descarta_con_el_campo()
    {
        ModuleMessage? message = Parse("""{"type":"ping","id":1,"nuevo":"\ud800"}""");
        Assert.Equal(new PingMessage(1), message);
    }

    // ---------------------------------------------------------------- Contra el anfitrión real

    /// <summary>Saluda, manda <paramref name="frames"/> y devuelve lo que el módulo contestó y cómo terminó.</summary>
    private static async Task<(bool Result, List<string> Replies)> AgainstHostAsync(ModuleHost host, params string[] frames)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> run = host.RunAsync(module);
            await editor.WriteAsync(Support.Frame(Hello));
            Assert.Contains("\"welcome\"", Encoding.UTF8.GetString((await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "el welcome"))!), StringComparison.Ordinal);
            Task<List<byte[]>> drain = FuzzSupport.DrainAsync(editor);
            foreach (string frame in frames)
            {
                if (!await FuzzSupport.TryWriteAsync(editor, Support.Frame(frame)))
                {
                    break;
                }
            }

            // Si el módulo no cortó, se cierra como el editor: así RunAsync termina y la lectura también.
            await Task.WhenAny(run, Task.Delay(300));
            if (!run.IsCompleted)
            {
                await FuzzSupport.TryWriteAsync(editor, Support.Frame("""{"type":"shutdown"}"""));
            }

            bool result = await FuzzSupport.Capped(run, "RunAsync");
            module.Dispose();
            List<byte[]> replies = await FuzzSupport.Capped(drain, "la lectura");
            return (result, [.. replies.Select(r => Encoding.UTF8.GetString(r))]);
        }
    }

    public static TheoryData<string> EditorOnlyAfterHello() =>
    [
        Hello,
        """{"type":"welcome","protocol":1,"module":{"id":"x","version":"1","name":"X","functions":[]}}""",
        """{"type":"result","id":1}""",
        """{"type":"progress","id":1,"fraction":0.5}""",
        """{"type":"error","code":"internal","message":"x","id":1}""",
        """{"type":"error","code":"internal","message":"x"}""",
        """{"type":"pong","id":1}""",
        """{"type":"log","level":"info","text":"x"}""",
    ];

    [Theory]
    [MemberData(nameof(EditorOnlyAfterHello))]
    public async Task Lo_que_el_editor_no_manda_cierra_el_modulo_con_malformed(string frame)
    {
        (bool result, List<string> replies) = await AgainstHostAsync(Support.EchoHost(), frame, """{"type":"ping","id":5}""");
        Assert.False(result);
        string last = Assert.Single(replies);
        Assert.Contains("\"malformed\"", last, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Una_llamada_con_el_id_de_otra_en_curso_cierra_el_modulo()
    {
        (bool result, List<string> replies) = await AgainstHostAsync(Support.EchoHost(),
            """{"type":"invoke","id":7,"function":"contar","parameters":{"pasos":100,"msPorPaso":20}}""",
            """{"type":"invoke","id":7,"function":"eco"}""");
        Assert.False(result);
        Assert.Contains(replies, r => r.Contains("\"malformed\"", StringComparison.Ordinal) && r.Contains('7', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pings_raros_cancelaciones_ajenas_y_tipos_futuros_no_cortan_la_conexion()
    {
        List<string> frames =
        [
            """{"type":"ping","id":-9223372036854775808}""",
            """{"type":"ping","id":0}""",
            """{"type":"cancel","id":123456}""",
            """{"type":"cancel","id":9223372036854775807}""",
            """{"type":"invoke","id":3,"function":"no-existe"}""",
            """{"type":"invoke","id":4,"function":"eco","parameters":{"texto":"ñandú 🎬 😀"}}""",
        ];
        for (int i = 0; i < 2000; i++)
        {
            frames.Add($$"""{"type":"futuro-{{i}}","carga":[{{i}},{"type":"ping","id":{{i}}}]}""");
        }

        frames.Add("""{"type":"ping","id":9}""");
        (bool result, List<string> replies) = await AgainstHostAsync(Support.EchoHost(), [.. frames]);
        Assert.True(result);
        Assert.Contains("""{"type":"pong","id":-9223372036854775808}""", replies);
        Assert.Contains("""{"type":"pong","id":0}""", replies);
        Assert.Contains(replies, r => r.Contains("\"unknown-function\"", StringComparison.Ordinal) && r.Contains("\"id\":3", StringComparison.Ordinal));
        string eco = Assert.Single(replies, r => r.Contains("\"result\"", StringComparison.Ordinal));
        Assert.Equal("ñandú 🎬 😀", JsonDocument.Parse(eco).RootElement.GetProperty("value").GetProperty("texto").GetString());
        Assert.Contains("""{"type":"pong","id":9}""", replies);
    }

    [Theory]
    [InlineData(1, 2147483647, true)]
    [InlineData(2147483647, 2147483647, false)]
    [InlineData(2, 5, false)]
    public async Task Un_saludo_con_versiones_extremas_elige_bien_o_dice_version_incompatible(int min, int max, bool works)
    {
        (NamedPipeServerStream editor, NamedPipeClientStream module) = await Support.PipePairAsync();
        using (editor)
        using (module)
        {
            Task<bool> run = Support.EchoHost().RunAsync(module);
            await editor.WriteAsync(Support.Frame($$"""{"type":"hello","minProtocol":{{min}},"maxProtocol":{{max}},"app":"rodaje","appVersion":"1"}"""));
            string reply = Encoding.UTF8.GetString((await FuzzSupport.Capped(FuzzSupport.ReadFrameAsync(editor), "la respuesta al saludo"))!);
            if (works)
            {
                Assert.Equal(1, JsonDocument.Parse(reply).RootElement.GetProperty("protocol").GetInt32());
                editor.Dispose();
                Assert.True(await FuzzSupport.Capped(run, "RunAsync"));
            }
            else
            {
                Assert.Contains("\"version-incompatible\"", reply, StringComparison.Ordinal);
                Assert.False(await FuzzSupport.Capped(run, "RunAsync"));
            }
        }
    }

    /// <summary>
    /// Secuencias al azar de mensajes válidos e inválidos contra el anfitrión real: termina siempre, sin lanzar, y todo lo
    /// que escribe es un mensaje válido del protocolo; cada <c>invoke</c> válido antes del primer mensaje inválido tiene
    /// exactamente una respuesta final (o el módulo terminó por <c>malformed</c>).
    /// </summary>
    [Fact]
    public async Task Secuencias_al_azar_terminan_y_cada_llamada_tiene_una_respuesta()
    {
        await FuzzSupport.RunCasesAsync(150, async seed =>
        {
            var random = new Random(seed);
            var frames = new List<string>();
            var invoked = new HashSet<long>();
            bool broken = false;
            for (int k = random.Next(1, 30); k > 0; k--)
            {
                long id = random.Next(1, 12);
                switch (random.Next(8))
                {
                    case 0 or 1:
                        frames.Add($$"""{"type":"invoke","id":{{id}},"function":"{{(random.Next(4) == 0 ? "contar" : "eco")}}","parameters":{"pasos":{{random.Next(1, 5)}},"msPorPaso":1""" + "}}");
                        if (!broken && !invoked.Add(id)) broken = true;
                        break;
                    case 2:
                        frames.Add($$"""{"type":"cancel","id":{{id}}}""");
                        break;
                    case 3:
                        frames.Add($$"""{"type":"ping","id":{{random.NextInt64(long.MinValue, long.MaxValue)}}}""");
                        break;
                    case 4:
                        frames.Add($$"""{"type":"futuro","id":{{id}}}""");
                        break;
                    case 5:
                        frames.Add(MalformedList[random.Next(MalformedList.Length)]);
                        broken = true;
                        break;
                    default:
                        frames.Add($$"""{"type":"invoke","id":{{id}},"function":"fallar"}""");
                        if (!broken && !invoked.Add(id)) broken = true;
                        break;
                }
            }

            // Los ids repetidos pueden ser legales si la primera llamada ya terminó: no se sabe de antemano.
            (bool result, List<string> replies) = await AgainstHostAsync(Support.EchoHost(maxConcurrent: 64), [.. frames]);
            bool malformed = false;
            var finals = new Dictionary<long, int>();
            foreach (string reply in replies)
            {
                ModuleMessage? message = MessageChannel.Parse(Encoding.UTF8.GetBytes(reply));
                Assert.NotNull(message);
                switch (message)
                {
                    case ResultMessage r:
                        finals[r.Id] = finals.GetValueOrDefault(r.Id) + 1;
                        break;
                    case ErrorMessage { Id: { } eid }:
                        finals[eid] = finals.GetValueOrDefault(eid) + 1;
                        break;
                    case ErrorMessage { Code: ErrorCodes.Malformed }:
                        malformed = true;
                        break;
                    case PongMessage or ProgressMessage:
                        break;
                    default:
                        Assert.Fail($"El módulo mandó {message.GetType().Name}.");
                        break;
                }
            }

            Assert.Equal(!malformed, result);
            if (!broken && !malformed)
            {
                foreach (long id in invoked)
                {
                    Assert.True(finals.GetValueOrDefault(id) == 1, $"La llamada {id} tuvo {finals.GetValueOrDefault(id)} respuestas finales.");
                }
            }
        }, first: 1_000_000);
    }

    private static InvokeMessage? TryParse(string json)
    {
        try
        {
            return Parse(json) as InvokeMessage;
        }
        catch (ModuleProtocolException)
        {
            return null;
        }
    }

    /// <summary>Un entero no negativo con muchos valores en el borde: 0, 1, potencias de dos y sus vecinos, el máximo.</summary>
    private static long Big(Random random, long max) => random.Next(5) switch
    {
        0 => random.NextInt64(0, Math.Min(max, 100_000) + 1),
        1 => Math.Min(max, (1L << random.Next(0, 63)) + random.Next(-1, 2)),
        2 => max - random.Next(0, 3),
        3 => random.Next(0, 3),
        _ => random.NextInt64(0, max) + 1,
    };
}
