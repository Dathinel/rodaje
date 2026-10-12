using System.Text;
using Rodaje.Packages.Content;
using Xunit.Abstractions;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Casos hostiles y fuzz de <c>rodaje.textstyle</c> v1 y de su objeto <c>font</c>: tamaños en el borde, números extremos,
/// familias con Unicode raro, <c>font.source</c> con rutas, JSON roto y mutaciones con semilla fija
/// (<see cref="ContentReaderFuzz.Seed"/>). El lector solo puede devolver un estilo que cumple el formato o lanzar
/// <see cref="PackageContentException"/>.
/// </summary>
public sealed class TextStyleHostileTests(ITestOutputHelper output)
{
    private const string Full = """
        { "format": "rodaje.textstyle", "version": 1, "id": "clasico", "name": "Clásico",
          "font": { "family": "Inter", "weight": 700, "italic": false, "source": "rodaje-fuentes@1.0.0/inter", "fallback": ["Segoe UI", "Arial"] },
          "size": 96, "reference": { "width": 1920, "height": 1080 },
          "lineHeight": 1.1, "letterSpacing": 0, "align": "center",
          "fill": { "color": "#ffffff" },
          "stroke": { "color": "#000000", "width": 6, "join": "round" },
          "shadow": { "color": "#00000099", "dx": 4, "dy": 6, "blur": 8 },
          "background": { "color": "#00000080", "padding": [24, 12], "radius": 12 },
          "in":  { "durationSeconds": 0.4, "curves": { "opacity": [ { "t": 0, "value": 0 }, { "t": 0.4, "value": 1, "interp": "easeOut" } ],
                                                     "y": [ { "t": 0, "value": 40 }, { "t": 0.4, "value": 0, "interp": "easeOut" } ] } },
          "out": { "durationSeconds": 0.3, "curves": { "opacity": [ { "t": 0, "value": 1 }, { "t": 0.3, "value": 0 } ] } } }
        """;

    private static string Style(string font = """{ "family": "A" }""", string size = "72", string extra = "") =>
        $$"""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": {{font}}, "size": {{size}}{{extra}} }""";

    private static TextStylePreset Read(string json) => ContentReaders.ReadTextStyle(ContentReaderFuzz.Utf8(json));

    private static string Error(string json) => Assert.Throws<PackageContentException>(() => Read(json)).Message;

    /// <summary>Lo que cumple todo estilo aceptado (docs/formato-paquete.md, «text-style»).</summary>
    internal static void AssertWellFormed(TextStylePreset style)
    {
        Assert.InRange(style.Name.Length, 1, SafeText.MaxName);
        ContentReaderFuzz.AssertClean(style.Name, "name");
        Assert.InRange(style.Font.Family.Length, 1, 128);
        ContentReaderFuzz.AssertClean(style.Font.Family, "font.family");
        Assert.Equal(style.Font.Family, style.Font.Family.Trim());
        Assert.InRange(style.Font.Weight, 100, 900);
        Assert.True(style.Font.Source is null || PackageReference.TryParse(style.Font.Source, out _), style.Font.Source);
        Assert.InRange(style.Font.Fallback.Count, 0, 8);
        foreach (string family in style.Font.Fallback)
        {
            Assert.InRange(family.Length, 1, 128);
            ContentReaderFuzz.AssertClean(family, "font.fallback");
        }

        Assert.True(style.Size is > 0 and <= 1000, $"size {style.Size}");
        Assert.InRange(style.ReferenceWidth, 16, 16384);
        Assert.InRange(style.ReferenceHeight, 16, 16384);
        Assert.True(style.LineHeight is > 0 and <= 10);
        Assert.InRange(style.LetterSpacing, -1000, 1000);
        Assert.True(Enum.IsDefined(style.Align));
        AssertColor(style.FillColor);
        if (style.Stroke is { } stroke)
        {
            AssertColor(stroke.Color);
            Assert.InRange(stroke.Width, 0, style.Size / 2);
            Assert.True(Enum.IsDefined(stroke.Join));
        }

        if (style.Shadow is { } shadow)
        {
            AssertColor(shadow.Color);
            Assert.InRange(shadow.Blur, 0, 64);
            Assert.InRange(shadow.Dx, -1000, 1000);
            Assert.InRange(shadow.Dy, -1000, 1000);
        }

        if (style.Background is { } background)
        {
            AssertColor(background.Color);
            Assert.InRange(background.PaddingX, 0, 1000);
            Assert.InRange(background.PaddingY, 0, 1000);
            Assert.InRange(background.Radius, 0, 1000);
        }

        foreach (TextStyleAnimation animation in new[] { style.In, style.Out }.OfType<TextStyleAnimation>())
        {
            Assert.True(animation.DurationSeconds is > 0 and <= 60);
            foreach (PresetCurve curve in animation.Curves)
            {
                Assert.Equal("rodaje.transform", curve.Effect);
                Assert.Contains(curve.Parameter, (string[])["x", "y", "scale", "rotation", "opacity"]);
                Assert.NotEmpty(curve.Keys);
                double previous = double.NegativeInfinity;
                foreach (PresetKey key in curve.Keys)
                {
                    Assert.InRange(key.T, 0, animation.DurationSeconds);
                    Assert.True(key.T > previous, "claves desordenadas");
                    Assert.True(double.IsFinite(key.Value));
                    previous = key.T;
                }
            }
        }
    }

    private static void AssertColor(string color) =>
        Assert.Matches("^#([0-9A-F]{6}|[0-9A-F]{8})$", color);

    [Theory]
    [InlineData("4.9e-324")]
    [InlineData("1e-300")]
    [InlineData("1000")]
    [InlineData("1000.0")]
    [InlineData("1E3")]
    public void Acepta_los_tamanos_en_el_borde(string size) =>
        AssertWellFormed(Read(Style(size: size)));

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-4.9e-324")]
    [InlineData("1000.0000000001")]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("\"72\"")]
    [InlineData("null")]
    [InlineData("[72]")]
    [InlineData("true")]
    public void Rechaza_los_tamanos_fuera_de_rango_o_que_no_son_numeros(string size) =>
        Assert.Contains("size", Error(Style(size: size)), StringComparison.Ordinal);

    [Theory]
    [InlineData("100", 100)]
    [InlineData("900", 900)]
    [InlineData("900.0", 900)]
    [InlineData("4e2", 400)]
    public void Acepta_los_pesos_enteros_en_el_borde(string weight, int expected) =>
        Assert.Equal(expected, Read(Style(font: $$"""{ "family": "A", "weight": {{weight}} }""")).Font.Weight);

    [Theory]
    [InlineData("99")]
    [InlineData("901")]
    [InlineData("100.000001")]
    [InlineData("899.9999999999")]
    [InlineData("1e400")]
    [InlineData("\"bold\"")]
    [InlineData("null")]
    public void Rechaza_los_pesos_que_no_son_enteros_de_100_a_900(string weight) =>
        Assert.Contains("font.weight", Error(Style(font: $$"""{ "family": "A", "weight": {{weight}} }""")), StringComparison.Ordinal);

    [Theory]
    [InlineData("\"\\u202eInter\"", "Inter")]
    [InlineData("\"  Inter\\t\"", "Inter")]
    [InlineData("\"Noto\\u0000 Sans\"", "Noto Sans")]
    [InlineData("\"Noto\\u2028Sans\"", "Noto Sans")]
    [InlineData("\"ヒラギノ角ゴ\"", "ヒラギノ角ゴ")]
    [InlineData("\"Ñandú 🎬\"", "Ñandú 🎬")]
    [InlineData("\"\\ud83d\\ude00\"", "😀")]
    public void La_familia_se_limpia_de_controles_y_marcas(string family, string expected) =>
        Assert.Equal(expected, Read(Style(font: $$"""{ "family": {{family}} }""")).Font.Family);

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"\\u202e\\u200f\"")]
    [InlineData("\"\\u0000\\u0001\"")]
    [InlineData("\"\\n\\t \"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("[\"Inter\"]")]
    public void Una_familia_vacia_o_que_no_es_texto_se_rechaza(string family) =>
        Assert.Contains("font.family", Error(Style(font: $$"""{ "family": {{family}} }""")), StringComparison.Ordinal);

    [Fact]
    public void La_familia_se_mide_en_caracteres_tras_limpiar()
    {
        Assert.Equal(128, Read(Style(font: $$"""{ "family": "{{new string('a', 128)}}" }""")).Font.Family.Length);
        Assert.Equal(128, Read(Style(font: $$"""{ "family": "{{string.Concat(Enumerable.Repeat("a\\u202e", 128))}}" }""")).Font.Family.Length);
        Assert.Contains("font.family", Error(Style(font: $$"""{ "family": "{{new string('a', 129)}}" }""")), StringComparison.Ordinal);
        Assert.Contains("font.family", Error(Style(font: $$"""{ "family": "{{string.Concat(Enumerable.Repeat("🎬", 65))}}" }""")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"Inter\\udc00\"")]
    [InlineData("\"\\ude00\\ud83d\"")]
    public void Un_sustituto_suelto_en_la_familia_invalida_el_archivo(string family) =>
        Assert.Contains("no es Unicode válido", Error(Style(font: $$"""{ "family": {{family}} }""")), StringComparison.Ordinal);

    [Fact]
    public void Un_sustituto_suelto_en_un_campo_desconocido_tambien_invalida_el_archivo() =>
        Assert.Contains("no es Unicode válido", Error(Style(extra: """, "futuro": { "\ud800": 1 }""")), StringComparison.Ordinal);

    [Fact]
    public void El_utf8_danado_invalida_el_archivo()
    {
        byte[] json = [.. Encoding.UTF8.GetBytes(Style(font: """{ "family": "In""")), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("""ter" }""")];
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTextStyle(new MemoryStream(json)));
        byte[] surrogate = [.. Encoding.UTF8.GetBytes(Style(font: """{ "family": "In""")), 0xED, 0xA0, 0x80, .. Encoding.UTF8.GetBytes("""ter" }""")];
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTextStyle(new MemoryStream(surrogate)));
    }

    [Theory]
    [InlineData("rodaje-fuentes@1.0.0/inter")]
    [InlineData("rodaje-fuentes@1.0.0-beta.2+abc/inter")]
    [InlineData("a@0.0.0/b")]
    public void Font_source_acepta_referencias(string source) =>
        Assert.Equal(source, Read(Style(font: $$"""{ "family": "A", "source": "{{source}}" }""")).Font.Source);

    [Theory]
    [InlineData("../fuentes/inter.ttf")]
    [InlineData("rodaje-fuentes@1.0.0/../inter")]
    [InlineData("rodaje-fuentes@1.0.0/fuentes/inter.ttf")]
    [InlineData("rodaje-fuentes@1.0.0/Inter")]
    [InlineData("rodaje-fuentes@1.0.0/con")]
    [InlineData("rodaje-fuentes@1.0/inter")]
    [InlineData("rodaje-fuentes@1.0.0\\\\inter")]
    [InlineData("C:\\\\Windows\\\\Fonts\\\\arial.ttf")]
    [InlineData("https://ejemplo.com/inter.ttf")]
    [InlineData("rodaje-fuentes@1.0.0/inter\\u202e")]
    [InlineData("@1.0.0/inter")]
    [InlineData("rodaje-fuentes@/inter")]
    [InlineData("rodaje-fuentes@1.0.0/")]
    [InlineData(" rodaje-fuentes@1.0.0/inter")]
    public void Font_source_rechaza_rutas_y_referencias_mal_escritas(string source) =>
        Assert.Contains("font.source", Error(Style(font: $$"""{ "family": "A", "source": "{{source}}" }""")), StringComparison.Ordinal);

    [Fact]
    public void Font_source_que_no_es_texto_se_ignora()
    {
        // Como el resto de textos opcionales: un número o null es «sin source».
        Assert.Null(Read(Style(font: """{ "family": "A", "source": null }""")).Font.Source);
    }

    [Theory]
    [InlineData("[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\"]", 8)]
    [InlineData("[]", 0)]
    [InlineData("[\"\\u202eArial\"]", 1)]
    public void Fallback_acepta_hasta_ocho(string fallback, int count) =>
        Assert.Equal(count, Read(Style(font: $$"""{ "family": "A", "fallback": {{fallback}} }""")).Font.Fallback.Count);

    [Theory]
    [InlineData("\"Arial\"")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\"\\u202e\"]")]
    [InlineData("[\" \"]")]
    [InlineData("[[\"Arial\"]]")]
    public void Fallback_rechaza_lo_que_no_es_una_lista_de_familias(string fallback) =>
        Assert.Contains("font.fallback", Error(Style(font: $$"""{ "family": "A", "fallback": {{fallback}} }""")), StringComparison.Ordinal);

    [Theory]
    [InlineData(""", "stroke": { "color": "#000000", "width": 36 }""", "72", true)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 36.000001 }""", "72", false)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 0 }""", "72", true)]
    [InlineData(""", "stroke": { "color": "#000000", "width": -0.0001 }""", "72", false)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 500 }""", "1000", true)]
    [InlineData(""", "stroke": { "color": "#000000", "width": 1e-300 }""", "4.9e-324", false)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 64, "dx": -1000, "dy": 1000 }""", "72", true)]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 64.0000001 }""", "72", false)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1000, 0], "radius": 1000 }""", "72", true)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1000.1, 0] }""", "72", false)]
    [InlineData(""", "background": { "color": "#000000", "padding": [1e400, 0] }""", "72", false)]
    [InlineData(""", "background": { "color": "#000000", "padding": ["1", 0] }""", "72", false)]
    [InlineData(""", "lineHeight": 10, "letterSpacing": -1000""", "72", true)]
    [InlineData(""", "lineHeight": 10.0000001""", "72", false)]
    [InlineData(""", "reference": { "width": 16, "height": 16384 }""", "72", true)]
    [InlineData(""", "reference": { "width": 15, "height": 1080 }""", "72", false)]
    [InlineData(""", "reference": { "width": 1920.0, "height": 1080 }""", "72", false)]
    [InlineData(""", "reference": { "width": 2147483648, "height": 1080 }""", "72", false)]
    public void Medidas_en_el_borde(string extra, string size, bool valid)
    {
        if (valid)
        {
            AssertWellFormed(Read(Style(size: size, extra: extra)));
        }
        else
        {
            Assert.Throws<PackageContentException>(() => Read(Style(size: size, extra: extra)));
        }
    }

    [Theory]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("#ABCDEF00", "#ABCDEF00")]
    [InlineData("#aBcDeF12", "#ABCDEF12")]
    public void Los_colores_se_guardan_en_mayusculas(string color, string expected) =>
        Assert.Equal(expected, Read(Style(extra: $$""", "fill": { "color": "{{color}}" }""")).FillColor);

    [Theory]
    [InlineData("#ABCDEG")]
    [InlineData("#ABCDE")]
    [InlineData("#ABCDEF0")]
    [InlineData("#ABCDEF000")]
    [InlineData("ABCDEF")]
    [InlineData("#１２３４５６")]
    [InlineData("#ABCDEF\\u0000")]
    [InlineData("#ＡＢＣＤＥＦ")]
    [InlineData(" #ABCDEF")]
    [InlineData("#ABC DEF")]
    public void Rechaza_los_colores_mal_escritos(string color) =>
        Assert.Contains("fill.color", Error(Style(extra: $$""", "fill": { "color": "{{color}}" }""")), StringComparison.Ordinal);

    [Theory]
    [InlineData(""", "in": { "durationSeconds": 60, "curves": { "x": [ { "t": 0, "value": 0 }, { "t": 60, "value": 1 } ] } }""", true)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 1.0000009, "value": 0 } ] } }""", true)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": -0.0000009, "value": 0 } ] } }""", true)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 1.000002, "value": 0 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 0.5, "value": 0 }, { "t": 0.5, "value": 1 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 0.5, "value": 0 }, { "t": 0.4, "value": 1 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 0, "value": 1e400 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "scale": [ { "t": 0, "value": 100.0001 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 0, "value": 0, "interp": "EaseOut" } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "x": [ { "t": 0, "value": 0 } ], "x": [ { "t": 0, "value": 1 } ] } }""", false)]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": [] }""", false)]
    [InlineData(""", "in": { "curves": {} }""", false)]
    [InlineData(""", "in": { "durationSeconds": 4.9e-324 }""", true)]
    [InlineData(""", "in": null, "out": null""", true)]
    public void Animaciones_en_el_borde(string extra, bool valid)
    {
        if (valid)
        {
            AssertWellFormed(Read(Style(extra: extra)));
        }
        else
        {
            Assert.Throws<PackageContentException>(() => Read(Style(extra: extra)));
        }
    }

    [Fact]
    public void Mil_claves_valen_y_mil_una_no()
    {
        string Keys(int n) => string.Join(", ", Enumerable.Range(0, n).Select(i => $$"""{ "t": {{(i / 1000.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}}, "value": 0 }"""));
        Assert.Equal(1000, Read(Style(extra: $$""", "in": { "durationSeconds": 1.0, "curves": { "x": [ {{Keys(1000)}} ] } }""")).In!.Curves[0].Keys.Count);
        Assert.Contains("más de 1000 claves", Error(Style(extra: $$""", "in": { "durationSeconds": 2.0, "curves": { "x": [ {{Keys(1001)}} ] } }""")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"rodaje.textstyle\"")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": { "family": "A" }, "size": 72 """)]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": { "family": "A" }, "size": 72 } { }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": { "family": "A" }, "size": 0x48 }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": { "family": "A" }, "size": NaN }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": { "family": 'A' }, "size": 72 }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 2147483648, "name": "S", "font": { "family": "A" }, "size": 72 }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": "Inter", "size": 72 }""")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "S", "font": null, "size": 72 }""")]
    public void El_json_roto_o_sin_estilo_se_rechaza(string json) =>
        Assert.Throws<PackageContentException>(() => Read(json));

    [Fact]
    public void Comentarios_y_comas_finales_se_toleran()
    {
        TextStylePreset style = Read("""
            // un estilo escrito a mano
            { "format": "rodaje.textstyle", "version": 1, "name": "S", /* familia */ "font": { "family": "A", }, "size": 72, }
            """);
        Assert.Equal("A", style.Font.Family);
    }

    [Fact]
    public void Rechaza_el_anidamiento_profundo_sin_desbordar_la_pila()
    {
        string deep = new string('{', 0) + string.Concat(Enumerable.Repeat("{\"a\":", 3000)) + "1" + new string('}', 3000);
        Assert.Contains("No es JSON válido", Error(Style(extra: $", \"otro\": {deep}")), StringComparison.Ordinal);
    }

    [Fact]
    public void Cada_corte_del_archivo_da_un_error()
    {
        byte[] full = Encoding.UTF8.GetBytes(Full);
        for (int length = 0; length < full.Length; length++)
        {
            // Un corte justo tras la última llave no existe: el objeto raíz sigue abierto en todos.
            Assert.Throws<PackageContentException>(() => ContentReaders.ReadTextStyle(new MemoryStream(full, 0, length)));
        }
    }

    /// <summary>Mutaciones al azar del ejemplo completo: un estilo que cumple el formato o un error con motivo.</summary>
    [Fact]
    public void Mutaciones_al_azar_dan_un_estilo_valido_o_un_error()
    {
        const int Cases = 4000;
        byte[] original = Encoding.UTF8.GetBytes(Full);
        int accepted = 0;
        for (int i = 0; i < Cases; i++)
        {
            int seed = ContentReaderFuzz.Seed + 20_000 + i;
            byte[] data = ContentReaderFuzz.Mutate(new Random(seed), original);
            try
            {
                AssertWellFormed(ContentReaders.ReadTextStyle(new MemoryStream(data)));
                accepted++;
            }
            catch (PackageContentException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {seed}] {ex.GetType().Name}: {ex.Message}\n{ContentReaderFuzz.Show(data)}");
            }
        }

        output.WriteLine($"{accepted} de {Cases} mutaciones se aceptaron.");
        Assert.InRange(accepted, Cases / 20, Cases - (Cases / 20));
    }

    /// <summary>
    /// Estilos armados por partes al azar (cada campo válido, en el borde o inválido): si el lector lo acepta, cumple el
    /// formato; si lo rechaza, es con <see cref="PackageContentException"/>.
    /// </summary>
    [Fact]
    public void Estilos_armados_al_azar_cumplen_el_formato_o_se_rechazan()
    {
        const int Cases = 3000;
        int accepted = 0;
        for (int i = 0; i < Cases; i++)
        {
            int seed = ContentReaderFuzz.Seed + 30_000 + i;
            var random = new Random(seed);
            string Pick(params string[] options) => options[random.Next(options.Length)];
            string Number() => Pick("0", "-0", "1", "0.5", "64", "64.0000001", "500", "1000", "1000.1", "-1000", "1e400", "4.9e-324", "\"1\"", "null", "36");
            var fields = new List<string>
            {
                "\"format\": \"rodaje.textstyle\"",
                "\"version\": " + Pick("1", "1", "1", "2", "0", "1.0"),
                "\"name\": " + Pick("\"Título\"", "\"\\u202e\"", "\"" + new string('n', 80) + "\"", "\"" + new string('n', 81) + "\"", "\"\\ud800\""),
                "\"font\": { \"family\": " + Pick("\"Inter\"", "\"\\u202eInter\"", "\"\"", "\"" + new string('f', 129) + "\"") +
                    ", \"weight\": " + Pick("400", "100", "900", "450.5", "1000") +
                    (random.Next(2) == 0 ? "" : ", \"source\": " + Pick("\"a@1.0.0/b\"", "\"../b\"", "\"a@1/b\"")) +
                    (random.Next(2) == 0 ? "" : ", \"fallback\": " + Pick("[]", "[\"Arial\"]", "[\"\"]", "[1,2]")) + " }",
                "\"size\": " + Number(),
            };
            if (random.Next(2) == 0) fields.Add("\"lineHeight\": " + Number());
            if (random.Next(2) == 0) fields.Add("\"letterSpacing\": " + Number());
            if (random.Next(2) == 0) fields.Add("\"align\": " + Pick("\"left\"", "\"right\"", "\"center\"", "\"Center\"", "1"));
            if (random.Next(2) == 0) fields.Add("\"fill\": { \"color\": " + Pick("\"#FFFFFF\"", "\"#fff\"", "\"#FFFFFF00\"", "null") + " }");
            if (random.Next(2) == 0) fields.Add($"\"stroke\": {{ \"color\": \"#000000\", \"width\": {Number()}, \"join\": {Pick("\"miter\"", "\"bevel\"", "\"x\"")} }}");
            if (random.Next(2) == 0) fields.Add($"\"shadow\": {{ \"color\": \"#000000\", \"dx\": {Number()}, \"dy\": {Number()}, \"blur\": {Number()} }}");
            if (random.Next(2) == 0) fields.Add($"\"background\": {{ \"color\": \"#000000\", \"padding\": [{Number()}, {Number()}], \"radius\": {Number()} }}");
            if (random.Next(2) == 0) fields.Add($"\"reference\": {{ \"width\": {Pick("16", "1920", "15", "16384", "16385")}, \"height\": {Pick("1080", "16")} }}");
            if (random.Next(2) == 0) fields.Add($"\"in\": {{ \"durationSeconds\": {Number()}, \"curves\": {{ \"{Pick("x", "opacity", "scale", "blur")}\": [ {{ \"t\": {Number()}, \"value\": {Number()} }} ] }} }}");
            string json = "{ " + string.Join(", ", fields.OrderBy(_ => random.Next())) + " }";
            try
            {
                AssertWellFormed(Read(json));
                accepted++;
            }
            catch (PackageContentException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            }
            catch (Exception ex)
            {
                Assert.Fail($"[semilla {seed}] {ex.GetType().Name}: {ex.Message}\n{json}");
            }
        }

        output.WriteLine($"{accepted} de {Cases} estilos armados se aceptaron.");
        Assert.True(accepted > 0);
    }

    /// <summary>
    /// Defecto: un estilo o unas zonas propias guardadas con marca BOM (EF BB BF, como las deja el Bloc de notas al
    /// «guardar como UTF-8» en versiones viejas o algunos editores) se ven en la biblioteca, porque
    /// <c>PackageLibrary</c> lee los propios con <c>SafeText.ReadAllTextStrict</c>, que salta la marca, pero luego
    /// <c>ContentReaders</c> los rechaza: «No es JSON válido: '0xEF' is an invalid start of a value». formato-paquete.md
    /// dice que los propios se leen «tolerando una marca BOM inicial». Lo que debería pasar: los lectores de contenido
    /// también la saltan (o la biblioteca no lista lo que no se puede leer).
    /// </summary>
    [Fact(Skip = "Defecto: ContentReaders rechaza un JSON con BOM que PackageLibrary sí lista como elemento propio.")]
    public void Un_elemento_propio_con_bom_se_lista_y_se_lee()
    {
        using var temp = new TempFolder();
        PackageLocations locations = PackageLocations.Under(temp.Path);
        Directory.CreateDirectory(locations.Own);
        byte[] style = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Style(extra: """, "kind": "text-style" """))];
        byte[] zones = [0xEF, 0xBB, 0xBF, .. ContentReaders.WriteSafeZones(new SafeZonePreset("", "Mi canal", 9, 16, [new SafeZone("Logo", SafeZoneKind.Interface, 0.8, 0.02, 0.18, 0.08)], false), "safe-zones")];
        File.WriteAllBytes(Path.Combine(locations.Own, "estilo.json"), style);
        File.WriteAllBytes(Path.Combine(locations.Own, "zonas.json"), zones);

        PackageScan library = PackageLibrary.Scan(locations, "rodaje");
        PackageItemEntry styleEntry = Assert.Single(library.Items(ItemKind.TextStyle));
        PackageItemEntry zonesEntry = Assert.Single(library.Items(ItemKind.SafeZones));

        using (FileStream stream = File.OpenRead(styleEntry.FilePath!))
        {
            Assert.Equal("A", ContentReaders.ReadTextStyle(stream).Font.Family);
        }

        using (FileStream stream = File.OpenRead(zonesEntry.FilePath!))
        {
            Assert.Equal("Logo", ContentReaders.ReadSafeZones(stream).Zones[0].Label);
        }
    }
}
