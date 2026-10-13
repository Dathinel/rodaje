using System.Text;
using System.Text.RegularExpressions;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests.Fuzz;

/// <summary>
/// Cada número de los archivos de complementos cambiado por <c>1e400</c> o <c>-1e400</c>, que <c>System.Text.Json</c> lee
/// como ±∞ sin error (tarea 267, después de #230): el lector lo rechaza con su <see cref="PackageContentException"/> y la
/// ruta del dato, en vez de dejar pasar un infinito a una curva, una duración o un tamaño. Es una guarda: los lectores ya
/// comprobaban <see cref="double.IsFinite"/> o leían enteros, y esta prueba cae si alguno deja de hacerlo.
/// </summary>
public sealed partial class OverflowNumberTests
{
    private const int Seed = 267_300_000;

    private const string Filter = """
        { "format": "rodaje.filter", "version": 1, "id": "calido", "name": "Cálido",
          "effects": [ { "type": "rodaje.color", "parameters": { "temperature": 0.3, "saturation": 0.1 } } ] }
        """;

    private const string Effect = """
        { "format": "rodaje.effect", "version": 1, "name": "Entrada con zoom", "anchor": "start", "durationSeconds": 0.5,
          "effects": [ { "type": "rodaje.color", "parameters": { "saturation": 0.2 } } ],
          "curves": [ { "effect": "rodaje.transform", "parameter": "scale", "keys": [ { "t": 0, "value": 1.3, "interp": "bezier", "ease": [0.4, 0, 0.2, 1] }, { "t": 0.5, "value": 1 } ] } ] }
        """;

    private const string Transition = """
        { "format": "rodaje.preset", "version": 2, "id": "deslizar", "name": "Deslizar", "kind": "transition", "durationSeconds": 0.5,
          "mix": "rodaje.wipe", "mixParameters": { "angle": 180, "softness": 0.2 }, "mixOptions": { "direction": "left" },
          "outgoing": { "x": [ { "t": 0, "value": 0 }, { "t": 0.5, "value": -1920 } ] } }
        """;

    private const string ExportPreset = """
        { "format": "rodaje.exportpreset", "version": 1, "id": "web", "name": "Web", "category": "web", "container": "mp4",
          "video": { "codec": "h264", "width": 1920, "height": 1080, "frameRate": 25, "rateControl": "crf", "quality": 23, "keyframeSeconds": 2 },
          "audio": { "codec": "aac", "rateControl": "vbr", "quality": 4, "loudness": { "lufs": -14, "truePeak": -1 } } }
        """;

    private const string TextStyle = """
        { "format": "rodaje.textstyle", "version": 2, "name": "Neón",
          "font": { "family": "Inter", "weight": 800 }, "size": 120, "lineHeight": 1.1, "letterSpacing": 2,
          "fill": { "color": "#FFFFFF", "gradient": { "kind": "linear", "angle": 90, "stops": [ { "position": 0, "color": "#ffd400" }, { "position": 1, "color": "#FF3D00" } ] } },
          "stroke": { "color": "#000000", "width": 4 },
          "shadow": { "color": "#00000099", "dx": 2, "dy": 6, "blur": 10 },
          "background": { "color": "#000000", "padding": [8, 4], "radius": 6 },
          "path": { "kind": "arc", "bend": -120 },
          "in": { "durationSeconds": 1, "unit": "letter", "stagger": 0.6,
                  "curves": { "blur": [ { "t": 0, "value": 24, "interp": "easeOut" }, { "t": 1, "value": 0 } ] } } }
        """;

    private const string SafeZones = """
        { "format": "rodaje.safezones", "version": 1, "id": "reels", "name": "Reels", "aspect": { "width": 9, "height": 16 },
          "zones": [ { "label": "Botones", "rect": [0.86, 0.5, 0.14, 0.38] } ] }
        """;

    private static readonly (string Name, string Text, Action<Stream> Read)[] Readers =
    [
        ("filtro", Filter, s => ContentReaders.ReadFilter(s)),
        ("efecto", Effect, s => ContentReaders.ReadEffect(s)),
        ("transición", Transition, s => ContentReaders.ReadTransition(s)),
        ("exportpreset", ExportPreset, s => ContentReaders.ReadExportPreset(s)),
        ("textstyle", TextStyle, s => ContentReaders.ReadTextStyle(s)),
        ("safezones", SafeZones, s => ContentReaders.ReadSafeZones(s)),
    ];

    [GeneratedRegex("""(?<=[:\[,]\s*)-?\d+(\.\d+)?(?=\s*[,\]}])""")]
    private static partial Regex NumberToken();

    [Fact]
    public void Seeds_AreValid()
    {
        foreach ((string _, string text, Action<Stream> read) in Readers)
        {
            read(Utf8(text));
        }
    }

    [Fact]
    public void EveryNumber_ThatOverflows_IsRejected()
    {
        var rng = new Random(Seed);
        foreach ((string name, string text, Action<Stream> read) in Readers)
        {
            MatchCollection numbers = NumberToken().Matches(text);
            Assert.NotEmpty(numbers);
            foreach (Match number in numbers)
            {
                string literal = rng.Next(2) == 0 ? "1e400" : "-1e400";
                string mutated = string.Concat(text.AsSpan(0, number.Index), literal, text.AsSpan(number.Index + number.Length));
                string what = $"{name}, semilla {Seed}: «{number.Value}» en la posición {number.Index} cambiado por {literal}";
                Exception? ex = Record.Exception(() => read(Utf8(mutated)));
                Assert.True(ex is PackageContentException, $"{what}: {(ex is null ? "se leyó sin error" : ex.GetType().Name + ": " + ex.Message)}");
                Assert.False(string.IsNullOrWhiteSpace(ex!.Message), what);
            }
        }
    }

    private static MemoryStream Utf8(string json) => new(Encoding.UTF8.GetBytes(json));
}
