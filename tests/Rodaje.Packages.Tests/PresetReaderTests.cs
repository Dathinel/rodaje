using System.Text;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

public sealed class PresetReaderTests
{
    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string FilterError(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadFilter(Utf8(json))).Message;

    private static string EffectError(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadEffect(Utf8(json))).Message;

    private static string TransitionError(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTransition(Utf8(json))).Message;

    private const string Filter = """
        { "format": "rodaje.filter", "version": 1, "id": "calido", "name": "Cálido",
          "effects": [ { "type": "rodaje.color", "parameters": { "temperature": 0.3, "saturation": 0.1 }, "options": { "lut": "./calido-lut" } },
                       { "type": "rodaje.vignette" } ] }
        """;

    private static string Effect(string anchor = "\"start\"", string curves = """[ { "effect": "rodaje.transform", "parameter": "scale", "keys": [ { "t": 0, "value": 1.3, "interp": "easeOut" }, { "t": 0.5, "value": 1 } ] } ]""", string effects = "[]", string extra = "") =>
        $$"""{ "format": "rodaje.effect", "version": 1, "name": "Entrada con zoom", "anchor": {{anchor}}, "durationSeconds": 0.5, "effects": {{effects}}, "curves": {{curves}}{{extra}} }""";

    private static string Transition(string body = "", string version = "2") =>
        $$"""{ "format": "rodaje.preset", "version": {{version}}, "id": "deslizar", "name": "Deslizar", "kind": "transition", "durationSeconds": 0.5{{body}} }""";

    [Fact]
    public void Lee_un_filtro()
    {
        FilterPreset filter = ContentReaders.ReadFilter(Utf8(Filter));
        Assert.Equal("calido", filter.Id);
        Assert.Equal("Cálido", filter.Name);
        Assert.Equal(2, filter.Effects.Count);
        Assert.Equal("rodaje.color", filter.Effects[0].Type);
        Assert.Equal(0.3, filter.Effects[0].Parameters["temperature"]);
        Assert.Equal("./calido-lut", filter.Effects[0].Options["lut"]);
        Assert.Empty(filter.Effects[1].Parameters);
    }

    [Theory]
    [InlineData("[]", "objeto JSON")]
    [InlineData("{ nope", "No es JSON válido")]
    [InlineData("""{ "version": 1, "name": "x", "effects": [] }""", "Falta format")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x" }""", "se esperaba «rodaje.filter»")]
    [InlineData("""{ "format": "rodaje.filter", "name": "x" }""", "version tiene que ser un entero")]
    [InlineData("""{ "format": "rodaje.filter", "version": 0, "name": "x" }""", "version tiene que ser un entero")]
    [InlineData("""{ "format": "rodaje.filter", "version": 2, "name": "x" }""", "actualiza el programa")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "id": "Mal Id", "name": "x" }""", "id «Mal Id» no es válido")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": " " }""", "Falta name")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x" }""", "Falta effects")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": {} }""", "effects tiene que ser una lista")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [] }""", "effects está vacía")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [1] }""", "effects[0] tiene que ser un objeto")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{}] }""", "effects[0] necesita type")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "Color" }] }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "color" }] }""", "no es un tipo de efecto válido")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "parameters": [] }] }""", "parameters tiene que ser un objeto")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "parameters": { "a b": 1 } }] }""", "no es un nombre de parámetro válido")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "parameters": { "exposure": "1" } }] }""", "exposure tiene que ser un número")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "parameters": { "exposure": 1, "exposure": 2 } }] }""", "exposure está repetido")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "options": { "lut": 3 } }] }""", "lut tiene que ser texto")]
    [InlineData("""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "options": { "1lut": "a" } }] }""", "no es un nombre de opción válido")]
    public void Rechaza_un_filtro_con_el_motivo(string json, string expected) =>
        Assert.Contains(expected, FilterError(json), StringComparison.Ordinal);

    [Fact]
    public void Limita_tamaño_efectos_valores_y_largo_de_opciones()
    {
        Assert.Contains("1 MB", FilterError(new string(' ', PresetReader.MaxFileBytes + 1)), StringComparison.Ordinal);
        string many = string.Join(",", Enumerable.Repeat("""{ "type": "rodaje.color" }""", PresetReader.MaxEffects + 1));
        Assert.Contains("más de 32 efectos", FilterError($$"""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{{many}}] }"""), StringComparison.Ordinal);
        string values = string.Join(",", Enumerable.Range(0, PresetReader.MaxValues + 1).Select(i => $"\"p{i}\": 1"));
        Assert.Contains("más de 16 valores", FilterError($$"""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "parameters": { {{values}} } }] }"""), StringComparison.Ordinal);
        string options = string.Join(",", Enumerable.Range(0, PresetReader.MaxValues + 1).Select(i => $"\"o{i}\": \"a\""));
        Assert.Contains("más de 16 opciones", FilterError($$"""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "options": { {{options}} } }] }"""), StringComparison.Ordinal);
        string longText = new('a', PresetReader.MaxOptionLength + 1);
        Assert.Contains("pasa de 256", FilterError($$"""{ "format": "rodaje.filter", "version": 1, "name": "x", "effects": [{ "type": "rodaje.color", "options": { "lut": "{{longText}}" } }] }"""), StringComparison.Ordinal);
        Assert.Contains("name pasa de 80", FilterError($$"""{ "format": "rodaje.filter", "version": 1, "name": "{{new string('n', 81)}}", "effects": [{ "type": "rodaje.color" }] }"""), StringComparison.Ordinal);
    }

    [Fact]
    public void Lee_un_efecto_con_ancla_curvas_y_referencia()
    {
        EffectPreset effect = ContentReaders.ReadEffect(Utf8(Effect(extra: """, "reference": { "width": 3840, "height": 2160 }""")));
        Assert.Equal(EffectPlacement.In, effect.Placement);
        Assert.Equal(0.5, effect.DurationSeconds);
        Assert.Equal((3840, 2160), (effect.ReferenceWidth, effect.ReferenceHeight));
        PresetCurve curve = Assert.Single(effect.Curves);
        Assert.Equal(("rodaje.transform", "scale"), (curve.Effect, curve.Parameter));
        Assert.Equal([new PresetKey(0, 1.3, PresetInterpolation.EaseOut), new PresetKey(0.5, 1, PresetInterpolation.Linear)], curve.Keys);
        Assert.Equal(EffectPlacement.Out, ContentReaders.ReadEffect(Utf8(Effect("\"end\""))).Placement);
        EffectPreset whole = ContentReaders.ReadEffect(Utf8(Effect("\"whole\"")));
        Assert.Equal(EffectPlacement.Whole, whole.Placement);
        Assert.Equal((1920, 1080), (whole.ReferenceWidth, whole.ReferenceHeight));
    }

    [Fact]
    public void Lee_rebote_elastico_y_bezier_con_sus_puntos()
    {
        string curves = """[ { "effect": "rodaje.transform", "parameter": "scale", "keys": [ { "t": 0, "value": 1.3, "interp": "bezier", "ease": [0.2, 1.4, 0.4, 1] }, { "t": 0.2, "value": 1, "interp": "bounce" }, { "t": 0.4, "value": 1.1, "interp": "elastic" }, { "t": 0.5, "value": 1 } ] } ]""";
        PresetCurve curve = Assert.Single(ContentReaders.ReadEffect(Utf8(Effect(curves: curves))).Curves);
        Assert.Equal([PresetInterpolation.Bezier, PresetInterpolation.Bounce, PresetInterpolation.Elastic, PresetInterpolation.Linear], curve.Keys.Select(k => k.Interpolation));
        Assert.Equal((0.2, 1.4, 0.4, 1.0), curve.Keys[0].Ease);
        Assert.Null(curve.Keys[1].Ease);
    }

    [Fact]
    public void Un_efecto_puede_animar_un_efecto_propio_de_su_lista()
    {
        EffectPreset effect = ContentReaders.ReadEffect(Utf8(Effect(
            effects: """[ { "type": "rodaje.blur", "parameters": { "radius": 0 } } ]""",
            curves: """[ { "effect": "rodaje.blur", "parameter": "radius", "keys": [ { "t": 0, "value": 30, "interp": "hold" }, { "t": 0.25, "value": 0, "interp": "easeIn" }, { "t": 0.5, "value": 0, "interp": "easeInOut" } ] } ]""")));
        Assert.Equal("rodaje.blur", effect.Effects[0].Type);
        Assert.Equal(PresetInterpolation.Hold, effect.Curves[0].Keys[0].Interpolation);
    }

    [Theory]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "durationSeconds": 1, "curves": [] }""", "Falta anchor")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "middle", "durationSeconds": 1 }""", "anchor tiene que ser start, end o whole")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start" }""", "Falta durationSeconds")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 0 }""", "durationSeconds tiene que ser mayor que 0 y de hasta 600")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 601 }""", "de hasta 600")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "reference": { "width": 8, "height": 1080 } }""", "reference necesita width y height")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "reference": { "width": 1920 } }""", "reference necesita width y height")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1 }""", "no trae effects ni curves")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": {} }""", "curves tiene que ser una lista")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [3] }""", "curves[0] tiene que ser un objeto")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "parameter": "x", "keys": [] }] }""", "curves[0] necesita effect")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.blur", "parameter": "radius", "keys": [] }] }""", "que no está en effects")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "keys": [] }] }""", "curves[0] necesita parameter")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "parameter": "x-y", "keys": [] }] }""", "no es un nombre de parámetro válido")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "parameter": "skew", "keys": [] }] }""", "no es un parámetro de la transformación")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "parameter": "x" }] }""", "curves[0] necesita keys")]
    [InlineData("""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "parameter": "x", "keys": [{ "t": 0, "value": 1 }] }, { "effect": "rodaje.transform", "parameter": "x", "keys": [{ "t": 0, "value": 1 }] }] }""", "repite la curva de rodaje.transform/x")]
    public void Rechaza_un_efecto_con_el_motivo(string json, string expected) =>
        Assert.Contains(expected, EffectError(json), StringComparison.Ordinal);

    [Theory]
    [InlineData("{}", "tiene que ser una lista de claves")]
    [InlineData("[]", "no tiene claves")]
    [InlineData("[1]", "tiene que ser un objeto con t y value")]
    [InlineData("""[{ "value": 1 }]""", "[0].t tiene que ser un número")]
    [InlineData("""[{ "t": 2, "value": 1 }]""", "[0].t queda fuera de 0 a durationSeconds")]
    [InlineData("""[{ "t": -0.1, "value": 1 }]""", "queda fuera de 0 a durationSeconds")]
    [InlineData("""[{ "t": 0.2, "value": 1 }, { "t": 0.2, "value": 2 }]""", "[1].t tiene que ser mayor que el de la clave anterior")]
    [InlineData("""[{ "t": 0 }]""", "[0].value tiene que ser un número")]
    [InlineData("""[{ "t": 0, "value": 2 }]""", "[0].value queda fuera de 0 a 1")]
    [InlineData("""[{ "t": 0, "value": 1, "interp": "rebote" }]""", "interp «rebote» no existe")]
    [InlineData("""[{ "t": 0, "value": 1, "interp": "bezier" }]""", "[0].ease tiene que ser [x1, y1, x2, y2]")]
    [InlineData("""[{ "t": 0, "value": 1, "interp": "bezier", "ease": [1.5, 0, 0.5, 1] }]""", "[0].ease: x1 y x2 van de 0 a 1")]
    public void Rechaza_claves_con_el_motivo(string keys, string expected)
    {
        string json = $$"""{ "format": "rodaje.effect", "version": 1, "name": "x", "anchor": "start", "durationSeconds": 1, "curves": [{ "effect": "rodaje.transform", "parameter": "opacity", "keys": {{keys}} }] }""";
        Assert.Contains(expected, EffectError(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Limita_curvas_y_claves()
    {
        string curves = string.Join(",", Enumerable.Range(0, PresetReader.MaxCurves + 1).Select(i => $$"""{ "effect": "rodaje.transform", "parameter": "p{{i}}", "keys": [] }"""));
        Assert.Contains("más de 64 curvas", EffectError(Effect(curves: $"[{curves}]")), StringComparison.Ordinal);
        string keys = string.Join(",", Enumerable.Range(0, PresetReader.MaxKeys + 1).Select(i => $$"""{ "t": {{i}}, "value": 0 }"""));
        Assert.Contains("más de 1000 claves", EffectError(Effect(curves: $$"""[{ "effect": "rodaje.transform", "parameter": "x", "keys": [{{keys}}] }]""")), StringComparison.Ordinal);
    }

    [Fact]
    public void Lee_una_transicion_v2_con_mezcla_y_sus_valores()
    {
        TransitionPreset preset = ContentReaders.ReadTransition(Utf8(Transition("""
            , "mix": "rodaje.wipe", "mixParameters": { "angle": 180, "softness": 0 }, "mixOptions": { "direction": "left" },
            "reference": { "width": 1920, "height": 1080 },
            "outgoing": { "x": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": -1920 } ] },
            "incoming": { "x": [ { "t": 0, "value": 1920 }, { "t": 0.5, "value": 0 } ], "opacity": [ { "t": 0, "value": 1 } ] }
            """)));
        Assert.Equal("deslizar", preset.Id);
        Assert.Equal("rodaje.wipe", preset.Mix);
        Assert.Equal(180, preset.MixParameters["angle"]);
        Assert.Equal("left", preset.MixOptions["direction"]);
        Assert.Equal(-1920, Assert.Single(preset.Outgoing).Keys[1].Value);
        Assert.Equal(["x", "opacity"], preset.Incoming.Select(c => c.Parameter));
        Assert.All(preset.Incoming, c => Assert.Equal("rodaje.transform", c.Effect));
    }

    [Fact]
    public void Sin_mezcla_ni_curvas_es_un_fundido_cruzado()
    {
        TransitionPreset preset = ContentReaders.ReadTransition(Utf8(Transition()));
        Assert.Equal("rodaje.crossfade", preset.Mix);
        Assert.Empty(preset.Outgoing);
        Assert.Empty(preset.Incoming);
        Assert.Empty(preset.MixParameters);
    }

    [Fact]
    public void Lee_el_v1_de_guardar_como_transicion_como_curvas_del_clip_que_entra()
    {
        const string V1 = """
            { "format": "rodaje.preset", "version": 1, "id": "propios.zoom-de-entrada", "name": "Zoom de entrada", "kind": "transition",
              "effect": "rodaje.transform", "durationSeconds": 0.5, "reference": { "width": 1920, "height": 1080 },
              "tracks": { "scale": [ { "t": 0, "value": 1.2, "interp": "easeOut" }, { "t": 0.5, "value": 1, "interp": "linear" } ] } }
            """;
        TransitionPreset preset = ContentReaders.ReadTransition(Utf8(V1));
        Assert.Equal("propios.zoom-de-entrada", preset.Id);
        Assert.Equal("rodaje.crossfade", preset.Mix);
        Assert.Empty(preset.Outgoing);
        PresetCurve curve = Assert.Single(preset.Incoming);
        Assert.Equal("scale", curve.Parameter);
        Assert.Equal(PresetInterpolation.EaseOut, curve.Keys[0].Interpolation);
    }

    [Theory]
    [InlineData(""", "kind": "effect" """, "no una transición")]
    [InlineData(""", "mix": "rodaje.preset" """, "mix no puede ser rodaje.preset")]
    [InlineData(""", "mix": 3 """, "mix necesita mix")]
    [InlineData(""", "outgoing": [] """, "outgoing tiene que ser un objeto")]
    [InlineData(""", "incoming": { "zoom": [] } """, "incoming.zoom: «zoom» no es un parámetro de la transformación")]
    [InlineData(""", "incoming": { "x": [{ "t": 0, "value": 0 }], "x": [{ "t": 0, "value": 0 }] } """, "incoming.x está repetido")]
    [InlineData(""", "incoming": { "scale": [{ "t": 0, "value": 101 }] } """, "queda fuera de 0 a 100")]
    [InlineData(""", "mixParameters": { "angle": "a" } """, "mixParameters.angle tiene que ser un número")]
    [InlineData(""", "mixOptions": [] """, "mixOptions tiene que ser un objeto")]
    public void Rechaza_una_transicion_v2_con_el_motivo(string body, string expected) =>
        Assert.Contains(expected, TransitionError(Transition(body.TrimEnd())), StringComparison.Ordinal);

    [Fact]
    public void Rechaza_lo_propio_de_cada_version()
    {
        Assert.Contains("de hasta 60", TransitionError("""{ "format": "rodaje.preset", "version": 2, "name": "x", "durationSeconds": 61 }"""), StringComparison.Ordinal);
        Assert.Contains("Falta tracks", TransitionError(Transition(version: "1")), StringComparison.Ordinal);
        Assert.Contains("effect tiene que ser rodaje.transform", TransitionError(Transition(""", "effect": "rodaje.color", "tracks": {}""", "1")), StringComparison.Ordinal);
        Assert.Contains("actualiza el programa", TransitionError(Transition(version: "3")), StringComparison.Ordinal);
    }
}
