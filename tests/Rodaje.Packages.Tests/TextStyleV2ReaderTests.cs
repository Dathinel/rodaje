using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

/// <summary><c>rodaje.textstyle</c> v2: degradado, bordes y sombras adicionales, tipografía, arco, barrido y animación por unidades.</summary>
public sealed class TextStyleV2ReaderTests
{
    private const string Full = """
        { "format": "rodaje.textstyle", "version": 2, "name": "Neón",
          "font": { "family": "Inter", "weight": 800 }, "size": 120,
          "fill": { "color": "#FFFFFF", "gradient": { "kind": "linear", "angle": 90, "stops": [ { "position": 0, "color": "#ffd400" }, { "position": 1, "color": "#FF3D00" } ] } },
          "stroke": { "color": "#000000", "width": 4 },
          "extraStrokes": [ { "color": "#FFFFFF", "width": 12, "join": "miter" } ],
          "shadow": { "color": "#00000099", "dy": 6, "blur": 10 },
          "extraShadows": [ { "color": "#FF00FF80", "blur": 30 } ],
          "typography": { "kerning": false, "case": "smallCaps", "features": ["liga", "-calt", "ss01"], "fit": "shrink" },
          "path": { "kind": "arc", "bend": -120 },
          "highlight": { "color": "#FFD400", "mode": "sweep" },
          "in": { "durationSeconds": 1, "unit": "letter", "stagger": 0.6, "order": "random",
                  "curves": { "blur": [ { "t": 0, "value": 24, "interp": "easeOut" }, { "t": 1, "value": 0 } ],
                              "y": [ { "t": 0, "value": -80, "interp": "bounce" }, { "t": 1, "value": 0 } ] } } }
        """;

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Minimal(string extra = "") =>
        $$"""{ "format": "rodaje.textstyle", "version": 2, "name": "Simple", "font": { "family": "Segoe UI" }, "size": 72{{extra}} }""";

    private static string Error(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTextStyle(Utf8(json))).Message;

    [Fact]
    public void Lee_todo_lo_nuevo()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(Full));
        Assert.Equal("linear", style.Gradient!.Kind);
        Assert.Equal(90, style.Gradient.Angle);
        Assert.Equal([new TextStyleGradientStop(0, "#FFD400"), new TextStyleGradientStop(1, "#FF3D00")], style.Gradient.Stops);
        Assert.Equal([new TextStyleStroke("#FFFFFF", 12, TextStyleJoin.Miter)], style.ExtraStrokes);
        Assert.Equal([new TextStyleShadow("#FF00FF80", 0, 0, 30)], style.ExtraShadows);
        Assert.Equal((false, "smallCaps", "shrink"), (style.Typography!.Kerning, style.Typography.Case, style.Typography.Fit));
        Assert.Equal(["liga", "-calt", "ss01"], style.Typography.Features);
        Assert.Equal(-120, style.PathBend);
        Assert.Equal("sweep", style.HighlightMode);
        Assert.Equal(("letter", 0.6, "random"), (style.In!.Unit, style.In.Stagger, style.In.Order));
        Assert.Equal(["blur", "y"], style.In.Curves.Select(c => c.Parameter));
        Assert.Equal(PresetInterpolation.Bounce, style.In.Curves[1].Keys[0].Interpolation);
    }

    [Fact]
    public void Sin_lo_nuevo_queda_como_en_la_version_1()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(Minimal(""", "in": { "durationSeconds": 1 }""")));
        Assert.Null(style.Gradient);
        Assert.Empty(style.ExtraStrokes);
        Assert.Empty(style.ExtraShadows);
        Assert.Null(style.Typography);
        Assert.Null(style.PathBend);
        Assert.Equal("word", style.HighlightMode);
        Assert.Equal(("block", 0.0, "forward"), (style.In!.Unit, style.In.Stagger, style.In.Order));
    }

    [Theory]
    [InlineData(""", "fill": { "color": "#FFFFFF", "gradient": { "stops": [ { "position": 0, "color": "#000000" } ] } }""", "al menos dos")]
    [InlineData(""", "fill": { "color": "#FFFFFF", "gradient": { "stops": [ { "position": 0.5, "color": "#000000" }, { "position": 0.2, "color": "#000000" } ] } }""", "mayor o igual")]
    [InlineData(""", "fill": { "color": "#FFFFFF", "gradient": { "kind": "conico", "stops": [] } }""", "fill.gradient.kind")]
    [InlineData(""", "extraStrokes": [ {"color":"#000000","width":1}, {"color":"#000000","width":1}, {"color":"#000000","width":1}, {"color":"#000000","width":1}, {"color":"#000000","width":1} ]""", "extraStrokes tiene que ser una lista de hasta 4")]
    [InlineData(""", "extraStrokes": [ { "color": "#000000", "width": 50 } ]""", "extraStrokes[0].width")]
    [InlineData(""", "extraShadows": [ "sombra" ]""", "extraShadows[0] tiene que ser un objeto")]
    [InlineData(""", "typography": { "features": ["ligadura"] }""", "features[0]")]
    [InlineData(""", "typography": { "features": ["--lig"] }""", "features[0] tiene que ser una etiqueta OpenType de 4 caracteres [a-z0-9 ] (con «-» delante para apagarla): solo admite")]
    [InlineData(""", "typography": { "features": ["kern", "-lig"] }""", "features[1] tiene que ser una etiqueta OpenType de 4 caracteres [a-z0-9 ] (con «-» delante para apagarla): tiene 3")]
    [InlineData(""", "typography": { "features": ["LIGA"] }""", "features[0] tiene que ser una etiqueta OpenType de 4 caracteres [a-z0-9 ] (con «-» delante para apagarla): solo admite")]
    [InlineData(""", "typography": { "features": ["li_a"] }""", "solo admite minúsculas")]
    [InlineData(""", "typography": { "features": [" cv1"] }""", "los espacios solo van al final")]
    [InlineData(""", "typography": { "features": [7] }""", "features[0] tiene que ser un texto")]
    [InlineData(""", "typography": { "case": "titulo" }""", "typography.case")]
    [InlineData(""", "typography": { "kerning": 1 }""", "typography.kerning")]
    [InlineData(""", "path": { "kind": "circulo", "bend": 10 }""", "path.kind")]
    [InlineData(""", "path": { "bend": 400 }""", "path.bend")]
    [InlineData(""", "highlight": { "color": "#FFD400", "mode": "brillo" }""", "highlight.mode")]
    [InlineData(""", "in": { "durationSeconds": 1, "unit": "silaba" }""", "in.unit")]
    [InlineData(""", "in": { "durationSeconds": 1, "stagger": 2 }""", "in.stagger")]
    [InlineData(""", "in": { "durationSeconds": 1, "order": "alfabetico" }""", "in.order")]
    [InlineData(""", "in": { "durationSeconds": 1, "curves": { "blur": [ { "t": 0, "value": 100 } ] } }""", "blur")]
    public void Valida_lo_nuevo(string extra, string expected) =>
        Assert.Contains(expected, Error(Minimal(extra)), StringComparison.Ordinal);

    // #298: el documento pide etiquetas OpenType de 4 caracteres [a-z0-9 ], con «-» delante para apagarlas.
    [Fact]
    public void Acepta_etiquetas_validas_con_relleno_y_apagadas()
    {
        TextStylePreset style = ContentReaders.ReadTextStyle(Utf8(Minimal(""", "typography": { "features": ["cv1 ", "-ss01", "kern"] }""")));
        Assert.Equal(["cv1 ", "-ss01", "kern"], style.Typography!.Features);
    }
}
