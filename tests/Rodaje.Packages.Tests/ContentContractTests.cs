using System.Globalization;
using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Contrato de «Contenido de los elementos» en <c>docs/formato-paquete.md</c>: una prueba por regla, con la frase que la
/// fija entre comillas latinas.
/// </summary>
public sealed class ContentContractTests
{
    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Fails(Action read) => Assert.Throws<PackageContentException>(read).Message;

    private static string Filter(string effects, string extra = "") =>
        $$"""{ "format": "rodaje.filter", "version": 1, "name": "F"{{extra}}, "effects": {{effects}} }""";

    private static string Effect(string keys, string parameter = "x", double duration = 1) =>
        $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": "start", "durationSeconds": {{duration.ToString(CultureInfo.InvariantCulture)}}, "curves": [ { "effect": "rodaje.transform", "parameter": "{{parameter}}", "keys": {{keys}} } ] }""";

    private static string Transition(string extra = "", string duration = "0.5") =>
        $$"""{ "format": "rodaje.preset", "version": 2, "name": "T", "durationSeconds": {{duration}}{{extra}} }""";

    private static string TextStyle(string extra = "", string version = "1") =>
        $$"""{ "format": "rodaje.textstyle", "version": {{version}}, "name": "S", "font": { "family": "Inter" }, "size": 96{{extra}} }""";

    private static string Zones(string zones, string extra = "") =>
        $$"""{ "format": "rodaje.safezones", "version": 1, "name": "Z"{{extra}}, "zones": [ {{zones}} ] }""";

    private static string Export(string body) =>
        $$"""{ "format": "rodaje.exportpreset", "version": 1, "name": "P", "container": "mp4", {{body}} }""";

    // ---- Reglas comunes de los JSON ----

    // «Hasta 1 MB, JSON (admite comentarios y comas finales) con un objeto en la raíz»
    [Fact]
    public void Comun_json_con_comentarios_y_comas_finales_hasta_1_MB_y_objeto_en_la_raiz()
    {
        FilterPreset filter = ContentReaders.ReadFilter(Utf8("""
            // comentario de línea
            { "format": "rodaje.filter", /* y de bloque */ "version": 1, "name": "F",
              "effects": [ { "type": "rodaje.color", }, ], }
            """));
        Assert.Equal("rodaje.color", Assert.Single(filter.Effects).Type);

        Assert.Contains("No es JSON válido", Fails(() => ContentReaders.ReadFilter(Utf8("{ \"format\": "))), StringComparison.Ordinal);
        Assert.Contains("objeto JSON", Fails(() => ContentReaders.ReadFilter(Utf8("[1]"))), StringComparison.Ordinal);
        string padded = Filter("""[ { "type": "rodaje.color" } ]""") + new string(' ', PresetReader.MaxFileBytes);
        Assert.Contains("pesa más de 1 MB", Fails(() => ContentReaders.ReadFilter(Utf8(padded))), StringComparison.Ordinal);
    }

    // «`format` es el del lector y `version` un entero desde 1» / «Una versión mayor que la que se entiende pide «actualiza el programa»»
    [Theory]
    [InlineData("\"rodaje.effect\"", "1", "se esperaba «rodaje.filter»")]
    [InlineData("\"rodaje.filter\"", "0", "version tiene que ser un entero desde 1")]
    [InlineData("\"rodaje.filter\"", "1.5", "version tiene que ser un entero desde 1")]
    [InlineData("\"rodaje.filter\"", "\"1\"", "version tiene que ser un entero desde 1")]
    [InlineData("\"rodaje.filter\"", "2", "actualiza el programa")]
    public void Comun_format_y_version(string format, string version, string expected) =>
        Assert.Contains(expected, Fails(() => ContentReaders.ReadFilter(Utf8(
            $$"""{ "format": {{format}}, "version": {{version}}, "name": "F", "effects": [ { "type": "rodaje.color" } ] }"""))), StringComparison.Ordinal);

    // «`name` obligatorio, hasta 80 caracteres sin controles ni marcas bidi» → «Falta name»
    [Fact]
    public void Comun_name_obligatorio_hasta_80_sin_controles_ni_bidi()
    {
        Assert.Contains("Falta name", Fails(() => ContentReaders.ReadFilter(Utf8("""{ "format": "rodaje.filter", "version": 1, "effects": [ { "type": "rodaje.color" } ] }"""))), StringComparison.Ordinal);
        Assert.Contains("Falta name", Fails(() => ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""").Replace("\"F\"", "\"\\u202E\\u0007\"", StringComparison.Ordinal)))), StringComparison.Ordinal);
        string eighty = new('n', SafeText.MaxName);
        Assert.Equal(eighty, ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""").Replace("\"F\"", $"\"{eighty}\"", StringComparison.Ordinal))).Name);
        Assert.Contains("name pasa de 80", Fails(() => ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""").Replace("\"F\"", $"\"{eighty}n\"", StringComparison.Ordinal)))), StringComparison.Ordinal);
        Assert.Equal("Cine", ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""").Replace("\"F\"", "\"\\u200ECi\\u0001ne\\u2066\"", StringComparison.Ordinal))).Name);
    }

    // «`id` opcional, con las reglas de los identificadores»
    [Fact]
    public void Comun_id_opcional_con_las_reglas_de_los_identificadores()
    {
        Assert.Equal("", ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]"""))).Id);
        Assert.Equal("cine-2", ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""", """, "id": "cine-2" """))).Id);
        Assert.Contains("no es válido", Fails(() => ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""", """, "id": "Cine" """)))), StringComparison.Ordinal);
        Assert.Contains("no es válido", Fails(() => ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.color" } ]""", """, "id": 3 """)))), StringComparison.Ordinal);
    }

    // «Tipos de efecto en minúsculas con un punto (`rodaje.color`, `otro.efecto`); nombres de parámetro y opción ASCII empezando por letra»
    // «Un tipo desconocido se conserva en el proyecto, pero el motor no lo aplica»
    [Theory]
    [InlineData("""{ "type": "otro.efecto", "parameters": { "a1": 1 }, "options": { "b2": "x" } }""", null)]
    [InlineData("""{ "type": "rodaje" }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "type": "Rodaje.color" }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "type": ".color" }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "type": "rodaje..color" }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "type": "rodaje.color", "parameters": { "1a": 1 } }""", "no es un nombre de parámetro válido")]
    [InlineData("""{ "type": "rodaje.color", "parameters": { "añil": 1 } }""", "no es un nombre de parámetro válido")]
    [InlineData("""{ "type": "rodaje.color", "options": { "_lut": "x" } }""", "no es un nombre de opción válido")]
    public void Comun_tipos_de_efecto_y_nombres(string effect, string? expected)
    {
        if (expected is null)
        {
            Assert.Equal("otro.efecto", ContentReaders.ReadFilter(Utf8(Filter($"[ {effect} ]"))).Effects[0].Type);
            return;
        }

        Assert.Contains(expected, Fails(() => ContentReaders.ReadFilter(Utf8(Filter($"[ {effect} ]")))), StringComparison.Ordinal);
    }

    // «Hasta 32 efectos, 64 curvas, 16 parámetros y 16 opciones por efecto, 1000 claves por curva, opciones de hasta 256 caracteres» → «Se rechaza el archivo»
    [Fact]
    public void Comun_limites_justo_en_el_tope_se_leen_y_uno_mas_se_rechaza()
    {
        static string Many(int count, Func<int, string> item) => string.Join(",", Enumerable.Range(0, count).Select(item));

        Assert.Equal(32, ContentReaders.ReadFilter(Utf8(Filter($"[ {Many(32, _ => """{ "type": "rodaje.color" }""")} ]"))).Effects.Count);
        Assert.Contains("más de 32 efectos", Fails(() => ContentReaders.ReadFilter(Utf8(Filter($"[ {Many(33, _ => """{ "type": "rodaje.color" }""")} ]")))), StringComparison.Ordinal);

        string parameters(int n) => $$"""[ { "type": "rodaje.color", "parameters": { {{Many(n, i => $"\"p{i}\": 1")}} } } ]""";
        Assert.Equal(16, ContentReaders.ReadFilter(Utf8(Filter(parameters(16)))).Effects[0].Parameters.Count);
        Assert.Contains("más de 16 valores", Fails(() => ContentReaders.ReadFilter(Utf8(Filter(parameters(17))))), StringComparison.Ordinal);

        string options(int n) => $$"""[ { "type": "rodaje.color", "options": { {{Many(n, i => $"\"o{i}\": \"a\"")}} } } ]""";
        Assert.Equal(16, ContentReaders.ReadFilter(Utf8(Filter(options(16)))).Effects[0].Options.Count);
        Assert.Contains("más de 16 opciones", Fails(() => ContentReaders.ReadFilter(Utf8(Filter(options(17))))), StringComparison.Ordinal);

        string option(int length) => $$"""[ { "type": "rodaje.color", "options": { "lut": "{{new string('a', length)}}" } } ]""";
        Assert.Equal(256, ContentReaders.ReadFilter(Utf8(Filter(option(256)))).Effects[0].Options["lut"].Length);
        Assert.Contains("pasa de 256", Fails(() => ContentReaders.ReadFilter(Utf8(Filter(option(257))))), StringComparison.Ordinal);

        string keys(int n) => $"[ {Many(n, i => $$"""{ "t": {{(i * 0.5).ToString(CultureInfo.InvariantCulture)}}, "value": 0 }""")} ]";
        Assert.Equal(1000, ContentReaders.ReadEffect(Utf8(Effect(keys(1000), duration: 600))).Curves[0].Keys.Count);
        Assert.Contains("más de 1000 claves", Fails(() => ContentReaders.ReadEffect(Utf8(Effect(keys(1001), duration: 600)))), StringComparison.Ordinal);

        string curves(int n) =>
            $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": "start", "durationSeconds": 1, "effects": [ { "type": "otro.efecto" } ], "curves": [ {{Many(n, i => $$"""{ "effect": "otro.efecto", "parameter": "p{{i}}", "keys": [ { "t": 0, "value": 0 } ] }""")}} ] }""";
        Assert.Equal(64, ContentReaders.ReadEffect(Utf8(curves(64))).Curves.Count);
        Assert.Contains("más de 64 curvas", Fails(() => ContentReaders.ReadEffect(Utf8(curves(65)))), StringComparison.Ordinal);
    }

    // «Valores finitos; `scale` de 0 a 100 y `opacity` de 0 a 1 en la transformación» → «Se rechaza el archivo»
    [Theory]
    [InlineData("scale", "100", true)]
    [InlineData("scale", "100.5", false)]
    [InlineData("scale", "-0.1", false)]
    [InlineData("opacity", "0", true)]
    [InlineData("opacity", "1.01", false)]
    [InlineData("x", "1e308", true)]
    [InlineData("x", "1e309", false)]
    public void Comun_rangos_de_la_transformacion_en_las_curvas(string parameter, string value, bool valid)
    {
        string json = Effect($$"""[ { "t": 0, "value": {{value}} } ]""", parameter);
        if (valid)
        {
            ContentReaders.ReadEffect(Utf8(json));
        }
        else
        {
            Fails(() => ContentReaders.ReadEffect(Utf8(json)));
        }
    }

    // La misma regla para los valores fijos de un efecto rodaje.transform en effects (filtro o efecto animado).
    [Theory]
    [InlineData("scale", "250")]
    [InlineData("scale", "-1")]
    [InlineData("opacity", "1.5")]
    [InlineData("opacity", "-0.5")]
    public void Comun_rangos_de_la_transformacion_en_los_valores_fijos(string parameter, string value)
    {
        string effect = $$"""[ { "type": "rodaje.transform", "parameters": { "{{parameter}}": {{value}} } } ]""";
        Assert.Contains("queda fuera de", Fails(() => ContentReaders.ReadFilter(Utf8(Filter(effect)))), StringComparison.Ordinal);
        string animated = $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": "whole", "durationSeconds": 1, "effects": {{effect}} }""";
        Assert.Contains("queda fuera de", Fails(() => ContentReaders.ReadEffect(Utf8(animated))), StringComparison.Ordinal);
    }

    [Fact]
    public void Comun_valores_fijos_de_la_transformacion_dentro_de_rango_se_leen()
    {
        FilterPreset filter = ContentReaders.ReadFilter(Utf8(Filter("""[ { "type": "rodaje.transform", "parameters": { "scale": 100, "opacity": 0, "x": -5000, "rotation": 720 } } ]""")));
        Assert.Equal(100, filter.Effects[0].Parameters["scale"]);
    }

    // «Claves `{ t, value, interp }`: `t` en segundos de 0 a `durationSeconds`, estrictamente creciente»
    [Theory]
    [InlineData("""[ { "t": 0, "value": 0 }, { "t": 1, "value": 0 } ]""", null)]
    [InlineData("""[ { "t": 1.01, "value": 0 } ]""", "fuera de 0 a durationSeconds")]
    [InlineData("""[ { "t": -0.01, "value": 0 } ]""", "fuera de 0 a durationSeconds")]
    [InlineData("""[ { "t": 0.5, "value": 0 }, { "t": 0.5, "value": 1 } ]""", "mayor que el de la clave anterior")]
    [InlineData("""[ { "t": 0.5, "value": 0 }, { "t": 0.4, "value": 1 } ]""", "mayor que el de la clave anterior")]
    public void Comun_t_de_cero_a_la_duracion_estrictamente_creciente(string keys, string? expected)
    {
        if (expected is null)
        {
            Assert.Equal(2, ContentReaders.ReadEffect(Utf8(Effect(keys))).Curves[0].Keys.Count);
            return;
        }

        Assert.Contains(expected, Fails(() => ContentReaders.ReadEffect(Utf8(Effect(keys)))), StringComparison.Ordinal);
    }

    // «`interp` es `hold`, `linear` (por defecto), `easeIn`, `easeOut`, `easeInOut`, `bounce`, `elastic` o `bezier`»
    [Theory]
    [InlineData(null, PresetInterpolation.Linear)]
    [InlineData("hold", PresetInterpolation.Hold)]
    [InlineData("linear", PresetInterpolation.Linear)]
    [InlineData("easeIn", PresetInterpolation.EaseIn)]
    [InlineData("easeOut", PresetInterpolation.EaseOut)]
    [InlineData("easeInOut", PresetInterpolation.EaseInOut)]
    [InlineData("bounce", PresetInterpolation.Bounce)]
    [InlineData("elastic", PresetInterpolation.Elastic)]
    public void Comun_interp_por_nombre_y_linear_por_defecto(string? interp, PresetInterpolation expected)
    {
        string key = interp is null ? """{ "t": 0, "value": 0 }""" : $$"""{ "t": 0, "value": 0, "interp": "{{interp}}" }""";
        Assert.Equal(expected, ContentReaders.ReadEffect(Utf8(Effect($"[ {key} ]"))).Curves[0].Keys[0].Interpolation);
    }

    // Un interp que no es ninguno de los nombres (tampoco un número o una lista) se rechaza; antes se leía como linear.
    [Theory]
    [InlineData("\"Linear\"")]
    [InlineData("3")]
    [InlineData("true")]
    [InlineData("[\"hold\"]")]
    public void Comun_interp_que_no_es_un_nombre_se_rechaza(string interp) =>
        Assert.Contains(".interp", Fails(() => ContentReaders.ReadEffect(Utf8(Effect($$"""[ { "t": 0, "value": 0, "interp": {{interp}} } ]""")))), StringComparison.Ordinal);

    // «`bezier`, que pide además `ease: [x1, y1, x2, y2]` (x de 0 a 1, y de −2 a 3)»
    [Theory]
    [InlineData("[0, -2, 1, 3]", null)]
    [InlineData("[0.5, 0.5, 0.5]", "[x1, y1, x2, y2]")]
    [InlineData("[-0.01, 0, 0.5, 1]", "x1 y x2 van de 0 a 1")]
    [InlineData("[0, 0, 1.01, 1]", "x1 y x2 van de 0 a 1")]
    [InlineData("[0, -2.01, 0.5, 1]", "de −2 a 3")]
    [InlineData("[0, 0, 0.5, 3.01]", "de −2 a 3")]
    public void Comun_bezier_pide_ease_con_sus_rangos(string ease, string? expected)
    {
        string json = Effect($$"""[ { "t": 0, "value": 0, "interp": "bezier", "ease": {{ease}} } ]""");
        if (expected is null)
        {
            Assert.Equal((0.0, -2.0, 1.0, 3.0), ContentReaders.ReadEffect(Utf8(json)).Curves[0].Keys[0].Ease);
            return;
        }

        Assert.Contains(expected, Fails(() => ContentReaders.ReadEffect(Utf8(json))), StringComparison.Ordinal);
    }

    // «`reference: { width, height }` (16 a 16384, por defecto 1920 × 1080)»
    [Theory]
    [InlineData("", 1920, 1080)]
    [InlineData(""", "reference": { "width": 16, "height": 16384 }""", 16, 16384)]
    [InlineData(""", "reference": { "width": 15, "height": 1080 }""", 0, 0)]
    [InlineData(""", "reference": { "width": 1920, "height": 16385 }""", 0, 0)]
    [InlineData(""", "reference": { "width": 1920.5, "height": 1080 }""", 0, 0)]
    [InlineData(""", "reference": [1920, 1080]""", 0, 0)]
    public void Comun_reference_de_16_a_16384_y_1920_por_1080_por_defecto(string reference, int width, int height)
    {
        string json = Transition(reference);
        if (width == 0)
        {
            Assert.Contains("reference necesita width y height", Fails(() => ContentReaders.ReadTransition(Utf8(json))), StringComparison.Ordinal);
            return;
        }

        TransitionPreset preset = ContentReaders.ReadTransition(Utf8(json));
        Assert.Equal((width, height), (preset.ReferenceWidth, preset.ReferenceHeight));
    }

    // ---- lut ----

    // «`LUT_3D_SIZE` de 2 a 65»
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(65, true)]
    [InlineData(66, false)]
    public void Lut_tamano_de_2_a_65(int size, bool valid)
    {
        string text = valid ? CubeReaderTests.Identity(size) : $"LUT_3D_SIZE {size}\n0 0 0\n";
        if (valid)
        {
            Assert.Equal(size, ContentReaders.ReadCube(Utf8(text)).Size);
        }
        else
        {
            Assert.Contains("LUT_3D_SIZE tiene que ser un entero de 2 a 65", Fails(() => ContentReaders.ReadCube(Utf8(text))), StringComparison.Ordinal);
        }
    }

    // «`TITLE` opcional, `DOMAIN_MIN`/`DOMAIN_MAX` (o `LUT_3D_INPUT_RANGE min max`), comentarios con `#` y luego exactamente N³ líneas de tres números con R variando más rápido»
    // «Los valores se llevan del dominio a 0–1 al leer»
    [Fact]
    public void Lut_cabecera_y_datos_con_r_variando_mas_rapido_llevados_a_cero_uno()
    {
        var text = new StringBuilder("# comentario\nTITLE \"Prueba\"\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 2 2 2\nLUT_3D_SIZE 2\n# otro\n");
        for (int i = 0; i < 8; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{i * 2} 0 1\n");
        }

        LutTable lut = ContentReaders.ReadCube(Utf8(text.ToString()));
        Assert.Equal("Prueba", lut.Title);
        // Entrada i en el orden del archivo: R es el índice que más rápido cambia.
        Assert.Equal(0f, lut.Rgb[0]);
        Assert.Equal(1f, lut.Rgb[3]);
        Assert.Equal(0.5f, lut.Rgb[2]);

        LutTable range = ContentReaders.ReadCube(Utf8(CubeReaderTests.Identity(2, "LUT_3D_INPUT_RANGE 0 0.5\n")));
        Assert.Equal(2f, range.Rgb[3]);
    }

    // «Se rechazan: LUT 1D (con el mensaje «Es una LUT 1D: Rodaje solo usa LUT 3D»), palabras clave desconocidas o después de los datos, repetidas, números no finitos, entradas de más o de menos y un dominio con mínimo ≥ máximo»
    [Theory]
    [InlineData("LUT_1D_SIZE 4\n0 0 0\n", "Es una LUT 1D: Rodaje solo usa LUT 3D")]
    [InlineData("LUT_3D_SIZE 2\nGAMMA 2.2\n", "palabra clave desconocida «GAMMA»")]
    [InlineData("LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n", "LUT_3D_SIZE está repetido")]
    [InlineData("TITLE \"a\"\nTITLE \"b\"\nLUT_3D_SIZE 2\n", "TITLE está repetido")]
    [InlineData("DOMAIN_MIN 0 0 0\nDOMAIN_MIN 0 0 0\nLUT_3D_SIZE 2\n", "DOMAIN_MIN está repetido")]
    [InlineData("LUT_3D_SIZE 2\n1e39 0 0\n", "se esperaban tres números")]
    [InlineData("LUT_3D_SIZE 2\nnan 0 0\n", "palabra clave desconocida «nan»")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "trae 1 entradas")]
    [InlineData("DOMAIN_MIN 0 1 0\nDOMAIN_MAX 1 1 1\nLUT_3D_SIZE 2\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n0 0 0\n", "DOMAIN_MIN tiene que ser menor")]
    public void Lut_rechazos_con_el_motivo(string text, string expected) =>
        Assert.Contains(expected, Fails(() => ContentReaders.ReadCube(Utf8(text))), StringComparison.Ordinal);

    [Fact]
    public void Lut_palabra_clave_despues_de_los_datos_y_entradas_de_mas()
    {
        string full = CubeReaderTests.Identity(2);
        Assert.Contains("tiene que ir antes de los datos", Fails(() => ContentReaders.ReadCube(Utf8(full + "TITLE \"tarde\"\n"))), StringComparison.Ordinal);
        Assert.Contains("más de las 8 entradas", Fails(() => ContentReaders.ReadCube(Utf8(full + "0 0 0\n"))), StringComparison.Ordinal);
    }

    // «Hasta 16 MB»
    [Fact]
    public void Lut_hasta_16_MB()
    {
        byte[] big = new byte[LutTable.MaxFileBytes + 1];
        Assert.Contains("16 MB", Fails(() => ContentReaders.ReadCube(new MemoryStream(big))), StringComparison.Ordinal);
    }

    // ---- filter ----

    // «Una lista no vacía de efectos con valores fijos; lo que falta toma el valor por defecto del registro»
    [Fact]
    public void Filtro_lista_no_vacia_y_sin_valores_inventados()
    {
        FilterPreset filter = ContentReaders.ReadFilter(Utf8("""
            { "format": "rodaje.filter", "version": 1, "id": "cine", "name": "Cine",
              "effects": [ { "type": "rodaje.color", "parameters": { "lutIntensity": 1 }, "options": { "lut": "./lut-cine" } },
                           { "type": "rodaje.vignette", "parameters": { "amount": 0.25 } } ] }
            """));
        Assert.Equal(["rodaje.color", "rodaje.vignette"], filter.Effects.Select(e => e.Type));
        Assert.Equal(["lutIntensity"], filter.Effects[0].Parameters.Keys);
        Assert.Equal("./lut-cine", filter.Effects[0].Options["lut"]);
        Assert.Contains("effects está vacía", Fails(() => ContentReaders.ReadFilter(Utf8(Filter("[]")))), StringComparison.Ordinal);
        Assert.Contains("Falta effects", Fails(() => ContentReaders.ReadFilter(Utf8("""{ "format": "rodaje.filter", "version": 1, "name": "F" }"""))), StringComparison.Ordinal);
    }

    // ---- effect ----

    // «`anchor` es `start` (al principio del clip), `end` (al final) o `whole` (estirada a todo el clip); `durationSeconds` de hasta 600 s»
    [Theory]
    [InlineData("\"start\"", "600", null)]
    [InlineData("\"end\"", "1", null)]
    [InlineData("\"whole\"", "1", null)]
    [InlineData("\"Start\"", "1", "anchor tiene que ser start, end o whole")]
    [InlineData("\"start\"", "600.001", "de hasta 600")]
    [InlineData("\"start\"", "0", "mayor que 0")]
    public void Efecto_anchor_y_duracion_hasta_600(string anchor, string duration, string? expected)
    {
        string json = $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": {{anchor}}, "durationSeconds": {{duration}}, "effects": [ { "type": "rodaje.blur" } ] }""";
        if (expected is null)
        {
            ContentReaders.ReadEffect(Utf8(json));
            return;
        }

        Assert.Contains(expected, Fails(() => ContentReaders.ReadEffect(Utf8(json))), StringComparison.Ordinal);
    }

    // «Cada curva anima un parámetro de `rodaje.transform` o de un efecto de `effects`, sin repetir el par efecto/parámetro»
    // «Hace falta al menos un efecto o una curva»
    [Fact]
    public void Efecto_curvas_de_la_transformacion_o_de_sus_efectos_sin_repetir()
    {
        string With(string effects, string curves) =>
            $$"""{ "format": "rodaje.effect", "version": 1, "name": "E", "anchor": "start", "durationSeconds": 1, "effects": {{effects}}, "curves": {{curves}} }""";
        const string Key = """[ { "t": 0, "value": 0 } ]""";

        EffectPreset ok = ContentReaders.ReadEffect(Utf8(With("""[ { "type": "rodaje.blur" } ]""",
            $$"""[ { "effect": "rodaje.blur", "parameter": "radius", "keys": {{Key}} }, { "effect": "rodaje.transform", "parameter": "x", "keys": {{Key}} } ]""")));
        Assert.Equal(2, ok.Curves.Count);
        Assert.Contains("que no está en effects", Fails(() => ContentReaders.ReadEffect(Utf8(With("[]", $$"""[ { "effect": "rodaje.blur", "parameter": "radius", "keys": {{Key}} } ]""")))), StringComparison.Ordinal);
        Assert.Contains("repite la curva de rodaje.blur/radius", Fails(() => ContentReaders.ReadEffect(Utf8(With("""[ { "type": "rodaje.blur" } ]""",
            $$"""[ { "effect": "rodaje.blur", "parameter": "radius", "keys": {{Key}} }, { "effect": "rodaje.blur", "parameter": "radius", "keys": {{Key}} } ]""")))), StringComparison.Ordinal);
        Assert.Contains("no trae effects ni curves", Fails(() => ContentReaders.ReadEffect(Utf8(With("[]", "[]")))), StringComparison.Ordinal);
    }

    // ---- transition ----

    // «por parámetro (`x`, `y`, `scale`, `rotation`, `opacity`)» / «`rodaje.crossfade` por defecto … nunca `rodaje.preset`» / «`durationSeconds` (hasta 60 s)» / «Las dos listas pueden faltar»
    [Fact]
    public void Transicion_parametros_mezcla_y_duracion_hasta_60()
    {
        const string Key = """[ { "t": 0, "value": 0 } ]""";
        TransitionPreset all = ContentReaders.ReadTransition(Utf8(Transition(
            $$""", "outgoing": { "x": {{Key}}, "y": {{Key}}, "scale": {{Key}}, "rotation": {{Key}}, "opacity": {{Key}} }""", "60")));
        Assert.Equal(["x", "y", "scale", "rotation", "opacity"], all.Outgoing.Select(c => c.Parameter));
        Assert.Equal("rodaje.crossfade", all.Mix);
        Assert.Empty(all.Incoming);

        Assert.Contains("de hasta 60", Fails(() => ContentReaders.ReadTransition(Utf8(Transition(duration: "60.5")))), StringComparison.Ordinal);
        Assert.Contains("mix no puede ser rodaje.preset", Fails(() => ContentReaders.ReadTransition(Utf8(Transition(""", "mix": "rodaje.preset" """)))), StringComparison.Ordinal);
        Assert.Contains("no es un parámetro de la transformación", Fails(() => ContentReaders.ReadTransition(Utf8(Transition($$""", "incoming": { "blur": {{Key}} }""")))), StringComparison.Ordinal);
    }

    // «El v1 de «Guardar como transición» se lee como curvas del clip que entra con fundido cruzado»
    [Fact]
    public void Transicion_v1_es_el_clip_que_entra_con_fundido_cruzado()
    {
        TransitionPreset v1 = ContentReaders.ReadTransition(Utf8("""
            { "format": "rodaje.preset", "version": 1, "name": "Z", "durationSeconds": 1, "mix": "rodaje.wipe",
              "tracks": { "opacity": [ { "t": 0, "value": 0 }, { "t": 1, "value": 1 } ] } }
            """));
        Assert.Equal("rodaje.crossfade", v1.Mix);
        Assert.Empty(v1.Outgoing);
        Assert.Equal("opacity", Assert.Single(v1.Incoming).Parameter);
    }

    // ---- text-style ----

    // «Obligatorios `font.family` y `size` (mayor que 0 y hasta 1000); lo demás tiene valor por defecto (peso 400, interlineado 1,2, centrado, relleno blanco) o falta (sin borde, sombra, fondo ni animación)»
    [Fact]
    public void Estilo_obligatorios_y_valores_por_defecto()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(TextStyle()));
        Assert.Equal((400, 1.2, TextStyleAlign.Center, "#FFFFFF"), (style.Font.Weight, style.LineHeight, style.Align, style.FillColor));
        Assert.Equal((1920, 1080), (style.ReferenceWidth, style.ReferenceHeight));
        Assert.Null(style.Stroke);
        Assert.Null(style.Shadow);
        Assert.Null(style.Background);
        Assert.Null(style.In);
        Assert.Null(style.Out);

        Assert.Equal(1000, ContentReaders.ReadTextStyle(Utf8(TextStyle().Replace("\"size\": 96", "\"size\": 1000", StringComparison.Ordinal))).Size);
        Assert.Contains("size", Fails(() => ContentReaders.ReadTextStyle(Utf8(TextStyle().Replace("\"size\": 96", "\"size\": 1000.5", StringComparison.Ordinal)))), StringComparison.Ordinal);
        Assert.Contains("size", Fails(() => ContentReaders.ReadTextStyle(Utf8(TextStyle().Replace("\"size\": 96", "\"size\": 0", StringComparison.Ordinal)))), StringComparison.Ordinal);
        Assert.Contains("Falta size", Fails(() => ContentReaders.ReadTextStyle(Utf8(TextStyle().Replace(", \"size\": 96", "", StringComparison.Ordinal)))), StringComparison.Ordinal);
        Assert.Contains("font.family", Fails(() => ContentReaders.ReadTextStyle(Utf8(TextStyle().Replace("\"family\": \"Inter\"", "\"weight\": 400", StringComparison.Ordinal)))), StringComparison.Ordinal);
    }

    // «colores `#RRGGBB` o `#RRGGBBAA`; peso entero de 100 a 900; hasta 8 fuentes de respaldo; `font.source` es la referencia de un elemento `font`»
    [Theory]
    [InlineData(""", "fill": { "color": "#00000099" }""", null)]
    [InlineData(""", "fill": { "color": "#0000009" }""", "fill.color")]
    [InlineData(""", "fill": { "color": "000000" }""", "fill.color")]
    [InlineData(""", "fill": { "color": "#GG0000" }""", "fill.color")]
    [InlineData(""", "font": { "family": "A", "weight": 100 }""", null)]
    [InlineData(""", "font": { "family": "A", "weight": 900 }""", null)]
    [InlineData(""", "font": { "family": "A", "weight": 99 }""", "font.weight")]
    [InlineData(""", "font": { "family": "A", "weight": 901 }""", "font.weight")]
    [InlineData(""", "font": { "family": "A", "fallback": ["1","2","3","4","5","6","7","8"] }""", null)]
    [InlineData(""", "font": { "family": "A", "fallback": ["1","2","3","4","5","6","7","8","9"] }""", "font.fallback")]
    [InlineData(""", "font": { "family": "A", "source": "rodaje-fuentes@1.0.0/inter" }""", null)]
    [InlineData(""", "font": { "family": "A", "source": "inter" }""", "font.source")]
    public void Estilo_colores_peso_respaldos_y_source(string extra, string? expected) => Check(TextStyle(extra), expected);

    // «borde de hasta la mitad de `size`, `blur` de 0 a 64, desplazamientos y márgenes de hasta 1000»
    [Theory]
    [InlineData(""", "stroke": { "color": "#000000", "width": 48 }""", null)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 48.01 }""", "stroke.width")]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 64, "dx": -1000, "dy": 1000 }""", null)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": -1 }""", "shadow.blur")]
    [InlineData(""", "shadow": { "color": "#000000", "dy": 1000.5 }""", "shadow.dy")]
    [InlineData(""", "background": { "color": "#000000", "padding": [1000, 0], "radius": 1000 }""", null)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1001, 0] }""", "background.padding")]
    [InlineData(""", "background": { "color": "#000000", "radius": 1001 }""", "background.radius")]
    public void Estilo_borde_sombra_y_margenes(string extra, string? expected) => Check(TextStyle(extra), expected);

    // «`highlight.color` es el color de la palabra que se está diciendo» / «`in` y `out` (hasta 60 s) son curvas de `rodaje.transform` por parámetro»
    [Theory]
    [InlineData(""", "highlight": { "color": "#FFD400" }""", null)]
    [InlineData(""", "highlight": { }""", "highlight.color")]
    [InlineData(""", "in": { "durationSeconds": 60, "curves": { "opacity": [ { "t": 0, "value": 0 }, { "t": 60, "value": 1 } ] } }""", null)]
    [InlineData(""", "out": { "durationSeconds": 60.5 }""", "out.durationSeconds")]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "scale": [ { "t": 0, "value": 101 } ] } }""", "fuera de 0 a 100")]
    public void Estilo_karaoke_y_animaciones(string extra, string? expected) => Check(TextStyle(extra), expected);

    // Versión 2: «`fill.gradient` (`kind` `linear` o `radial`, `angle` de −360 a 360, de 2 a 8 `stops` … en orden)»
    [Theory]
    [InlineData("\"kind\": \"radial\", \"angle\": -360, \"stops\": [ { \"position\": 0, \"color\": \"#000000\" }, { \"position\": 1, \"color\": \"#FFFFFF\" } ]", null)]
    [InlineData("\"kind\": \"conic\", \"stops\": [ { \"position\": 0, \"color\": \"#000000\" }, { \"position\": 1, \"color\": \"#FFFFFF\" } ]", "fill.gradient.kind")]
    [InlineData("\"angle\": 361, \"stops\": [ { \"position\": 0, \"color\": \"#000000\" }, { \"position\": 1, \"color\": \"#FFFFFF\" } ]", "fill.gradient.angle")]
    [InlineData("\"stops\": [ { \"position\": 0, \"color\": \"#000000\" } ]", "al menos dos paradas")]
    [InlineData("\"stops\": [ { \"position\": 0.6, \"color\": \"#000000\" }, { \"position\": 0.5, \"color\": \"#FFFFFF\" } ]", "mayor o igual que la anterior")]
    public void Estilo_v2_degradado(string gradient, string? expected) =>
        Check(TextStyle($$""", "fill": { "color": "#FFFFFF", "gradient": { {{gradient}} } }""", "2"), expected);

    [Fact]
    public void Estilo_v2_degradado_hasta_8_paradas()
    {
        string Stops(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{ "position": {{(i / 10.0).ToString(CultureInfo.InvariantCulture)}}, "color": "#000000" }"""));
        Check(TextStyle($$""", "fill": { "color": "#FFFFFF", "gradient": { "stops": [ {{Stops(8)}} ] } }""", "2"), null);
        Check(TextStyle($$""", "fill": { "color": "#FFFFFF", "gradient": { "stops": [ {{Stops(9)}} ] } }""", "2"), "stops");
    }

    // «`extraStrokes` y `extraShadows` (hasta 4 cada uno, con los campos de `stroke` y `shadow`)»
    [Fact]
    public void Estilo_v2_capas_extra_hasta_4()
    {
        string Strokes(int n) => string.Join(",", Enumerable.Repeat("""{ "color": "#000000", "width": 2 }""", n));
        string Shadows(int n) => string.Join(",", Enumerable.Repeat("""{ "color": "#000000", "blur": 4 }""", n));
        Check(TextStyle($", \"extraStrokes\": [ {Strokes(4)} ], \"extraShadows\": [ {Shadows(4)} ]", "2"), null);
        Check(TextStyle($", \"extraStrokes\": [ {Strokes(5)} ]", "2"), "extraStrokes");
        Check(TextStyle($", \"extraShadows\": [ {Shadows(5)} ]", "2"), "extraShadows");
        Check(TextStyle(""", "extraStrokes": [ { "color": "#000000", "width": 60 } ]""", "2"), "extraStrokes[0].width");
        Check(TextStyle(""", "extraShadows": [ { "color": "#000000", "blur": 65 } ]""", "2"), "extraShadows[0].blur");
    }

    // «`typography` (`kerning`, `case`: `none`, `upper`, `lower` o `smallCaps`, `features`: hasta 16 etiquetas OpenType de 4 caracteres con `-` delante para apagarlas, `fit`: `none`, `shrink` o `fill`)»
    [Theory]
    [InlineData("\"kerning\": false, \"case\": \"smallCaps\", \"features\": [\"ss01\", \"-liga\"], \"fit\": \"shrink\"", null)]
    [InlineData("\"kerning\": 1", "typography.kerning")]
    [InlineData("\"case\": \"title\"", "typography.case")]
    [InlineData("\"fit\": \"grow\"", "typography.fit")]
    [InlineData("\"features\": [\"liga1\"]", "etiqueta OpenType")]
    [InlineData("\"features\": [\"lig\"]", "etiqueta OpenType")]
    public void Estilo_v2_tipografia(string typography, string? expected) =>
        Check(TextStyle($$""", "typography": { {{typography}} }""", "2"), expected);

    [Fact]
    public void Estilo_v2_hasta_16_funciones_opentype()
    {
        string Features(int n) => string.Join(",", Enumerable.Range(1, n).Select(i => $"\"ss{i:00}\""));
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(TextStyle($", \"typography\": {{ \"features\": [ {Features(16)} ] }}", "2")));
        Assert.Equal(16, style.Typography!.Features.Count);
        Assert.True(style.Typography.Kerning);
        Assert.Equal(("none", "none"), (style.Typography.Case, style.Typography.Fit));
        Check(TextStyle($", \"typography\": {{ \"features\": [ {Features(17)} ] }}", "2"), "features");
    }

    // «`path` (`{ "kind": "arc", "bend" }` de −360 a 360), `highlight.mode` (`word` o `sweep`)»
    [Theory]
    [InlineData(""", "path": { "kind": "arc", "bend": -360 }""", null)]
    [InlineData(""", "path": { "kind": "arc", "bend": 360.5 }""", "path.bend")]
    [InlineData(""", "path": { "kind": "wave", "bend": 10 }""", "path.kind")]
    [InlineData(""", "highlight": { "color": "#FFD400", "mode": "sweep" }""", null)]
    [InlineData(""", "highlight": { "color": "#FFD400", "mode": "line" }""", "highlight.mode")]
    public void Estilo_v2_arco_y_modo_de_karaoke(string extra, string? expected) => Check(TextStyle(extra, "2"), expected);

    // «en `in` y `out`, `unit` (`block`, `line`, `word` o `letter`), `stagger` (0 a 1), `order` (`forward`, `backward`, `center`, `edges` o `random`) y la curva `blur` (0 a 64)»
    [Theory]
    [InlineData("\"unit\": \"letter\", \"stagger\": 1, \"order\": \"edges\", \"curves\": { \"blur\": [ { \"t\": 0, \"value\": 64 } ] }", null)]
    [InlineData("\"unit\": \"char\"", "in.unit")]
    [InlineData("\"stagger\": 1.01", "in.stagger")]
    [InlineData("\"stagger\": -0.01", "in.stagger")]
    [InlineData("\"order\": \"reverse\"", "in.order")]
    [InlineData("\"curves\": { \"blur\": [ { \"t\": 0, \"value\": 64.5 } ] }", "fuera de 0 a 64")]
    public void Estilo_v2_animacion_por_unidades(string animation, string? expected) =>
        Check(TextStyle($$""", "in": { "durationSeconds": 1, {{animation}} }""", "2"), expected);

    [Fact]
    public void Estilo_v2_animacion_por_defecto_es_por_bloque_hacia_delante()
    {
        TextStyleAnimation animation = ContentReaders.ReadTextStyle(Utf8(TextStyle(""", "in": { "durationSeconds": 1 }"""))).In!;
        Assert.Equal(("block", 0.0, "forward"), (animation.Unit, animation.Stagger, animation.Order));
    }

    // ---- safe-zones ----

    // «`rect` es `[x, y, ancho, alto]` en fracciones de la imagen (0 a 1, desde arriba a la izquierda), con ancho y alto mayores que 0 y sin salirse (`x + ancho` e `y + alto` hasta 1)»
    [Theory]
    [InlineData("[0, 0, 1, 1]", true)]
    [InlineData("[0.25, 0.5, 0.75, 0.5]", true)]
    [InlineData("[0, 0, 0, 0.5]", false)]
    [InlineData("[0, 0, 0.5, 0]", false)]
    [InlineData("[0, -0.01, 0.5, 0.5]", false)]
    [InlineData("[0.5, 0, 0.51, 0.5]", false)]
    [InlineData("[0, 0.5, 0.5, 0.51]", false)]
    public void Zonas_rect_en_fracciones_sin_salirse(string rect, bool valid)
    {
        string json = Zones($$"""{ "label": "A", "rect": {{rect}} }""");
        if (valid)
        {
            ContentReaders.ReadSafeZones(Utf8(json));
        }
        else
        {
            Assert.Contains("se sale de la imagen", Fails(() => ContentReaders.ReadSafeZones(Utf8(json))), StringComparison.Ordinal);
        }
    }

    // «de 1 a 32 zonas; `label` de 1 a 60 caracteres; `kind` es `interface` (por defecto: lo que tapa la interfaz) o `safe`»
    [Fact]
    public void Zonas_de_1_a_32_etiqueta_de_1_a_60_y_kind_por_defecto_interface()
    {
        string Zone(string label) => $$"""{ "label": "{{label}}", "rect": [0, 0, 0.5, 0.5] }""";
        Assert.Equal(32, ContentReaders.ReadSafeZones(Utf8(Zones(string.Join(",", Enumerable.Repeat(Zone("A"), 32))))).Zones.Count);
        Assert.Contains("de 1 a 32 zonas", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones(string.Join(",", Enumerable.Repeat(Zone("A"), 33)))))), StringComparison.Ordinal);
        Assert.Contains("de 1 a 32 zonas", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones("")))), StringComparison.Ordinal);

        Assert.Equal(60, ContentReaders.ReadSafeZones(Utf8(Zones(Zone(new string('a', 60))))).Zones[0].Label.Length);
        Assert.Contains("label tiene que tener de 1 a 60", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones(Zone(new string('a', 61)))))), StringComparison.Ordinal);
        Assert.Contains("label tiene que tener de 1 a 60", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones(Zone(" "))))), StringComparison.Ordinal);

        SafeZonePreset preset = ContentReaders.ReadSafeZones(Utf8(Zones(Zone("A") + """, { "label": "B", "kind": "safe", "rect": [0, 0, 1, 1] }""")));
        Assert.Equal([SafeZoneKind.Interface, SafeZoneKind.Safe], preset.Zones.Select(z => z.Kind));
        Assert.Contains("kind «Safe» no existe", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones("""{ "label": "A", "kind": "Safe", "rect": [0, 0, 1, 1] }""")))), StringComparison.Ordinal);
    }

    // Un kind que no es texto tampoco es interface ni safe; antes se leía como interface.
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[\"safe\"]")]
    public void Zonas_kind_que_no_es_texto_se_rechaza(string kind) =>
        Assert.Contains("zones[0].kind", Fails(() => ContentReaders.ReadSafeZones(Utf8(Zones($$"""{ "label": "A", "kind": {{kind}}, "rect": [0, 0, 1, 1] }""")))), StringComparison.Ordinal);

    // «`aspect` (enteros de 1 a 100) es la forma para la que se pensó: sin él sirve para cualquiera» / «`center: true` pone la cruz del centro»
    [Theory]
    [InlineData(""", "aspect": { "width": 1, "height": 100 }""", null)]
    [InlineData(""", "aspect": { "width": 0, "height": 16 }""", "aspect.width")]
    [InlineData(""", "aspect": { "width": 9, "height": 101 }""", "aspect.height")]
    [InlineData(""", "aspect": { "width": 9.5, "height": 16 }""", "aspect.width")]
    [InlineData(""", "aspect": { "width": 9 }""", "aspect.height")]
    [InlineData(""", "center": "true" """, "center tiene que ser true o false")]
    public void Zonas_aspect_y_center(string extra, string? expected) =>
        Check(Zones("""{ "label": "A", "rect": [0, 0, 1, 1] }""", extra), expected, ContentReaders.ReadSafeZones);

    [Fact]
    public void Zonas_sin_aspect_sirven_para_cualquiera_y_sin_center_no_hay_cruz()
    {
        SafeZonePreset any = ContentReaders.ReadSafeZones(Utf8(Zones("""{ "label": "A", "rect": [0, 0, 1, 1] }""")));
        Assert.Equal((0, 0, "", false), (any.AspectWidth, any.AspectHeight, any.AspectText, any.Center));
        Assert.True(any.Fits(1920, 1080));
        SafeZonePreset vertical = ContentReaders.ReadSafeZones(Utf8(Zones("""{ "label": "A", "rect": [0, 0, 1, 1] }""", """, "aspect": { "width": 9, "height": 16 }, "center": true""")));
        Assert.Equal(("9:16", true), (vertical.AspectText, vertical.Center));
        Assert.True(vertical.Fits(1080, 1920));
        Assert.False(vertical.Fits(1920, 1080));
    }

    // «El escritor (`ContentReaders.WriteSafeZones`) da el mismo JSON»
    [Fact]
    public void Zonas_el_escritor_da_el_mismo_json()
    {
        const string Reels = """
            { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels",
              "aspect": { "width": 9, "height": 16 },
              "zones": [ { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.4] },
                         { "label": "Nombre y pie de foto", "kind": "interface", "rect": [0, 0.78, 0.84, 0.16] },
                         { "label": "Títulos", "kind": "safe", "rect": [0.05, 0.05, 0.9, 0.9] } ] }
            """;
        SafeZonePreset read = ContentReaders.ReadSafeZones(Utf8(Reels));
        byte[] written = ContentReaders.WriteSafeZones(read);
        SafeZonePreset back = ContentReaders.ReadSafeZones(new MemoryStream(written));

        Assert.Equal(read with { Zones = [] }, back with { Zones = [] });
        Assert.Equal(read.Zones, back.Zones);
        Assert.Equal(written, ContentReaders.WriteSafeZones(back));

        // Sin forma ni id, con cruz, y con el kind del elemento propio suelto.
        SafeZonePreset bare = new("", "Propio", 0, 0, [new SafeZone("Logo", SafeZoneKind.Safe, 0.1, 0.2, 0.3, 0.4)], true);
        byte[] own = ContentReaders.WriteSafeZones(bare, "safe-zones");
        Assert.Equal(bare with { Zones = [] }, ContentReaders.ReadSafeZones(new MemoryStream(own)) with { Zones = [] });
        Assert.Equal(bare.Zones, ContentReaders.ReadSafeZones(new MemoryStream(own)).Zones);
        string text = Encoding.UTF8.GetString(own);
        Assert.DoesNotContain("\"aspect\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", text, StringComparison.Ordinal);
    }

    // ---- sound ----

    // «RIFF/WAVE con PCM de 16, 24 o 32 bits o float de 32 (también `WAVE_FORMAT_EXTENSIBLE` con esos subformatos), de 8 a 192 kHz y de 1 a 8 canales»
    [Theory]
    [InlineData(8_000, 1, 16, 1, true)]
    [InlineData(192_000, 8, 24, 1, true)]
    [InlineData(48_000, 2, 32, 1, true)]
    [InlineData(48_000, 2, 32, 3, true)]
    [InlineData(7_999, 1, 16, 1, false)]
    [InlineData(192_001, 1, 16, 1, false)]
    [InlineData(48_000, 9, 16, 1, false)]
    [InlineData(48_000, 1, 8, 1, false)]
    [InlineData(48_000, 1, 64, 3, false)]
    [InlineData(48_000, 1, 16, 2, false)]
    public void Sonido_formatos_frecuencias_y_canales(int rate, int channels, int bits, int tag, bool valid)
    {
        byte[] wav = ExportPresetAndSoundReaderTests.Wave(rate, channels, bits, tag, frames: 4);
        if (valid)
        {
            Assert.Equal(new SoundInfo(rate, channels, bits, tag == 3, 4), ContentReaders.ReadSoundInfo(new MemoryStream(wav)));
        }
        else
        {
            Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(wav)));
        }
    }

    [Theory]
    [InlineData(1, 24, true)]
    [InlineData(3, 32, true)]
    [InlineData(3, 64, false)]
    public void Sonido_extensible_con_los_mismos_subformatos(int subformat, int bits, bool valid)
    {
        byte[] wav = Extensible(subformat, bits);
        if (valid)
        {
            Assert.Equal(new SoundInfo(48_000, 2, bits, subformat == 3, 2), ContentReaders.ReadSound(new MemoryStream(wav)).Info);
        }
        else
        {
            Fails(() => ContentReaders.ReadSound(new MemoryStream(wav)));
        }
    }

    // «hasta 64 MB» / «Se rechazan con el motivo: otra cabecera, otro formato de muestra, frecuencias o canales fuera de rango, datos antes de `fmt`, sin datos o cortados»
    [Fact]
    public void Sonido_rechazos_con_el_motivo()
    {
        byte[] good = ExportPresetAndSoundReaderTests.Wave(48_000, 1, 16, 1, 8);
        byte[] other = (byte[])good.Clone();
        "AVI "u8.CopyTo(other.AsSpan(8));
        Assert.Contains("RIFF/WAVE", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(other))), StringComparison.Ordinal);

        // data antes de fmt: se cambia el orden de los dos bloques.
        byte[] dataFirst = [.. good[..12], .. good[36..], .. good[12..36]];
        Assert.Contains("antes del bloque fmt", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(dataFirst))), StringComparison.Ordinal);

        byte[] empty = good[..44];
        BitConverter.TryWriteBytes(empty.AsSpan(40), 0);
        Assert.Contains("no trae muestras", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(empty))), StringComparison.Ordinal);
        Assert.Contains("no trae el bloque de datos", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(good[..36]))), StringComparison.Ordinal);
        Assert.Contains("cortado", Fails(() => ContentReaders.ReadSound(new MemoryStream(good[..^2]))), StringComparison.Ordinal);

        Assert.Contains("64 MB", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(new byte[SoundInfo.MaxFileBytes + 1]))), StringComparison.Ordinal);
        byte[] huge = (byte[])good.Clone();
        BitConverter.TryWriteBytes(huge.AsSpan(40), (uint)SoundInfo.MaxFileBytes);
        Assert.Contains("64 MB", Fails(() => ContentReaders.ReadSoundInfo(new MemoryStream(huge))), StringComparison.Ordinal);
    }

    // ---- export-preset ----

    // «`category`: `platform`, `quality`, `master` (por defecto), `web`, `audio`, `broadcast`, `cinema` o `archive`» / «`container`: Obligatorio … en minúsculas» / «`fastStart`, `chapters`: `true` o `false` (por defecto `true` y `false`)»
    [Theory]
    [InlineData("platform")]
    [InlineData("quality")]
    [InlineData("master")]
    [InlineData("web")]
    [InlineData("audio")]
    [InlineData("broadcast")]
    [InlineData("cinema")]
    [InlineData("archive")]
    public void Exportar_categorias(string category) =>
        Assert.Equal(category, ContentReaders.ReadExportPreset(Utf8(Export($$""" "category": "{{category}}", "video": { "codec": "h264" }"""))).Category);

    [Fact]
    public void Exportar_valores_por_defecto_y_rechazos_de_la_raiz()
    {
        ExportPresetDefinition preset = ContentReaders.ReadExportPreset(Utf8(Export("""
            "video": { "codec": "h264" }, "audio": { "codec": "aac", "loudness": { "lufs": -14 } }
            """)));
        Assert.Equal(("master", true, false), (preset.Category, preset.FastStart, preset.Chapters));
        Assert.Equal(-1, preset.Audio!.LoudnessTruePeak);

        Check(Export(""" "category": "Master", "video": { "codec": "h264" }"""), "category", ContentReaders.ReadExportPreset);
        Check(Export(""" "fastStart": "no", "video": { "codec": "h264" }"""), "fastStart", ContentReaders.ReadExportPreset);
        Check(Export(""" "chapters": 1, "video": { "codec": "h264" }"""), "chapters", ContentReaders.ReadExportPreset);
        Check(Export(""" "video": { "codec": "h264" }""").Replace("\"mp4\"", "\"MP4\"", StringComparison.Ordinal), "container", ContentReaders.ReadExportPreset);
        Check(Export(""" "video": { }"""), "Falta video.codec", ContentReaders.ReadExportPreset);
    }

    // Tabla de campos de video y audio, una fila por regla.
    [Theory]
    [InlineData("\"codec\": \"dnxhr\", \"profile\": \"dnxhr_hqx\"", null)]
    [InlineData("\"codec\": \"prores\", \"profile\": \"4444\"", null)]
    [InlineData("\"codec\": \"h264\", \"speedPreset\": \"Slow\"", "video.speedPreset")]
    [InlineData("\"codec\": \"h264\", \"bitDepth\": 16, \"chroma\": \"444\", \"alpha\": true", null)]
    [InlineData("\"codec\": \"h264\", \"bitDepth\": 14", "video.bitDepth")]
    [InlineData("\"codec\": \"h264\", \"alpha\": \"yes\"", "alpha")]
    [InlineData("\"codec\": \"h264\", \"width\": 16, \"height\": 16384, \"fit\": \"stretch\"", null)]
    [InlineData("\"codec\": \"h264\", \"height\": 16385", "video.height")]
    [InlineData("\"codec\": \"h264\", \"fit\": \"pad\"", "video.fit")]
    [InlineData("\"codec\": \"h264\", \"frameRate\": 241", "frameRate")]
    [InlineData("\"codec\": \"h264\", \"frameRate\": \"60000/1001\", \"fieldOrder\": \"bff\"", null)]
    [InlineData("\"codec\": \"h264\", \"fieldOrder\": \"interlaced\", \"frameRate\": 25", "video.fieldOrder")]
    [InlineData("\"codec\": \"h264\", \"fieldOrder\": \"bff\"", "entrelazado")]
    [InlineData("\"codec\": \"h264\", \"rateControl\": \"abr\", \"bitrateKbps\": 5000, \"passes\": 2", null)]
    [InlineData("\"codec\": \"h264\", \"rateControl\": \"cbr\", \"bitrateKbps\": 5000, \"passes\": 2", "passes 2")]
    [InlineData("\"codec\": \"h264\", \"rateControl\": \"abr\"", "bitrateKbps")]
    [InlineData("\"codec\": \"h264\", \"rateControl\": \"constant\"", "video.rateControl")]
    [InlineData("\"codec\": \"h264\", \"projection\": \"equirectangular\"", null)]
    public void Exportar_campos_de_video(string video, string? expected) =>
        Check(Export($$""" "video": { {{video}} }"""), expected, ContentReaders.ReadExportPreset);

    [Theory]
    [InlineData("\"codec\": \"flac\", \"sampleRate\": 8000, \"channels\": 8, \"sampleFormat\": \"float\", \"rateControl\": \"lossless\"", null)]
    [InlineData("\"codec\": \"aac\", \"sampleRate\": 192001", "audio.sampleRate")]
    [InlineData("\"codec\": \"aac\", \"channels\": 0", "audio.channels")]
    [InlineData("\"codec\": \"pcm\", \"sampleFormat\": \"s8\"", "audio.sampleFormat")]
    [InlineData("\"codec\": \"aac\", \"rateControl\": \"abr\"", "audio.rateControl")]
    [InlineData("\"codec\": \"aac\", \"loudness\": { \"lufs\": -70, \"truePeak\": -9 }", null)]
    [InlineData("\"codec\": \"aac\", \"loudness\": { \"lufs\": -4.9 }", "audio.loudness.lufs")]
    [InlineData("\"codec\": \"aac\", \"loudness\": { \"lufs\": -23, \"truePeak\": 0.1 }", "audio.loudness.truePeak")]
    public void Exportar_campos_de_audio(string audio, string? expected) =>
        Check(Export($$""" "audio": { {{audio}} }"""), expected, ContentReaders.ReadExportPreset);

    private static void Check(string json, string? expected) => Check(json, expected, ContentReaders.ReadTextStyle);

    private static void Check<T>(string json, string? expected, Func<Stream, T> read)
    {
        if (expected is null)
        {
            read(Utf8(json));
            return;
        }

        Assert.Contains(expected, Fails(() => read(Utf8(json))), StringComparison.Ordinal);
    }

    /// <summary>Un WAV <c>WAVE_FORMAT_EXTENSIBLE</c> estéreo de 48 kHz y dos cuadros con el subformato dado (1 PCM, 3 float).</summary>
    private static byte[] Extensible(int subformat, int bits)
    {
        int bytes = bits / 8;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(40);
        w.Write((ushort)0xFFFE);
        w.Write((ushort)2);
        w.Write(48_000);
        w.Write(48_000 * 2 * bytes);
        w.Write((ushort)(2 * bytes));
        w.Write((ushort)bits);
        w.Write((ushort)22);
        w.Write((ushort)bits);
        w.Write(3);
        w.Write((ushort)subformat);
        w.Write((byte[])[0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71]);
        w.Write("data"u8);
        w.Write(2 * 2 * bytes);
        w.Write(new byte[2 * 2 * bytes]);
        w.Flush();
        byte[] result = stream.ToArray();
        BitConverter.TryWriteBytes(result.AsSpan(4), result.Length - 8);
        return result;
    }
}
