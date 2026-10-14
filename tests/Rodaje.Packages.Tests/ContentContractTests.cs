using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de la sección «Contenido de los elementos» de <c>docs/formato-paquete.md</c>: una prueba por regla, con la
/// frase del documento encima.
/// </summary>
public sealed class ContentContractTests
{
    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Error<T>(Func<Stream, T> read, string text) =>
        Assert.Throws<PackageContentException>(() => read(Utf8(text))).Message;

    private static string Filter(string effects, string extra = "") =>
        $$"""{ "format": "rodaje.filter", "version": 1, "name": "F"{{extra}}, "effects": {{effects}} }""";

    private static string Effect(string curves, string extra = "", double duration = 0.5) =>
        $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": "start", "durationSeconds": {{duration.ToString(CultureInfo.InvariantCulture)}}{{extra}}, "curves": {{curves}} }""";

    private static string Curve(string parameter, string keys) => $$"""[ { "effect": "rodaje.transform", "parameter": "{{parameter}}", "keys": {{keys}} } ]""";

    private static string Transition(string extra) =>
        $$"""{ "format": "rodaje.preset", "version": 2, "name": "T", "durationSeconds": 0.5{{extra}} }""";

    private static string Style(string extra = "", int version = 1) =>
        $$"""{ "format": "rodaje.textstyle", "version": {{version}}, "name": "S", "font": { "family": "Inter" }, "size": 96{{extra}} }""";

    private static string Zones(string zones, string extra = "") =>
        $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z"{{extra}}, "zones": [ {{zones}} ] }""";

    private static string Export(string extra) =>
        $$"""{ "format": "rodaje.exportpreset", "version": 1, "name": "X", "container": "mp4"{{extra}} }""";

    private static string Values(int count, Func<int, string> value) =>
        "{ " + string.Join(", ", Enumerable.Range(0, count).Select(i => $"\"p{i}\": {value(i)}")) + " }";

    // ── Reglas comunes de los JSON ────────────────────────────────────────────────────────────────────────────────

    // «Hasta 1 MB, JSON (admite comentarios y comas finales) con un objeto en la raíz»
    [Fact]
    public void Comun_admite_comentarios_y_comas_finales_y_pide_objeto_hasta_1_mb()
    {
        FilterPreset preset = ContentReaders.ReadFilter(Utf8("""
            // Un filtro
            { "format": "rodaje.filter", "version": 1, "name": "F", /* sin id */ "effects": [ { "type": "rodaje.color", }, ], }
            """));
        Assert.Single(preset.Effects);
        Assert.Contains("No es JSON válido", Error(ContentReaders.ReadFilter, "{ \"format\": "), StringComparison.Ordinal);
        Assert.Contains("objeto", Error(ContentReaders.ReadFilter, "[1, 2]"), StringComparison.Ordinal);
        string big = Filter("""[ { "type": "rodaje.color" } ]""", $", \"x\": \"{new string('a', 1 << 20)}\"");
        Assert.Contains("pesa más de 1 MB", Error(ContentReaders.ReadFilter, big), StringComparison.Ordinal);
    }

    // «`format` es el del lector y `version` un entero desde 1» / «Una versión mayor que la que se entiende pide
    // "actualiza el programa"»
    [Theory]
    [InlineData("\"rodaje.effect\"", "1", "se esperaba «rodaje.filter»")]
    [InlineData("\"rodaje.filter\"", "0", "entero desde 1")]
    [InlineData("\"rodaje.filter\"", "1.5", "entero desde 1")]
    [InlineData("\"rodaje.filter\"", "\"1\"", "entero desde 1")]
    [InlineData("\"rodaje.filter\"", "2", "actualiza el programa")]
    public void Comun_format_y_version(string format, string version, string expected) =>
        Assert.Contains(expected, Error(ContentReaders.ReadFilter, $$"""{ "format": {{format}}, "version": {{version}}, "name": "F", "effects": [ { "type": "rodaje.color" } ] }"""), StringComparison.Ordinal);

    // «`name` obligatorio, hasta 80 caracteres sin controles ni marcas bidi» / «Falta name»
    [Fact]
    public void Comun_name_obligatorio_hasta_80_sin_controles_ni_bidi()
    {
        const string Effects = """[ { "type": "rodaje.color" } ]""";
        Assert.Contains("Falta name", Error(ContentReaders.ReadFilter, """{ "format": "rodaje.filter", "version": 1, "effects": [ { "type": "rodaje.color" } ] }"""), StringComparison.Ordinal);
        Assert.Contains("Falta name", Error(ContentReaders.ReadFilter, """{ "format": "rodaje.filter", "version": 1, "name": "\u0001‮", "effects": [ { "type": "rodaje.color" } ] }"""), StringComparison.Ordinal);
        string eighty = new('n', 80);
        Assert.Equal(eighty, ContentReaders.ReadFilter(Utf8(Filter(Effects).Replace("\"F\"", $"\"{eighty}\"", StringComparison.Ordinal))).Name);
        Assert.Contains("80", Error(ContentReaders.ReadFilter, Filter(Effects).Replace("\"F\"", $"\"{eighty}n\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal("Cine", ContentReaders.ReadFilter(Utf8(Filter(Effects).Replace("\"F\"", "\"Ci\\u202Ene\\u0007\"", StringComparison.Ordinal))).Name);
    }

    // «`id` opcional, con las reglas de los identificadores»
    [Fact]
    public void Comun_id_opcional_con_las_reglas_de_los_identificadores()
    {
        const string Effects = """[ { "type": "rodaje.color" } ]""";
        Assert.Equal("", ContentReaders.ReadFilter(Utf8(Filter(Effects))).Id);
        Assert.Equal("cine-calido", ContentReaders.ReadFilter(Utf8(Filter(Effects, """, "id": "cine-calido" """))).Id);
        Assert.Contains("id", Error(ContentReaders.ReadFilter, Filter(Effects, """, "id": "Cine Cálido" """)), StringComparison.Ordinal);
        Assert.Contains("id", Error(ContentReaders.ReadFilter, Filter(Effects, """, "id": 3 """)), StringComparison.Ordinal);
    }

    // «Tipos de efecto en minúsculas con un punto (`rodaje.color`, `otro.efecto`); nombres de parámetro y opción ASCII
    // empezando por letra» / «Un tipo desconocido se conserva en el proyecto»
    [Fact]
    public void Comun_tipos_de_efecto_y_nombres_de_parametro()
    {
        PresetEffect kept = ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "otro.efecto", "parameters": { "a1": 1 }, "options": { "b2": "x" } } ]"""))).Effects[0];
        Assert.Equal(("otro.efecto", 1.0, "x"), (kept.Type, kept.Parameters["a1"], kept.Options["b2"]));
        foreach (string type in (string[])["\"Rodaje.Color\"", "\"rodajecolor\"", "\".color\"", "\"rodaje.\"", "\"rodaje..color\"", "\"rodaje.cólor\""])
        {
            Assert.Contains("tipo de efecto", Error(ContentReaders.ReadFilter, Filter($$"""[ { "type": {{type}} } ]""")), StringComparison.Ordinal);
        }

        Assert.Contains("nombre de parámetro", Error(ContentReaders.ReadFilter, Filter("""[ { "type": "rodaje.color", "parameters": { "1a": 1 } } ]""")), StringComparison.Ordinal);
        Assert.Contains("nombre de parámetro", Error(ContentReaders.ReadFilter, Filter("""[ { "type": "rodaje.color", "parameters": { "añil": 1 } } ]""")), StringComparison.Ordinal);
        Assert.Contains("nombre de opción", Error(ContentReaders.ReadFilter, Filter("""[ { "type": "rodaje.color", "options": { "_lut": "x" } } ]""")), StringComparison.Ordinal);
    }

    // «Hasta 32 efectos, 64 curvas, 16 parámetros y 16 opciones por efecto, 1000 claves por curva, opciones de hasta 256
    // caracteres» / «Se rechaza el archivo»
    [Fact]
    public void Comun_limites_de_efectos_curvas_valores_claves_y_opciones()
    {
        string Effects(int n) => "[" + string.Join(",", Enumerable.Repeat("""{ "type": "rodaje.color" }""", n)) + "]";
        Assert.Equal(32, ContentReaders.ReadFilter(Utf8(Filter(Effects(32)))).Effects.Count);
        Assert.Contains("32 efectos", Error(ContentReaders.ReadFilter, Filter(Effects(33))), StringComparison.Ordinal);

        string Parameters(int n) => Filter($$"""[ { "type": "rodaje.color", "parameters": {{Values(n, i => "1")}} } ]""");
        Assert.Equal(16, ContentReaders.ReadFilter(Utf8(Parameters(16))).Effects[0].Parameters.Count);
        Assert.Contains("16", Error(ContentReaders.ReadFilter, Parameters(17)), StringComparison.Ordinal);

        string Options(int n) => Filter($$"""[ { "type": "rodaje.color", "options": {{Values(n, i => "\"x\"")}} } ]""");
        Assert.Equal(16, ContentReaders.ReadFilter(Utf8(Options(16))).Effects[0].Options.Count);
        Assert.Contains("16", Error(ContentReaders.ReadFilter, Options(17)), StringComparison.Ordinal);

        string Option(int length) => Filter($$"""[ { "type": "rodaje.color", "options": { "lut": "{{new string('o', length)}}" } } ]""");
        Assert.Equal(256, ContentReaders.ReadFilter(Utf8(Option(256))).Effects[0].Options["lut"].Length);
        Assert.Contains("256", Error(ContentReaders.ReadFilter, Option(257)), StringComparison.Ordinal);

        string Keys(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"{{ \"t\": {(i * 0.0001).ToString(CultureInfo.InvariantCulture)}, \"value\": 0 }}")) + "]";
        Assert.Equal(1000, ContentReaders.ReadEffect(Utf8(Effect(Curve("x", Keys(1000))))).Curves[0].Keys.Count);
        Assert.Contains("1000 claves", Error(ContentReaders.ReadEffect, Effect(Curve("x", Keys(1001)))), StringComparison.Ordinal);

        string Curves(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{ "effect": "otro.efecto", "parameter": "p{{i}}", "keys": [ { "t": 0, "value": 0 } ] }""")) + "]";
        string effect = """, "effects": [ { "type": "otro.efecto" } ]""";
        Assert.Equal(64, ContentReaders.ReadEffect(Utf8(Effect(Curves(64), effect))).Curves.Count);
        Assert.Contains("64 curvas", Error(ContentReaders.ReadEffect, Effect(Curves(65), effect)), StringComparison.Ordinal);
    }

    // «Valores finitos; `scale` de 0 a 100 y `opacity` de 0 a 1 en la transformación»
    [Theory]
    [InlineData("scale", "0", true)]
    [InlineData("scale", "100", true)]
    [InlineData("scale", "100.001", false)]
    [InlineData("scale", "-0.001", false)]
    [InlineData("opacity", "1", true)]
    [InlineData("opacity", "1.001", false)]
    [InlineData("opacity", "-0.1", false)]
    [InlineData("x", "-100000", true)]
    [InlineData("x", "1e400", false)]
    [InlineData("rotation", "-1e400", false)]
    public void Comun_valores_finitos_y_rangos_de_la_transformacion(string parameter, string value, bool valid)
    {
        string json = Effect(Curve(parameter, $$"""[ { "t": 0, "value": {{value}} } ]"""));
        if (valid)
        {
            Assert.Single(ContentReaders.ReadEffect(Utf8(json)).Curves);
        }
        else
        {
            Error(ContentReaders.ReadEffect, json);
        }
    }

    // «Claves `{ t, value, interp }`: `t` en segundos de 0 a `durationSeconds`, estrictamente creciente»
    [Theory]
    [InlineData("""[ { "t": -0.1, "value": 0 } ]""", "fuera de 0 a durationSeconds")]
    [InlineData("""[ { "t": 0.6, "value": 0 } ]""", "fuera de 0 a durationSeconds")]
    [InlineData("""[ { "t": 0.2, "value": 0 }, { "t": 0.2, "value": 1 } ]""", "mayor que el de la clave anterior")]
    [InlineData("""[ { "t": 0.3, "value": 0 }, { "t": 0.2, "value": 1 } ]""", "mayor que el de la clave anterior")]
    [InlineData("""[ { "value": 0 } ]""", ".t tiene que ser un número")]
    [InlineData("""[ { "t": 0 } ]""", ".value tiene que ser un número")]
    [InlineData("""[]""", "no tiene claves")]
    public void Comun_claves_en_tiempo_y_crecientes(string keys, string expected) =>
        Assert.Contains(expected, Error(ContentReaders.ReadEffect, Effect(Curve("x", keys))), StringComparison.Ordinal);

    // Regresión: dentro de la holgura de redondeo, una clave apenas pasada de durationSeconds se recortaba a
    // durationSeconds y quedaba con el mismo t que la anterior; «estrictamente creciente» tiene que valer después.
    [Fact]
    public void Comun_claves_siguen_estrictamente_crecientes_tras_recortar_al_final()
    {
        string json = Effect(Curve("x", """[ { "t": 0, "value": 0 }, { "t": 0.5, "value": 1 }, { "t": 0.5000005, "value": 2 } ]"""));
        Assert.Contains("mayor que el de la clave anterior", Error(ContentReaders.ReadEffect, json), StringComparison.Ordinal);

        IReadOnlyList<PresetKey> keys = ContentReaders.ReadEffect(Utf8(Effect(Curve("x", """[ { "t": 0, "value": 0 }, { "t": 0.5000005, "value": 1 } ]""")))).Curves[0].Keys;
        Assert.Equal([0, 0.5], keys.Select(k => k.T));
    }

    // «`interp` es `hold`, `linear` (por defecto), `easeIn`, `easeOut`, `easeInOut`, `bounce`, `elastic` o `bezier`, que
    // pide además `ease: [x1, y1, x2, y2]` (x de 0 a 1, y de −2 a 3)»
    [Fact]
    public void Comun_interpolaciones_y_bezier()
    {
        string keys = """
            [ { "t": 0, "value": 0 }, { "t": 0.05, "value": 0, "interp": "hold" }, { "t": 0.1, "value": 0, "interp": "linear" },
              { "t": 0.15, "value": 0, "interp": "easeIn" }, { "t": 0.2, "value": 0, "interp": "easeOut" },
              { "t": 0.25, "value": 0, "interp": "easeInOut" }, { "t": 0.3, "value": 0, "interp": "bounce" },
              { "t": 0.35, "value": 0, "interp": "elastic" }, { "t": 0.4, "value": 0, "interp": "bezier", "ease": [0, -2, 1, 3] } ]
            """;
        IReadOnlyList<PresetKey> read = ContentReaders.ReadEffect(Utf8(Effect(Curve("x", keys)))).Curves[0].Keys;
        Assert.Equal(
            [PresetInterpolation.Linear, PresetInterpolation.Hold, PresetInterpolation.Linear, PresetInterpolation.EaseIn, PresetInterpolation.EaseOut,
             PresetInterpolation.EaseInOut, PresetInterpolation.Bounce, PresetInterpolation.Elastic, PresetInterpolation.Bezier],
            read.Select(k => k.Interpolation));
        Assert.Equal((0, -2, 1, 3), read[^1].Ease);

        foreach (string bad in (string[])[
            """{ "t": 0, "value": 0, "interp": "cubic" }""",
            """{ "t": 0, "value": 0, "interp": "Linear" }""",
            """{ "t": 0, "value": 0, "interp": "bezier" }""",
            """{ "t": 0, "value": 0, "interp": "bezier", "ease": [0, 0, 1] }""",
            """{ "t": 0, "value": 0, "interp": "bezier", "ease": [-0.1, 0, 1, 1] }""",
            """{ "t": 0, "value": 0, "interp": "bezier", "ease": [0, 0, 1.1, 1] }""",
            """{ "t": 0, "value": 0, "interp": "bezier", "ease": [0, -2.1, 1, 1] }""",
            """{ "t": 0, "value": 0, "interp": "bezier", "ease": [0, 0, 1, 3.1] }"""])
        {
            Error(ContentReaders.ReadEffect, Effect(Curve("x", $"[ {bad} ]")));
        }
    }

    // «`reference: { width, height }` (16 a 16384, por defecto 1920 × 1080)»
    [Theory]
    [InlineData("", 1920, 1080)]
    [InlineData(""", "reference": { "width": 16, "height": 16384 }""", 16, 16384)]
    [InlineData(""", "reference": { "width": 15, "height": 1080 }""", 0, 0)]
    [InlineData(""", "reference": { "width": 1920, "height": 16385 }""", 0, 0)]
    [InlineData(""", "reference": { "width": 1920.5, "height": 1080 }""", 0, 0)]
    [InlineData(""", "reference": { "width": 1920 }""", 0, 0)]
    public void Comun_reference(string extra, int width, int height)
    {
        string json = Effect(Curve("x", """[ { "t": 0, "value": 0 } ]"""), extra);
        if (width == 0)
        {
            Assert.Contains("reference", Error(ContentReaders.ReadEffect, json), StringComparison.Ordinal);
        }
        else
        {
            EffectPreset preset = ContentReaders.ReadEffect(Utf8(json));
            Assert.Equal((width, height), (preset.ReferenceWidth, preset.ReferenceHeight));
        }
    }

    // ── lut ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Cube(int size, string header = "", Func<int, string>? line = null)
    {
        var text = new StringBuilder(header);
        text.Append("LUT_3D_SIZE ").Append(size).Append('\n');
        for (int i = 0; i < size * size * size; i++)
        {
            text.Append(line?.Invoke(i) ?? "0.5 0.5 0.5").Append('\n');
        }

        return text.ToString();
    }

    // «`LUT_3D_SIZE` de 2 a 65»
    [Fact]
    public void Lut_tamano_de_2_a_65()
    {
        Assert.Equal(2, ContentReaders.ReadCube(Utf8(Cube(2))).Size);
        Assert.Contains("de 2 a 65", Error(ContentReaders.ReadCube, "LUT_3D_SIZE 1\n0 0 0\n"), StringComparison.Ordinal);
        Assert.Contains("de 2 a 65", Error(ContentReaders.ReadCube, "LUT_3D_SIZE 66\n"), StringComparison.Ordinal);
    }

    // «`TITLE` opcional, `DOMAIN_MIN`/`DOMAIN_MAX` (o `LUT_3D_INPUT_RANGE min max`), comentarios con `#`» / «Los valores
    // se llevan del dominio a 0–1 al leer»
    [Fact]
    public void Lut_titulo_comentarios_y_dominio()
    {
        Assert.Equal("", ContentReaders.ReadCube(Utf8(Cube(2))).Title);
        LutTable lut = ContentReaders.ReadCube(Utf8(Cube(2, "# comentario\nTITLE \"Cálido\"\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 2 4 1\n", _ => "1 1 1")));
        Assert.Equal("Cálido", lut.Title);
        Assert.Equal([0.5f, 0.25f, 1f], lut.Rgb[..3]);
        LutTable range = ContentReaders.ReadCube(Utf8(Cube(2, "LUT_3D_INPUT_RANGE -1 1\n", _ => "0 0 0")));
        Assert.All(range.Rgb, v => Assert.Equal(0.5f, v));
    }

    // «exactamente N³ líneas de tres números con R variando más rápido»
    [Fact]
    public void Lut_n_al_cubo_con_r_mas_rapido()
    {
        LutTable lut = ContentReaders.ReadCube(Utf8(Cube(2, line: i => $"{i % 2} {i / 2 % 2} {i / 4}")));
        Assert.Equal([1f, 0f, 0f], lut.Rgb[3..6]);
        Assert.Equal([0f, 1f, 0f], lut.Rgb[6..9]);
        Assert.Equal([0f, 0f, 1f], lut.Rgb[12..15]);
        Assert.Contains("entradas", Error(ContentReaders.ReadCube, Cube(2) + "0 0 0\n"), StringComparison.Ordinal);
        Assert.Contains("entradas", Error(ContentReaders.ReadCube, Cube(2)[..^"0.5 0.5 0.5\n".Length]), StringComparison.Ordinal);
        Assert.Contains("tres números", Error(ContentReaders.ReadCube, Cube(2, line: i => i == 3 ? "0.5 0.5" : "0 0 0")), StringComparison.Ordinal);
    }

    // «Se rechazan: LUT 1D (con el mensaje «Es una LUT 1D: Rodaje solo usa LUT 3D»), palabras clave desconocidas o
    // después de los datos, repetidas, números no finitos, entradas de más o de menos y un dominio con mínimo ≥ máximo»
    [Theory]
    [InlineData("LUT_1D_SIZE 4\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n", "Es una LUT 1D: Rodaje solo usa LUT 3D")]
    [InlineData("LUT_SIZE 2\n", "palabra clave desconocida")]
    [InlineData("TITLE \"a\"\nTITLE \"b\"\n", "TITLE está repetido")]
    [InlineData("LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n", "LUT_3D_SIZE está repetido")]
    [InlineData("DOMAIN_MIN 0 0 0\nDOMAIN_MIN 0 0 0\n", "DOMAIN_MIN está repetido")]
    [InlineData("DOMAIN_MIN 0 1 0\nDOMAIN_MAX 1 1 1\n", "DOMAIN_MIN tiene que ser menor")]
    [InlineData("DOMAIN_MIN 0 0 0\nLUT_3D_INPUT_RANGE 0 1\n", "dominio está repetido")]
    public void Lut_rechazos_de_cabecera(string header, string expected) =>
        Assert.Contains(expected, Error(ContentReaders.ReadCube, header.StartsWith("LUT_1D", StringComparison.Ordinal) || header.Contains("LUT_3D_SIZE", StringComparison.Ordinal) ? header : Cube(2, header)), StringComparison.Ordinal);

    [Fact]
    public void Lut_rechaza_palabras_clave_despues_de_los_datos_y_numeros_no_finitos()
    {
        Assert.Contains("antes de los datos", Error(ContentReaders.ReadCube, Cube(2) + "TITLE \"tarde\"\n"), StringComparison.Ordinal);
        Assert.Contains("tres números", Error(ContentReaders.ReadCube, Cube(2, line: i => i == 0 ? "1e39 0 0" : "0 0 0")), StringComparison.Ordinal);
        Assert.Contains("tres números", Error(ContentReaders.ReadCube, Cube(2, line: i => i == 0 ? "-Infinity 0 0" : "0 0 0")), StringComparison.Ordinal);
        Error(ContentReaders.ReadCube, Cube(2, line: i => i == 0 ? "NaN 0 0" : "0 0 0"));
    }

    // «Hasta 16 MB»
    [Fact]
    public void Lut_hasta_16_mb()
    {
        byte[] big = new byte[(16 * 1024 * 1024) + 1];
        big.AsSpan().Fill((byte)'#');
        Assert.Contains("16 MB", Assert.Throws<PackageContentException>(() => ContentReaders.ReadCube(new MemoryStream(big))).Message, StringComparison.Ordinal);
    }

    // ── filter ──────────────────────────────────────────────────────────────────────────────────────────────────

    // «Una lista no vacía de efectos con valores fijos; lo que falta toma el valor por defecto del registro»
    [Fact]
    public void Filter_lista_no_vacia_y_sin_rellenar_lo_que_falta()
    {
        FilterPreset preset = ContentReaders.ReadFilter(Utf8(Filter("""
            [ { "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "./lut-cine" } },
              { "type": "rodaje.vignette", "parameters": { "amount": 0.25 } } ]
            """, """, "id": "cine" """)));
        Assert.Equal(("cine", "F"), (preset.Id, preset.Name));
        Assert.Equal(["rodaje.color", "rodaje.vignette"], preset.Effects.Select(e => e.Type));
        Assert.Equal("./lut-cine", preset.Effects[0].Options["lut"]);
        Assert.Single(preset.Effects[1].Parameters);
        Assert.Empty(preset.Effects[1].Options);
        Assert.Contains("effects está vacía", Error(ContentReaders.ReadFilter, Filter("[]")), StringComparison.Ordinal);
        Assert.Contains("Falta effects", Error(ContentReaders.ReadFilter, """{ "format": "rodaje.filter", "version": 1, "name": "F" }"""), StringComparison.Ordinal);
    }

    // ── effect ──────────────────────────────────────────────────────────────────────────────────────────────────

    // «`anchor` es `start` (al principio del clip), `end` (al final) o `whole` (estirada a todo el clip)»
    [Theory]
    [InlineData("start", EffectPlacement.In)]
    [InlineData("end", EffectPlacement.Out)]
    [InlineData("whole", EffectPlacement.Whole)]
    public void Effect_anchor(string anchor, EffectPlacement placement) =>
        Assert.Equal(placement, ContentReaders.ReadEffect(Utf8(Effect(Curve("x", """[ { "t": 0, "value": 0 } ]""")).Replace("\"start\"", $"\"{anchor}\"", StringComparison.Ordinal))).Placement);

    [Fact]
    public void Effect_anchor_desconocido_o_ausente_se_rechaza()
    {
        string json = Effect(Curve("x", """[ { "t": 0, "value": 0 } ]"""));
        Assert.Contains("anchor", Error(ContentReaders.ReadEffect, json.Replace("\"start\"", "\"middle\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("Falta anchor", Error(ContentReaders.ReadEffect, json.Replace("\"anchor\": \"start\", ", "", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    // «`durationSeconds` de hasta 600 s»
    [Fact]
    public void Effect_duracion_hasta_600_s()
    {
        Assert.Equal(600, ContentReaders.ReadEffect(Utf8(Effect(Curve("x", """[ { "t": 0, "value": 0 } ]"""), duration: 600))).DurationSeconds);
        Assert.Contains("600", Error(ContentReaders.ReadEffect, Effect(Curve("x", """[ { "t": 0, "value": 0 } ]"""), duration: 600.01)), StringComparison.Ordinal);
        Assert.Contains("durationSeconds", Error(ContentReaders.ReadEffect, Effect(Curve("x", """[ { "t": 0, "value": 0 } ]"""), duration: 0)), StringComparison.Ordinal);
    }

    // «Cada curva anima un parámetro de `rodaje.transform` o de un efecto de `effects`, sin repetir el par efecto/parámetro»
    [Fact]
    public void Effect_curvas_de_la_transformacion_o_de_effects_sin_repetir()
    {
        const string Key = """[ { "t": 0, "value": 0 } ]""";
        string withBlur = """, "effects": [ { "type": "rodaje.blur", "parameters": { "radius": 0 } } ]""";
        EffectPreset preset = ContentReaders.ReadEffect(Utf8(Effect($$"""[ { "effect": "rodaje.blur", "parameter": "radius", "keys": {{Key}} }, { "effect": "rodaje.transform", "parameter": "scale", "keys": {{Key}} } ]""", withBlur)));
        Assert.Equal([("rodaje.blur", "radius"), ("rodaje.transform", "scale")], preset.Curves.Select(c => (c.Effect, c.Parameter)));
        Assert.Contains("no está en effects", Error(ContentReaders.ReadEffect, Effect($$"""[ { "effect": "rodaje.glow", "parameter": "amount", "keys": {{Key}} } ]""", withBlur)), StringComparison.Ordinal);
        Assert.Contains("no es un parámetro de la transformación", Error(ContentReaders.ReadEffect, Effect(Curve("skew", Key))), StringComparison.Ordinal);
        Assert.Contains("repite", Error(ContentReaders.ReadEffect, Effect($$"""[ { "effect": "rodaje.transform", "parameter": "x", "keys": {{Key}} }, { "effect": "rodaje.transform", "parameter": "x", "keys": {{Key}} } ]""")), StringComparison.Ordinal);
    }

    // «Hace falta al menos un efecto o una curva»
    [Fact]
    public void Effect_necesita_un_efecto_o_una_curva()
    {
        Assert.Contains("ni curves", Error(ContentReaders.ReadEffect, Effect("[]", """, "effects": [] """)), StringComparison.Ordinal);
        Assert.Single(ContentReaders.ReadEffect(Utf8(Effect("[]", """, "effects": [ { "type": "rodaje.blur" } ] """))).Effects);
    }

    // ── transition ──────────────────────────────────────────────────────────────────────────────────────────────

    // «por parámetro (`x`, `y`, `scale`, `rotation`, `opacity`), más la mezcla [...] (`rodaje.crossfade` por defecto
    // [...]; nunca `rodaje.preset`) con sus `mixParameters` y `mixOptions` opcionales»
    [Fact]
    public void Transition_mezcla_por_defecto_y_nunca_preset()
    {
        TransitionPreset bare = ContentReaders.ReadTransition(Utf8(Transition("")));
        Assert.Equal(("rodaje.crossfade", 0, 0, 0, 0), (bare.Mix, bare.Outgoing.Count, bare.Incoming.Count, bare.MixParameters.Count, bare.MixOptions.Count));

        TransitionPreset wipe = ContentReaders.ReadTransition(Utf8(Transition("""
            , "mix": "rodaje.wipe", "mixParameters": { "angle": 180 }, "mixOptions": { "shape": "linear" },
            "outgoing": { "x": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": -1920 } ], "opacity": [ { "t": 0, "value": 1 } ] },
            "incoming": { "y": [ { "t": 0, "value": 1080 } ], "scale": [ { "t": 0, "value": 1 } ], "rotation": [ { "t": 0, "value": 90 } ] }
            """)));
        Assert.Equal(("rodaje.wipe", 180.0, "linear"), (wipe.Mix, wipe.MixParameters["angle"], wipe.MixOptions["shape"]));
        Assert.Equal(["x", "opacity"], wipe.Outgoing.Select(c => c.Parameter));
        Assert.All(wipe.Incoming, c => Assert.Equal("rodaje.transform", c.Effect));

        Assert.Contains("rodaje.preset", Error(ContentReaders.ReadTransition, Transition(""", "mix": "rodaje.preset" """)), StringComparison.Ordinal);
        Assert.Contains("parámetro de la transformación", Error(ContentReaders.ReadTransition, Transition(""", "incoming": { "skew": [ { "t": 0, "value": 0 } ] }""")), StringComparison.Ordinal);
    }

    // «`durationSeconds` (hasta 60 s)»
    [Fact]
    public void Transition_hasta_60_s() =>
        Assert.Contains("60", Error(ContentReaders.ReadTransition, Transition("").Replace("0.5", "60.5", StringComparison.Ordinal)), StringComparison.Ordinal);

    // «El v1 de «Guardar como transición» se lee como curvas del clip que entra con fundido cruzado»
    [Fact]
    public void Transition_v1_son_curvas_del_que_entra_con_fundido()
    {
        TransitionPreset v1 = ContentReaders.ReadTransition(Utf8("""
            { "format": "rodaje.preset", "version": 1, "name": "Vieja", "kind": "transition", "durationSeconds": 1,
              "tracks": { "opacity": [ { "t": 0, "value": 0 }, { "t": 1, "value": 1 } ] } }
            """));
        Assert.Equal(("rodaje.crossfade", 0), (v1.Mix, v1.Outgoing.Count));
        Assert.Equal("opacity", Assert.Single(v1.Incoming).Parameter);
    }

    // ── text-style ──────────────────────────────────────────────────────────────────────────────────────────────

    // «Obligatorios `font.family` y `size` (mayor que 0 y hasta 1000); lo demás tiene valor por defecto (peso 400,
    // interlineado 1,2, centrado, relleno blanco) o falta (sin borde, sombra, fondo ni animación)»
    [Fact]
    public void TextStyle_obligatorios_y_valores_por_defecto()
    {
        TextStylePreset s = ContentReaders.ReadTextStyle(Utf8(Style()));
        Assert.Equal((400, false, 1.2, TextStyleAlign.Center, "#FFFFFF"), (s.Font.Weight, s.Font.Italic, s.LineHeight, s.Align, s.FillColor));
        Assert.Equal((1920, 1080, 0.0), (s.ReferenceWidth, s.ReferenceHeight, s.LetterSpacing));
        Assert.True(s.Stroke is null && s.Shadow is null && s.Background is null && s.In is null && s.Out is null && s.HighlightColor is null);
        Assert.Contains("Falta font", Error(ContentReaders.ReadTextStyle, """{ "format": "rodaje.textstyle", "version": 1, "name": "S", "size": 96 }"""), StringComparison.Ordinal);
        Assert.Contains("font.family", Error(ContentReaders.ReadTextStyle, Style().Replace("\"Inter\"", "\"  \"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("Falta size", Error(ContentReaders.ReadTextStyle, Style().Replace(", \"size\": 96", "", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(1000, ContentReaders.ReadTextStyle(Utf8(Style().Replace("96", "1000", StringComparison.Ordinal))).Size);
        Assert.Contains("size", Error(ContentReaders.ReadTextStyle, Style().Replace("96", "1000.5", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("size", Error(ContentReaders.ReadTextStyle, Style().Replace("96", "0", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    // «colores `#RRGGBB` o `#RRGGBBAA`»
    [Theory]
    [InlineData("#ffcc00", "#FFCC00")]
    [InlineData("#00000099", "#00000099")]
    [InlineData("#FFF", null)]
    [InlineData("FFCC00", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("#FFCC0", null)]
    public void TextStyle_colores(string color, string? expected)
    {
        string json = Style($$""", "fill": { "color": "{{color}}" }""");
        if (expected is null)
        {
            Assert.Contains("#RRGGBB", Error(ContentReaders.ReadTextStyle, json), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(expected, ContentReaders.ReadTextStyle(Utf8(json)).FillColor);
        }
    }

    // «peso entero de 100 a 900; hasta 8 fuentes de respaldo; `font.source` es la referencia de un elemento `font`»
    [Fact]
    public void TextStyle_peso_respaldo_y_origen_de_la_fuente()
    {
        string Font(string inner) => $$"""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "size": 96, "font": { "family": "Inter"{{inner}} } }""";
        Assert.Equal(900, ContentReaders.ReadTextStyle(Utf8(Font(""", "weight": 900"""))).Font.Weight);
        Assert.Contains("font.weight", Error(ContentReaders.ReadTextStyle, Font(""", "weight": 950""")), StringComparison.Ordinal);
        Assert.Contains("font.weight", Error(ContentReaders.ReadTextStyle, Font(""", "weight": 99""")), StringComparison.Ordinal);
        Assert.Contains("entero", Error(ContentReaders.ReadTextStyle, Font(""", "weight": 450.5""")), StringComparison.Ordinal);

        string Fallback(int n) => Font($", \"fallback\": [{string.Join(",", Enumerable.Repeat("\"Arial\"", n))}]");
        Assert.Equal(8, ContentReaders.ReadTextStyle(Utf8(Fallback(8))).Font.Fallback.Count);
        Assert.Contains("hasta 8", Error(ContentReaders.ReadTextStyle, Fallback(9)), StringComparison.Ordinal);

        Assert.Equal("rodaje-fuentes@1.0.0/inter", ContentReaders.ReadTextStyle(Utf8(Font(""", "source": "rodaje-fuentes@1.0.0/inter" """))).Font.Source);
        Assert.Contains("font.source", Error(ContentReaders.ReadTextStyle, Font(""", "source": "inter" """)), StringComparison.Ordinal);
    }

    // «borde de hasta la mitad de `size`, `blur` de 0 a 64, desplazamientos y márgenes de hasta 1000»
    [Theory]
    [InlineData(""", "stroke": { "color": "#000000", "width": 48 }""", true)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 48.01 }""", false)]
    [InlineData(""", "stroke": { "color": "#000000", "width": -1 }""", false)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 64, "dx": -1000, "dy": 1000 }""", true)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 64.1 }""", false)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": -0.1 }""", false)]
    [InlineData(""", "shadow": { "color": "#000000", "dx": 1000.1 }""", false)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1000, 0], "radius": 1000 }""", true)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1000.1, 0] }""", false)]
    [InlineData(""", "background": { "color": "#000000", "radius": 1000.1 }""", false)]
    public void TextStyle_borde_sombra_y_fondo(string extra, bool valid)
    {
        if (valid)
        {
            ContentReaders.ReadTextStyle(Utf8(Style(extra)));
        }
        else
        {
            Error(ContentReaders.ReadTextStyle, Style(extra));
        }
    }

    // «`highlight.color` es el color de la palabra que se está diciendo» / «`in` y `out` (hasta 60 s) son curvas de
    // `rodaje.transform` por parámetro, como en `rodaje.preset`»
    [Fact]
    public void TextStyle_resaltado_y_animaciones()
    {
        TextStylePreset s = ContentReaders.ReadTextStyle(Utf8(Style("""
            , "highlight": { "color": "#FFD400" },
              "in": { "durationSeconds": 60, "curves": { "opacity": [ { "t": 0, "value": 0 }, { "t": 60, "value": 1 } ] } },
              "out": { "durationSeconds": 0.3 }
            """)));
        Assert.Equal(("#FFD400", "word"), (s.HighlightColor, s.HighlightMode));
        Assert.Equal(("rodaje.transform", "opacity"), (s.In!.Curves[0].Effect, s.In.Curves[0].Parameter));
        Assert.Empty(s.Out!.Curves);
        Assert.Contains("in.durationSeconds", Error(ContentReaders.ReadTextStyle, Style(""", "in": { "durationSeconds": 60.1 }""")), StringComparison.Ordinal);
        Assert.Contains("parámetro de la transformación", Error(ContentReaders.ReadTextStyle, Style(""", "in": { "durationSeconds": 1, "curves": { "skew": [ { "t": 0, "value": 0 } ] } }""")), StringComparison.Ordinal);
        Assert.Contains("fuera de 0 a 1", Error(ContentReaders.ReadTextStyle, Style(""", "out": { "durationSeconds": 1, "curves": { "opacity": [ { "t": 0, "value": 2 } ] } }""")), StringComparison.Ordinal);
    }

    // «`fill.gradient` (`kind` `linear` o `radial`, `angle` de −360 a 360, de 2 a 8 `stops` `{ "position", "color" }`
    // en orden)»
    [Fact]
    public void TextStyle_v2_degradado()
    {
        string Gradient(string inner) => Style($$""", "fill": { "color": "#FFFFFF", "gradient": { {{inner}} } }""", version: 2);
        string Stops(int n) => "\"stops\": [" + string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{ "position": {{(i / 8.0).ToString(CultureInfo.InvariantCulture)}}, "color": "#000000" }""")) + "]";
        TextStyleGradient g = ContentReaders.ReadTextStyle(Utf8(Gradient($"{Stops(2)}"))).Gradient!;
        Assert.Equal(("linear", 0.0, 2), (g.Kind, g.Angle, g.Stops.Count));
        Assert.Equal(("radial", -360.0, 8), (ContentReaders.ReadTextStyle(Utf8(Gradient($"\"kind\": \"radial\", \"angle\": -360, {Stops(8)}"))).Gradient!.Kind, -360.0, 8));
        Assert.Contains("kind", Error(ContentReaders.ReadTextStyle, Gradient($"\"kind\": \"conic\", {Stops(2)}")), StringComparison.Ordinal);
        Assert.Contains("angle", Error(ContentReaders.ReadTextStyle, Gradient($"\"angle\": 361, {Stops(2)}")), StringComparison.Ordinal);
        Assert.Contains("dos paradas", Error(ContentReaders.ReadTextStyle, Gradient(Stops(1))), StringComparison.Ordinal);
        Assert.Contains("hasta 8", Error(ContentReaders.ReadTextStyle, Gradient(Stops(9))), StringComparison.Ordinal);
        Assert.Contains("mayor o igual", Error(ContentReaders.ReadTextStyle, Gradient("""
            "stops": [ { "position": 0.6, "color": "#000000" }, { "position": 0.4, "color": "#FFFFFF" } ]
            """)), StringComparison.Ordinal);
    }

    // «`extraStrokes` y `extraShadows` (hasta 4 cada uno, con los campos de `stroke` y `shadow`)»
    [Fact]
    public void TextStyle_v2_capas_extra_hasta_4()
    {
        string Layers(string property, string layer, int n) => Style($", \"{property}\": [{string.Join(",", Enumerable.Repeat(layer, n))}]", version: 2);
        const string Stroke = """{ "color": "#000000", "width": 4 }""";
        const string Shadow = """{ "color": "#000000", "blur": 8 }""";
        Assert.Equal(4, ContentReaders.ReadTextStyle(Utf8(Layers("extraStrokes", Stroke, 4))).ExtraStrokes.Count);
        Assert.Equal(4, ContentReaders.ReadTextStyle(Utf8(Layers("extraShadows", Shadow, 4))).ExtraShadows.Count);
        Assert.Contains("hasta 4", Error(ContentReaders.ReadTextStyle, Layers("extraStrokes", Stroke, 5)), StringComparison.Ordinal);
        Assert.Contains("hasta 4", Error(ContentReaders.ReadTextStyle, Layers("extraShadows", Shadow, 5)), StringComparison.Ordinal);
        Assert.Contains("mitad de size", Error(ContentReaders.ReadTextStyle, Layers("extraStrokes", """{ "color": "#000000", "width": 49 }""", 1)), StringComparison.Ordinal);
        Assert.Contains("blur", Error(ContentReaders.ReadTextStyle, Layers("extraShadows", """{ "color": "#000000", "blur": 65 }""", 1)), StringComparison.Ordinal);
    }

    // «`typography` (`kerning`, `case`: `none`, `upper`, `lower` o `smallCaps`, `features`: hasta 16 etiquetas OpenType de
    // 4 caracteres con `-` delante para apagarlas, `fit`: `none`, `shrink` o `fill`)»
    [Fact]
    public void TextStyle_v2_tipografia()
    {
        TextStyleTypography t = ContentReaders.ReadTextStyle(Utf8(Style(""", "typography": {}""", version: 2))).Typography!;
        Assert.Equal((true, "none", 0, "none"), (t.Kerning, t.Case, t.Features.Count, t.Fit));
        t = ContentReaders.ReadTextStyle(Utf8(Style(""", "typography": { "kerning": false, "case": "smallCaps", "features": ["ss01", "-liga"], "fit": "shrink" }""", version: 2))).Typography!;
        Assert.Equal((false, "smallCaps", "ss01,-liga", "shrink"), (t.Kerning, t.Case, string.Join(",", t.Features), t.Fit));
        foreach (string bad in (string[])[
            """{ "case": "title" }""", """{ "fit": "stretch" }""", """{ "kerning": 1 }""", """{ "features": ["liga1"] }""",
            """{ "features": ["lig"] }""", """{ "features": ["li a"] }"""])
        {
            Error(ContentReaders.ReadTextStyle, Style($", \"typography\": {bad}", version: 2));
        }

        string Features(int n) => Style($", \"typography\": {{ \"features\": [{string.Join(",", Enumerable.Repeat("\"kern\"", n))}] }}", version: 2);
        Assert.Equal(16, ContentReaders.ReadTextStyle(Utf8(Features(16))).Typography!.Features.Count);
        Assert.Contains("hasta 16", Error(ContentReaders.ReadTextStyle, Features(17)), StringComparison.Ordinal);
    }

    // «`path` (`{ "kind": "arc", "bend" }` de −360 a 360), `highlight.mode` (`word` o `sweep`)»
    [Fact]
    public void TextStyle_v2_arco_y_modo_de_resaltado()
    {
        Assert.Equal(-360, ContentReaders.ReadTextStyle(Utf8(Style(""", "path": { "kind": "arc", "bend": -360 }""", version: 2))).PathBend);
        Assert.Contains("path.bend", Error(ContentReaders.ReadTextStyle, Style(""", "path": { "kind": "arc", "bend": 360.5 }""", version: 2)), StringComparison.Ordinal);
        Assert.Contains("path.kind", Error(ContentReaders.ReadTextStyle, Style(""", "path": { "kind": "wave", "bend": 10 }""", version: 2)), StringComparison.Ordinal);
        Assert.Equal("sweep", ContentReaders.ReadTextStyle(Utf8(Style(""", "highlight": { "color": "#FFD400", "mode": "sweep" }""", version: 2))).HighlightMode);
        Assert.Contains("highlight.mode", Error(ContentReaders.ReadTextStyle, Style(""", "highlight": { "color": "#FFD400", "mode": "line" }""", version: 2)), StringComparison.Ordinal);
    }

    // «en `in` y `out`, `unit` (`block`, `line`, `word` o `letter`), `stagger` (0 a 1), `order` (`forward`, `backward`,
    // `center`, `edges` o `random`) y la curva `blur` (0 a 64), con las interpolaciones `bounce` y `elastic`»
    [Fact]
    public void TextStyle_v2_animacion_por_unidades()
    {
        TextStyleAnimation a = ContentReaders.ReadTextStyle(Utf8(Style(""", "in": { "durationSeconds": 1 }""", version: 2))).In!;
        Assert.Equal(("block", 0.0, "forward"), (a.Unit, a.Stagger, a.Order));
        a = ContentReaders.ReadTextStyle(Utf8(Style("""
            , "in": { "durationSeconds": 1.2, "unit": "letter", "stagger": 1, "order": "edges",
                      "curves": { "blur": [ { "t": 0, "value": 64, "interp": "elastic" }, { "t": 0.6, "value": 0, "interp": "bounce" } ] } }
            """, version: 2))).In!;
        Assert.Equal(("letter", 1.0, "edges", "blur"), (a.Unit, a.Stagger, a.Order, a.Curves[0].Parameter));
        Assert.Contains("in.unit", Error(ContentReaders.ReadTextStyle, Style(""", "in": { "durationSeconds": 1, "unit": "page" }""", version: 2)), StringComparison.Ordinal);
        Assert.Contains("in.stagger", Error(ContentReaders.ReadTextStyle, Style(""", "in": { "durationSeconds": 1, "stagger": 1.1 }""", version: 2)), StringComparison.Ordinal);
        Assert.Contains("out.order", Error(ContentReaders.ReadTextStyle, Style(""", "out": { "durationSeconds": 1, "order": "shuffle" }""", version: 2)), StringComparison.Ordinal);
        Assert.Contains("fuera de 0 a 64", Error(ContentReaders.ReadTextStyle, Style(""", "in": { "durationSeconds": 1, "curves": { "blur": [ { "t": 0, "value": 64.5 } ] } }""", version: 2)), StringComparison.Ordinal);
    }

    // «la 1 se sigue leyendo»
    [Fact]
    public void TextStyle_v1_se_sigue_leyendo_y_la_3_pide_actualizar()
    {
        Assert.Equal("Inter", ContentReaders.ReadTextStyle(Utf8(Style(version: 1))).Font.Family);
        Assert.Contains("actualiza el programa", Error(ContentReaders.ReadTextStyle, Style(version: 3)), StringComparison.Ordinal);
    }

    // ── safe-zones ──────────────────────────────────────────────────────────────────────────────────────────────

    // «`rect` es `[x, y, ancho, alto]` en fracciones de la imagen (0 a 1, desde arriba a la izquierda), con ancho y alto
    // mayores que 0 y sin salirse (`x + ancho` e `y + alto` hasta 1)»
    [Theory]
    [InlineData("[0, 0, 1, 1]", true)]
    [InlineData("[0.86, 0.5, 0.14, 0.5]", true)]
    [InlineData("[0.5, 0, 0.5000001, 1]", true)]
    [InlineData("[0.5, 0, 0.51, 1]", false)]
    [InlineData("[0, 0.5, 1, 0.51]", false)]
    [InlineData("[0, 0, 0, 1]", false)]
    [InlineData("[0, 0, 1, -0.1]", false)]
    [InlineData("[-0.01, 0, 0.5, 0.5]", false)]
    public void SafeZones_rect_dentro_de_la_imagen_y_con_tamano(string rect, bool valid)
    {
        string json = Zones($$"""{ "label": "Z", "rect": {{rect}} }""");
        if (valid)
        {
            SafeZone zone = Assert.Single(ContentReaders.ReadSafeZones(Utf8(json)).Zones);
            Assert.True(zone.Width > 0 && zone.Height > 0 && zone.Left + zone.Width <= 1 && zone.Top + zone.Height <= 1);
        }
        else
        {
            Assert.Contains("se sale de la imagen", Error(ContentReaders.ReadSafeZones, json), StringComparison.Ordinal);
        }
    }

    // Regresión: en el borde, una zona con x = 1 y un ancho dentro de la holgura se aceptaba y se recortaba a ancho 0,
    // contra «ancho y alto mayores que 0»; el escritor la guardaba y el lector luego no la podía leer.
    [Theory]
    [InlineData("[1, 0, 0.0000005, 0.5]")]
    [InlineData("[0, 1, 0.5, 0.0000005]")]
    public void SafeZones_rect_en_el_borde_no_queda_con_tamano_cero(string rect) =>
        Assert.Contains("se sale de la imagen", Error(ContentReaders.ReadSafeZones, Zones($$"""{ "label": "Z", "rect": {{rect}} }""")), StringComparison.Ordinal);

    // «de 1 a 32 zonas; `label` de 1 a 60 caracteres; `kind` es `interface` (por defecto [...]) o `safe`»
    [Fact]
    public void SafeZones_cantidad_etiqueta_y_tipo()
    {
        string Many(int n) => Zones(string.Join(",", Enumerable.Repeat("""{ "label": "Z", "rect": [0, 0, 1, 1] }""", n)));
        Assert.Equal(32, ContentReaders.ReadSafeZones(Utf8(Many(32))).Zones.Count);
        Assert.Contains("de 1 a 32", Error(ContentReaders.ReadSafeZones, Many(33)), StringComparison.Ordinal);
        Assert.Contains("de 1 a 32", Error(ContentReaders.ReadSafeZones, Zones("")), StringComparison.Ordinal);

        string Label(string label) => Zones($$"""{ "label": "{{label}}", "rect": [0, 0, 1, 1] }""");
        Assert.Equal(60, ContentReaders.ReadSafeZones(Utf8(Label(new string('l', 60)))).Zones[0].Label.Length);
        Assert.Contains("de 1 a 60", Error(ContentReaders.ReadSafeZones, Label(new string('l', 61))), StringComparison.Ordinal);
        Assert.Contains("de 1 a 60", Error(ContentReaders.ReadSafeZones, Label("\\u0007")), StringComparison.Ordinal);

        Assert.Equal(SafeZoneKind.Interface, ContentReaders.ReadSafeZones(Utf8(Label("Z"))).Zones[0].Kind);
        Assert.Equal(SafeZoneKind.Safe, ContentReaders.ReadSafeZones(Utf8(Zones("""{ "label": "Z", "kind": "safe", "rect": [0, 0, 1, 1] }"""))).Zones[0].Kind);
    }

    // «`aspect` (enteros de 1 a 100) es la forma para la que se pensó: sin él sirve para cualquiera» / «`center: true`
    // pone la cruz del centro»
    [Theory]
    [InlineData(""", "aspect": { "width": 1, "height": 100 }""", true)]
    [InlineData(""", "aspect": { "width": 0, "height": 16 }""", false)]
    [InlineData(""", "aspect": { "width": 9, "height": 101 }""", false)]
    [InlineData(""", "aspect": { "width": 9.5, "height": 16 }""", false)]
    [InlineData(""", "aspect": { "width": 9 }""", false)]
    [InlineData(""", "center": true""", true)]
    [InlineData(""", "center": 1""", false)]
    public void SafeZones_forma_y_centro(string extra, bool valid)
    {
        string json = Zones("""{ "label": "Z", "rect": [0, 0, 1, 1] }""", extra);
        if (valid)
        {
            SafeZonePreset preset = ContentReaders.ReadSafeZones(Utf8(json));
            Assert.Equal(extra.Contains("center", StringComparison.Ordinal), preset.Center);
        }
        else
        {
            Error(ContentReaders.ReadSafeZones, json);
        }
    }

    // «El escritor (`ContentReaders.WriteSafeZones`) da el mismo JSON»
    [Fact]
    public void SafeZones_el_escritor_da_el_mismo_json()
    {
        const string Original = """
            { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels", "center": true,
              "aspect": { "width": 9, "height": 16 },
              "zones": [ { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.4] },
                         { "label": "Títulos", "kind": "safe", "rect": [0.05, 0.05, 0.9, 0.9] },
                         { "label": "Borde", "rect": [0.98415, 0, 0.01585, 1] } ] }
            """;
        SafeZonePreset read = ContentReaders.ReadSafeZones(Utf8(Original));
        byte[] written = ContentReaders.WriteSafeZones(read);
        SafeZonePreset again = ContentReaders.ReadSafeZones(new MemoryStream(written));
        Assert.Equal((read.Id, read.Name, read.AspectWidth, read.AspectHeight, read.Center), (again.Id, again.Name, again.AspectWidth, again.AspectHeight, again.Center));
        Assert.Equal(read.Zones.Select(z => (z.Label, z.Kind)), again.Zones.Select(z => (z.Label, z.Kind)));

        // Las posiciones se escriben a 4 decimales: lo escrito se vuelve a leer y a escribir igual, byte a byte.
        Assert.All(read.Zones.Zip(again.Zones), p =>
        {
            Assert.Equal(p.First.Left, p.Second.Left, 0.00005 + 1e-12);
            Assert.Equal(p.First.Width, p.Second.Width, 0.00005 + 1e-12);
            Assert.True(p.Second.Left + p.Second.Width <= 1);
        });
        Assert.Equal(written, ContentReaders.WriteSafeZones(again));

        SafeZonePreset bare = new("", "Sin forma", 0, 0, [new SafeZone("Z", SafeZoneKind.Interface, 0, 0, 1, 1)], false);
        string text = Encoding.UTF8.GetString(ContentReaders.WriteSafeZones(bare, "safe-zones"));
        Assert.DoesNotContain("\"id\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("aspect", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"safe-zones\"", text, StringComparison.Ordinal);
        Assert.Equal(bare.Zones, ContentReaders.ReadSafeZones(Utf8(text)).Zones);
    }

    // ── sound ───────────────────────────────────────────────────────────────────────────────────────────────────

    // «PCM de 16, 24 o 32 bits o float de 32 (también `WAVE_FORMAT_EXTENSIBLE` con esos subformatos), de 8 a 192 kHz y
    // de 1 a 8 canales»
    [Theory]
    [InlineData(8_000, 1, 16, 1, true)]
    [InlineData(192_000, 8, 24, 1, true)]
    [InlineData(48_000, 2, 32, 1, true)]
    [InlineData(48_000, 2, 32, 3, true)]
    [InlineData(48_000, 2, 64, 3, false)]
    [InlineData(48_000, 2, 16, 3, false)]
    [InlineData(48_000, 1, 8, 1, false)]
    [InlineData(7_999, 1, 16, 1, false)]
    [InlineData(192_001, 1, 16, 1, false)]
    [InlineData(48_000, 0, 16, 1, false)]
    [InlineData(48_000, 9, 16, 1, false)]
    [InlineData(48_000, 1, 16, 2, false)]
    public void Sound_formatos_frecuencias_y_canales(int rate, int channels, int bits, int tag, bool valid)
    {
        byte[] wav = ExportPresetAndSoundReaderTests.Wave(rate, Math.Max(channels, 1), bits, tag, frames: 10);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        if (valid)
        {
            SoundInfo info = ContentReaders.ReadSoundInfo(new MemoryStream(wav));
            Assert.Equal((rate, channels, bits, tag == 3, 10L), (info.SampleRate, info.Channels, info.BitsPerSample, info.IsFloat, info.Frames));
        }
        else
        {
            Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(wav)));
        }
    }

    [Theory]
    [InlineData(1, 24, false)]
    [InlineData(3, 32, true)]
    [InlineData(2, 16, null)]
    public void Sound_extensible_con_los_mismos_subformatos(int subformat, int bits, bool? isFloat)
    {
        byte[] wav = Extensible(subformat, bits);
        if (isFloat is { } f)
        {
            Assert.Equal(f, ContentReaders.ReadSoundInfo(new MemoryStream(wav)).IsFloat);
        }
        else
        {
            Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(wav)));
        }
    }

    // «hasta 64 MB» / «Se rechazan con el motivo: otra cabecera, [...] datos antes de `fmt`, sin datos o cortados»
    [Fact]
    public void Sound_rechazos_con_motivo()
    {
        Assert.Contains("64 MB", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(new byte[(64 * 1024 * 1024) + 1]))).Message, StringComparison.Ordinal);
        Assert.Contains("RIFF/WAVE", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream([.. "RIFF"u8, 0, 0, 0, 0, .. "AVI "u8]))).Message, StringComparison.Ordinal);

        byte[] good = ExportPresetAndSoundReaderTests.Wave(48_000, 1, 16, 1, frames: 4);
        byte[] dataFirst = [.. good[..12], .. good[36..], .. good[12..36]];
        Assert.Contains("antes del bloque fmt", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(dataFirst))).Message, StringComparison.Ordinal);

        byte[] empty = [.. good[..40], 0, 0, 0, 0];
        Assert.Contains("no trae muestras", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(empty))).Message, StringComparison.Ordinal);
        Assert.Contains("no trae el bloque de datos", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSoundInfo(new MemoryStream(good[..36]))).Message, StringComparison.Ordinal);
        Assert.Contains("cortado", Assert.Throws<PackageContentException>(() => ContentReaders.ReadSound(new MemoryStream(good[..^1]))).Message, StringComparison.Ordinal);
    }

    // Regresión: «RIFF/WAVE» rellena con un byte los bloques de tamaño impar; un `fmt ` de 17 o 19 bytes desalineaba
    // la lectura del bloque siguiente y el WAV válido se rechazaba sin datos.
    [Fact]
    public void Sound_bloque_fmt_impar_respeta_el_relleno()
    {
        byte[] good = ExportPresetAndSoundReaderTests.Wave(48_000, 2, 16, 1, frames: 8);
        byte[] odd = [.. good[..16], 17, 0, 0, 0, .. good[20..36], 0, 0, .. good[36..]];
        (SoundInfo info, float[] samples) = ContentReaders.ReadSound(new MemoryStream(odd));
        Assert.Equal((48_000, 2, 8L), (info.SampleRate, info.Channels, info.Frames));
        Assert.Equal(16, samples.Length);
        Assert.InRange(samples[0], 0.49f, 0.51f);
    }

    /// <summary>Un WAV <c>WAVE_FORMAT_EXTENSIBLE</c> de un canal con el subformato dado en los dos primeros bytes del GUID.</summary>
    private static byte[] Extensible(int subformat, int bits)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(40);
        w.Write((ushort)0xFFFE);
        w.Write((ushort)1);
        w.Write(48_000);
        w.Write(48_000 * bits / 8);
        w.Write((ushort)(bits / 8));
        w.Write((ushort)bits);
        w.Write((ushort)22);
        w.Write((ushort)bits);
        w.Write(4);
        w.Write((ushort)subformat);
        w.Write(new byte[14]);
        w.Write("data"u8);
        w.Write(bits / 8 * 4);
        w.Write(new byte[bits / 8 * 4]);
        w.Flush();
        return stream.ToArray();
    }

    // ── export-preset ───────────────────────────────────────────────────────────────────────────────────────────

    // «`category`: `platform`, `quality`, `master` (por defecto), [...]» / «`container`: Obligatorio» / «`fastStart`,
    // `chapters`: `true` o `false` (por defecto `true` y `false`)»
    [Fact]
    public void Export_categoria_contenedor_y_banderas()
    {
        ExportPresetDefinition d = ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "h264" }""")));
        Assert.Equal(("master", "mp4", true, false), (d.Category, d.Container, d.FastStart, d.Chapters));
        foreach (string category in (string[])["platform", "quality", "master", "web", "audio", "broadcast", "cinema", "archive"])
        {
            Assert.Equal(category, ContentReaders.ReadExportPreset(Utf8(Export($$""", "category": "{{category}}", "video": { "codec": "h264" }"""))).Category);
        }

        Assert.Contains("category", Error(ContentReaders.ReadExportPreset, Export(""", "category": "social", "video": { "codec": "h264" }""")), StringComparison.Ordinal);
        Assert.Contains("Falta container", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "h264" }""").Replace(", \"container\": \"mp4\"", "", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("container", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "h264" }""").Replace("\"mp4\"", "\"MP4\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("fastStart", Error(ContentReaders.ReadExportPreset, Export(""", "fastStart": "no", "video": { "codec": "h264" }""")), StringComparison.Ordinal);
        Assert.Contains("video, audio", Error(ContentReaders.ReadExportPreset, Export("")), StringComparison.Ordinal);
    }

    // «`video.codec`: Obligatorio con `video`» / «`video.profile`, `level`, `speedPreset`: Los del codificador (`hq`,
    // `4444`, `dnxhr_hqx`, `high`…); minúsculas, dígitos y `_`»
    [Fact]
    public void Export_codec_perfil_y_preajuste_de_velocidad()
    {
        ExportPresetVideo v = ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "dnxhr", "profile": "dnxhr_hqx", "speedPreset": "slow" }"""))).Video!;
        Assert.Equal(("dnxhr", "dnxhr_hqx", "slow"), (v.Codec, v.Profile, v.SpeedPreset));
        Assert.Equal("4444", ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "prores", "profile": "4444" }"""))).Video!.Profile);
        Assert.Contains("Falta video.codec", Error(ContentReaders.ReadExportPreset, Export(""", "video": {}""")), StringComparison.Ordinal);
        Assert.Contains("video.profile", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "h264", "profile": "High" }""")), StringComparison.Ordinal);
        Assert.Contains("video.speedPreset", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "h264", "speedPreset": "very-slow" }""")), StringComparison.Ordinal);
    }

    // «`video.bitDepth`, `chroma`, `alpha`: 8, 10, 12 o 16; `420`, `422` o `444`; `true` o `false`» / «`video.width`,
    // `height`, `fit`: De 16 a 16384 (sin ellos, el tamaño de la secuencia); `bars`, `crop` o `stretch`»
    [Theory]
    [InlineData("\"bitDepth\": 12", true)]
    [InlineData("\"bitDepth\": 14", false)]
    [InlineData("\"bitDepth\": 6", false)]
    [InlineData("\"chroma\": \"444\"", true)]
    [InlineData("\"chroma\": \"411\"", false)]
    [InlineData("\"alpha\": true", true)]
    [InlineData("\"alpha\": 1", false)]
    [InlineData("\"width\": 16, \"height\": 16384", true)]
    [InlineData("\"width\": 15", false)]
    [InlineData("\"height\": 16385", false)]
    [InlineData("\"fit\": \"crop\"", true)]
    [InlineData("\"fit\": \"zoom\"", false)]
    public void Export_profundidad_croma_alfa_tamano_y_ajuste(string field, bool valid)
    {
        string json = Export($$""", "video": { "codec": "prores", {{field}} }""");
        if (valid)
        {
            ContentReaders.ReadExportPreset(Utf8(json));
        }
        else
        {
            Error(ContentReaders.ReadExportPreset, json);
        }
    }

    [Fact]
    public void Export_valores_por_defecto_del_video()
    {
        ExportPresetVideo v = ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "h264" }"""))).Video!;
        Assert.Equal((8, "420", false, null, null, "bars"), (v.BitDepth, v.Chroma, v.Alpha, v.Width, v.Height, v.Fit));
        Assert.Equal((0, 0, "progressive", "flat"), (v.FrameRateNumerator, v.FrameRateDenominator, v.FieldOrder, v.Projection));
    }

    // «`video.frameRate`: Un número (25, 29.97, 23.976 y 59.94 se leen como 30000/1001 y compañía) o `"30000/1001"`;
    // sin ella, la de la secuencia»
    [Theory]
    [InlineData("25", 25, 1)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("\"30000/1001\"", 30000, 1001)]
    [InlineData("\"25/1\"", 25, 1)]
    [InlineData("0", 0, 0)]
    [InlineData("12.5", 0, 0)]
    [InlineData("\"25\"", 0, 0)]
    [InlineData("\"30000/0\"", 0, 0)]
    public void Export_tasa_de_cuadros(string rate, int num, int den)
    {
        string json = Export($$""", "video": { "codec": "h264", "frameRate": {{rate}} }""");
        if (num == 0)
        {
            Assert.Contains("video.frameRate", Error(ContentReaders.ReadExportPreset, json), StringComparison.Ordinal);
        }
        else
        {
            ExportPresetVideo v = ContentReaders.ReadExportPreset(Utf8(json)).Video!;
            Assert.Equal((num, den), (v.FrameRateNumerator, v.FrameRateDenominator));
        }
    }

    // «`video.fieldOrder`: `progressive`, `tff` o `bff`. Entrelazado necesita `frameRate`»
    [Fact]
    public void Export_entrelazado_necesita_tasa()
    {
        Assert.Equal("tff", ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "prores", "fieldOrder": "tff", "frameRate": 25 }"""))).Video!.FieldOrder);
        Assert.Contains("entrelazado", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "prores", "fieldOrder": "bff" }""")), StringComparison.Ordinal);
        Assert.Contains("video.fieldOrder", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "prores", "fieldOrder": "interlaced", "frameRate": 25 }""")), StringComparison.Ordinal);
    }

    // «`video.rateControl`: `crf`, `vbr`, `cbr`, `abr`, `lossless` o `profile`, con [...] `bitrateKbps` (obligatoria en
    // `vbr`, `cbr` y `abr`) [...] y `passes` (2 solo con `vbr` o `abr`)»
    [Theory]
    [InlineData("\"rateControl\": \"crf\", \"quality\": 23", true)]
    [InlineData("\"rateControl\": \"lossless\"", true)]
    [InlineData("\"rateControl\": \"profile\"", true)]
    [InlineData("\"rateControl\": \"cq\"", false)]
    [InlineData("\"rateControl\": \"cbr\"", false)]
    [InlineData("\"rateControl\": \"vbr\"", false)]
    [InlineData("\"rateControl\": \"abr\"", false)]
    [InlineData("\"rateControl\": \"cbr\", \"bitrateKbps\": 8000, \"maxBitrateKbps\": 8000, \"bufferKbps\": 16000", true)]
    [InlineData("\"rateControl\": \"vbr\", \"bitrateKbps\": 8000, \"passes\": 2", true)]
    [InlineData("\"rateControl\": \"abr\", \"bitrateKbps\": 8000, \"passes\": 2", true)]
    [InlineData("\"rateControl\": \"cbr\", \"bitrateKbps\": 8000, \"passes\": 2", false)]
    [InlineData("\"rateControl\": \"crf\", \"passes\": 2", false)]
    [InlineData("\"rateControl\": \"vbr\", \"bitrateKbps\": 8000, \"passes\": 3", false)]
    public void Export_control_de_tasa_del_video(string fields, bool valid)
    {
        string json = Export($$""", "video": { "codec": "h264", {{fields}} }""");
        if (valid)
        {
            ContentReaders.ReadExportPreset(Utf8(json));
        }
        else
        {
            Error(ContentReaders.ReadExportPreset, json);
        }
    }

    // «`video.projection`: `flat` o `equirectangular`»
    [Fact]
    public void Export_proyeccion()
    {
        Assert.Equal("equirectangular", ContentReaders.ReadExportPreset(Utf8(Export(""", "video": { "codec": "h264", "projection": "equirectangular" }"""))).Video!.Projection);
        Assert.Contains("video.projection", Error(ContentReaders.ReadExportPreset, Export(""", "video": { "codec": "h264", "projection": "cubemap" }""")), StringComparison.Ordinal);
    }

    // «`audio.codec`, `sampleRate`, `channels`, `sampleFormat`: `aac`, `opus`, `flac`, `pcm`…; de 8 a 192 kHz; de 1 a 8;
    // `s16`, `s24`, `s32` o `float`» / «`audio.rateControl`: `cbr`, `vbr`, `crf` o `lossless`»
    [Theory]
    [InlineData("\"sampleRate\": 8000, \"channels\": 8, \"sampleFormat\": \"float\"", true)]
    [InlineData("\"sampleRate\": 192000, \"channels\": 1, \"sampleFormat\": \"s16\"", true)]
    [InlineData("\"sampleRate\": 7999", false)]
    [InlineData("\"sampleRate\": 192001", false)]
    [InlineData("\"channels\": 0", false)]
    [InlineData("\"channels\": 9", false)]
    [InlineData("\"sampleFormat\": \"u8\"", false)]
    [InlineData("\"rateControl\": \"lossless\"", true)]
    [InlineData("\"rateControl\": \"abr\"", false)]
    public void Export_audio(string fields, bool valid)
    {
        string json = Export($$""", "audio": { "codec": "aac", {{fields}} }""");
        if (valid)
        {
            ContentReaders.ReadExportPreset(Utf8(json));
        }
        else
        {
            Error(ContentReaders.ReadExportPreset, json);
        }
    }

    // «`audio.loudness`: `lufs` (de −70 a −5) y `truePeak` (de −9 a 0, por defecto −1)»
    [Fact]
    public void Export_sonoridad()
    {
        ExportPresetAudio a = ContentReaders.ReadExportPreset(Utf8(Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -23 } }"""))).Audio!;
        Assert.Equal((-23.0, -1.0), (a.LoudnessLufs, a.LoudnessTruePeak));
        a = ContentReaders.ReadExportPreset(Utf8(Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -70, "truePeak": -9 } }"""))).Audio!;
        Assert.Equal((-70.0, -9.0), (a.LoudnessLufs, a.LoudnessTruePeak));
        Assert.Null(ContentReaders.ReadExportPreset(Utf8(Export(""", "audio": { "codec": "pcm" }"""))).Audio!.LoudnessLufs);
        Assert.Contains("lufs", Error(ContentReaders.ReadExportPreset, Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -4 } }""")), StringComparison.Ordinal);
        Assert.Contains("lufs", Error(ContentReaders.ReadExportPreset, Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -71 } }""")), StringComparison.Ordinal);
        Assert.Contains("truePeak", Error(ContentReaders.ReadExportPreset, Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -23, "truePeak": 0.5 } }""")), StringComparison.Ordinal);
        Assert.Contains("truePeak", Error(ContentReaders.ReadExportPreset, Export(""", "audio": { "codec": "pcm", "loudness": { "lufs": -23, "truePeak": -9.5 } }""")), StringComparison.Ordinal);
    }
}
