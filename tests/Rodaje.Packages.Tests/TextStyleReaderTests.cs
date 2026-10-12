using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary><c>rodaje.textstyle</c> v1: lectura completa, valores por defecto, rechazos y una mutación aleatoria acotada.</summary>
public sealed class TextStyleReaderTests
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
          "out": { "durationSeconds": 0.3, "curves": { "opacity": [ { "t": 0, "value": 1 }, { "t": 0.3, "value": 0 } ] } },
          "desconocido": { "se": "ignora" } }
        """;

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Minimal(string extra = "") =>
        $$"""{ "format": "rodaje.textstyle", "version": 1, "name": "Simple", "font": { "family": "Segoe UI" }, "size": 72{{extra}} }""";

    private static string Error(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTextStyle(Utf8(json))).Message;

    [Fact]
    public void Lee_el_ejemplo_de_la_propuesta()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(Full));
        Assert.Equal(("clasico", "Clásico"), (style.Id, style.Name));
        Assert.Equal(new TextStyleFont("Inter", 700, false, "rodaje-fuentes@1.0.0/inter", style.Font.Fallback), style.Font);
        Assert.Equal(["Segoe UI", "Arial"], style.Font.Fallback);
        Assert.Equal((96.0, 1920, 1080, 1.1, 0.0, TextStyleAlign.Center), (style.Size, style.ReferenceWidth, style.ReferenceHeight, style.LineHeight, style.LetterSpacing, style.Align));
        Assert.Equal("#FFFFFF", style.FillColor);
        Assert.Equal(new TextStyleStroke("#000000", 6, TextStyleJoin.Round), style.Stroke);
        Assert.Equal(new TextStyleShadow("#00000099", 4, 6, 8), style.Shadow);
        Assert.Equal(new TextStyleBackground("#00000080", 24, 12, 12), style.Background);
        Assert.Equal(0.4, style.In!.DurationSeconds);
        Assert.Equal(["opacity", "y"], style.In.Curves.Select(c => c.Parameter));
        Assert.All(style.In.Curves, c => Assert.Equal("rodaje.transform", c.Effect));
        Assert.Equal(PresetInterpolation.EaseOut, style.In.Curves[0].Keys[1].Interpolation);
        Assert.Equal(0.3, style.Out!.DurationSeconds);
    }

    [Fact]
    public void Lee_el_color_de_la_palabra_dicha()
    {
        Assert.Null(ContentReaders.ReadTextStyle(Utf8(Minimal())).HighlightColor);
        Assert.Equal("#FFD400", ContentReaders.ReadTextStyle(Utf8(Minimal(""", "highlight": { "color": "#ffd400" }"""))).HighlightColor);
        Assert.Contains("highlight.color", Error(Minimal(""", "highlight": { }""")));
        Assert.Contains("highlight.color", Error(Minimal(""", "highlight": { "color": "amarillo" }""")));
    }

    [Fact]
    public void Lo_opcional_toma_valores_por_defecto()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(Minimal()));
        Assert.Equal("", style.Id);
        Assert.Equal(new TextStyleFont("Segoe UI", 400, false, null, style.Font.Fallback), style.Font);
        Assert.Empty(style.Font.Fallback);
        Assert.Equal((1920, 1080, 1.2, TextStyleAlign.Center, "#FFFFFF"), (style.ReferenceWidth, style.ReferenceHeight, style.LineHeight, style.Align, style.FillColor));
        Assert.Null(style.Stroke);
        Assert.Null(style.Shadow);
        Assert.Null(style.Background);
        Assert.Null(style.In);
        Assert.Null(style.Out);
    }

    [Theory]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x" }""", "se esperaba «rodaje.textstyle»")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 2, "name": "x" }""", "actualiza el programa")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "x", "size": 10 }""", "Falta font")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "x", "font": { "family": " " }, "size": 10 }""", "font.family")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "x", "font": { "family": "A" } }""", "Falta size")]
    [InlineData("""{ "format": "rodaje.textstyle", "version": 1, "name": "x", "font": [], "size": 10 }""", "font tiene que ser un objeto")]
    public void Rechaza_lo_que_no_es_un_estilo(string json, string expected) =>
        Assert.Contains(expected, Error(json), StringComparison.Ordinal);

    [Theory]
    [InlineData(""", "size": 0""", "size")]
    [InlineData(""", "size": 1001""", "size")]
    [InlineData(""", "font": { "family": "A", "weight": 950 }""", "font.weight")]
    [InlineData(""", "font": { "family": "A", "weight": 450.5 }""", "font.weight")]
    [InlineData(""", "font": { "family": "A", "italic": 1 }""", "font.italic")]
    [InlineData(""", "font": { "family": "A", "source": "no es referencia" }""", "font.source")]
    [InlineData(""", "font": { "family": "A", "fallback": ["1","2","3","4","5","6","7","8","9"] }""", "font.fallback")]
    [InlineData(""", "font": { "family": "A", "fallback": [""] }""", "font.fallback")]
    [InlineData(""", "align": "justify" """, "align")]
    [InlineData(""", "lineHeight": 0""", "lineHeight")]
    [InlineData(""", "letterSpacing": 5000""", "letterSpacing")]
    [InlineData(""", "fill": { "color": "blanco" }""", "fill.color")]
    [InlineData(""", "fill": { "color": "#FFF" }""", "fill.color")]
    [InlineData(""", "fill": { }""", "Falta fill.color")]
    [InlineData(""", "stroke": { "color": "#000000", "width": 40 }""", "stroke.width")]
    [InlineData(""", "stroke": { "color": "#000000", "width": 2, "join": "sharp" }""", "stroke.join")]
    [InlineData(""", "shadow": { "color": "#000000", "blur": 65 }""", "shadow.blur")]
    [InlineData(""", "shadow": { "color": "#000000", "dx": 1e9 }""", "shadow.dx")]
    [InlineData(""", "background": { "color": "#000000", "padding": [1] }""", "background.padding")]
    [InlineData(""", "background": { "color": "#000000", "padding": [1, -2] }""", "background.padding")]
    [InlineData(""", "in": { "durationSeconds": 0 }""", "in.durationSeconds")]
    [InlineData(""", "in": { "durationSeconds": 61 }""", "in.durationSeconds")]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "blur": [ { "t": 0, "value": 1 } ] } }""", "no es un parámetro de la transformación")]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "opacity": [ { "t": 0, "value": 2 } ] } }""", "fuera de 0 a 1")]
    [InlineData(""", "out": { "durationSeconds": 1, "curves": { "x": [ { "t": 2, "value": 0 } ] } }""", "fuera de 0 a durationSeconds")]
    [InlineData(""", "out": "no" """, "out tiene que ser un objeto")]
    public void Valida_los_rangos(string extra, string expected) =>
        Assert.Contains(expected, Error(Minimal(extra)), StringComparison.Ordinal);

    [Fact]
    public void Mas_de_un_mega_se_rechaza_antes_de_leer()
    {
        string big = Minimal($$""", "pad": "{{new string('x', 1 << 20)}}" """);
        Assert.Contains("1 MB", Error(big), StringComparison.Ordinal);
    }

    [Fact]
    public void Mutaciones_al_azar_dan_un_estilo_o_un_error_en_espanol()
    {
        byte[] original = Encoding.UTF8.GetBytes(Full);
        var random = new Random(5_000);
        for (int i = 0; i < 2000; i++)
        {
            byte[] data = (byte[])original.Clone();
            for (int k = random.Next(1, 4); k > 0; k--)
            {
                data[random.Next(data.Length)] = (byte)random.Next(32, 127);
            }

            try
            {
                TextStylePreset style = ContentReaders.ReadTextStyle(new MemoryStream(data));
                Assert.InRange(style.Size, double.Epsilon, 1000);
                Assert.InRange(style.Font.Weight, 100, 900);
            }
            catch (PackageContentException ex)
            {
                Assert.False(string.IsNullOrWhiteSpace(ex.Message));
            }
        }
    }
}
