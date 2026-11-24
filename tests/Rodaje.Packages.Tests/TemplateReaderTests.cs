using System.Text;
using System.Text.Json.Nodes;
using Rodaje.Packages.Content;

namespace Rodaje.Packages.Tests;

[Collection("Plantillas")]
public sealed class TemplateReaderTests
{
    internal const string Example = """
        { "format": "rodaje.template", "version": 1, "name": "Rótulo inferior Barra", "kind": "title",
          "reference": { "width": 1920, "height": 1080 }, "durationSeconds": 5, "minSeconds": 1.5,
          "body": { "layers": [
            { "id": "nombre", "text": "Nombre Apellido", "start": 0, "end": 0.1, "x": -560, "y": 380,
              "box": { "widthFraction": 0.5 },
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Nombre",
                "font": { "family": "Inter", "weight": 700 }, "size": 64,
                "background": { "color": "#E63946", "padding": [24, 10] },
                "typography": { "kerning": true },
                "in": { "durationSeconds": 0.5, "curves": { "x": [ { "t": 0, "value": -200, "interp": "easeOut" }, { "t": 0.5, "value": 0 } ] } } } },
            { "id": "cargo", "text": "Cargo", "start": 0.2, "x": -560, "y": 450,
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Cargo",
                "font": { "family": "Inter" }, "size": 40 } } ] },
          "parameters": [
            { "id": "nombre", "label": "Nombre", "type": "text", "targets": ["layers/nombre/text"] },
            { "id": "cargo", "label": "Cargo", "type": "text", "targets": ["layers/cargo/text"] },
            { "id": "acento", "label": "Color de acento", "type": "color", "targets": ["layers/nombre/style/background/color"] },
            { "id": "fuente", "label": "Fuente", "type": "font", "targets": ["layers/nombre/style/font", "layers/cargo/style/font"] },
            { "id": "tamano", "label": "Tamaño", "type": "number", "min": 1, "max": 2000, "step": 1, "targets": ["layers/nombre/style/size"] },
            { "id": "kerning", "label": "Kerning", "type": "bool", "targets": ["layers/nombre/style/typography/kerning"] },
            { "id": "peso", "label": "Peso", "type": "choice", "options": [ { "label": "Normal", "value": 400 }, { "label": "Negrita", "value": 700 } ],
              "targets": ["layers/nombre/style/font/weight"] } ] }
        """;

    internal static JsonObject Title(int count = 2)
    {
        var root = JsonNode.Parse(Example)!.AsObject();
        if (count != 2)
        {
            var layers = root["body"]!["layers"]!.AsArray();
            JsonNode prototype = layers[0]!.DeepClone();
            layers.Clear();
            for (int i = 0; i < count; i++)
            {
                JsonNode layer = prototype.DeepClone();
                layer["id"] = $"capa-{i}";
                layers.Add(layer);
            }

            root["parameters"] = new JsonArray();
        }

        return root;
    }

    internal static JsonObject Other(string kind)
    {
        JsonObject root = Title();
        root["kind"] = kind;
        root["parameters"] = new JsonArray();
        root["body"] = JsonNode.Parse(kind == "transition" ? """
            { "format": "rodaje.preset", "version": 2, "name": "Barrido", "durationSeconds": 1,
              "mix": "rodaje.wipe", "mixParameters": { "angle": 90 },
              "incoming": { "opacity": [ { "t": 0, "value": 0 }, { "t": 1, "value": 1 } ] } }
            """ : """
            { "format": "rodaje.effect", "version": 1, "name": "Entrada", "anchor": "start", "durationSeconds": 1,
              "curves": [ { "effect": "rodaje.transform", "parameter": "opacity", "keys": [ { "t": 0, "value": 0 }, { "t": 1, "value": 1 } ] } ] }
            """);
        return root;
    }

    internal static void Set(JsonNode root, string path, JsonNode? value)
    {
        string[] segments = path.Split('/');
        foreach (string segment in segments[..^1])
        {
            root = root is JsonArray array ? array[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]! : root[segment]!;
        }

        if (root is JsonArray list)
        {
            list[int.Parse(segments[^1], System.Globalization.CultureInfo.InvariantCulture)] = value;
        }
        else
        {
            root[segments[^1]] = value;
        }
    }

    [Fact]
    public void Lee_documento_de_dos_capas_y_todos_los_tipos()
    {
        TemplateDocument document = ContentReaders.ReadTemplate(Example);
        Assert.Equal(TemplateKind.Title, document.Kind);
        Assert.Equal("Rótulo inferior Barra", document.Name);
        Assert.Equal((1920, 1080, 5.0, 1.5), (document.ReferenceWidth, document.ReferenceHeight, document.DurationSeconds, document.MinSeconds));
        Assert.Equal(7, document.Parameters.Count);
        TemplateInstance instance = TemplateInstantiator.Instantiate(document);
        Assert.Equal(["nombre", "cargo"], instance.Layers.Select(l => l.Id));
        Assert.Equal((0.0, 0.1, -560.0, 380.0), (instance.Layers[0].Start, instance.Layers[0].End, instance.Layers[0].X, instance.Layers[0].Y));
        Assert.Equal(0.5, instance.Layers[0].Box!.WidthFraction);
        Assert.Null(instance.Layers[1].Box);
        Assert.Equal(0, instance.Layers[1].End);
        Assert.Equal(700, instance.Layers[0].Style.Font.Weight);
    }

    [Theory]
    [InlineData("format", "null", "format")]
    [InlineData("format", "\"rodaje.preset\"", "format")]
    [InlineData("version", "2", "actualiza el programa")]
    [InlineData("version", "0", "version")]
    [InlineData("version", "1.5", "version")]
    [InlineData("version", "\"1\"", "version")]
    [InlineData("name", "\"\"", "name")]
    [InlineData("id", "\"No válido\"", "id")]
    [InlineData("kind", "\"otro\"", "kind")]
    [InlineData("kind", "null", "kind")]
    [InlineData("reference", "[]", "reference")]
    [InlineData("reference/width", "0", "reference")]
    [InlineData("reference/height", "16385", "reference")]
    [InlineData("reference/width", "16.5", "reference")]
    [InlineData("durationSeconds", "0", "durationSeconds")]
    [InlineData("durationSeconds", "601", "durationSeconds")]
    [InlineData("durationSeconds", "1e999", "durationSeconds")]
    [InlineData("minSeconds", "-1", "minSeconds")]
    [InlineData("minSeconds", "6", "minSeconds")]
    [InlineData("minSeconds", "null", "minSeconds")]
    [InlineData("body", "null", "body")]
    [InlineData("body/layers", "{}", "layers")]
    [InlineData("body/layers", "[]", "layers")]
    [InlineData("body/layers/0", "true", "capa")]
    [InlineData("body/layers/1/id", "\"nombre\"", "id")]
    [InlineData("body/layers/0/id", "\"\"", "id")]
    [InlineData("body/layers/0/text", "42", "text")]
    [InlineData("body/layers/0/start", "-0.1", "start")]
    [InlineData("body/layers/0/end", "-1", "end")]
    [InlineData("body/layers/0/x", "1e999", "x")]
    [InlineData("body/layers/0/y", "\"0\"", "y")]
    [InlineData("body/layers/0/box", "[]", "box")]
    [InlineData("body/layers/0/box/widthFraction", "-0.01", "widthFraction")]
    [InlineData("body/layers/0/box/widthFraction", "1.01", "widthFraction")]
    [InlineData("body/layers/0/box/widthFraction", "0", "widthFraction")]
    [InlineData("body/layers/0/x", "16385", "x")]
    [InlineData("body/layers/0/y", "-16385", "y")]
    [InlineData("body/layers/0/style", "{}", "format")]
    [InlineData("body/layers/0/style", "null", "style")]
    [InlineData("body/layers/0/style/size", "1001", "size")]
    [InlineData("body/layers/0/style/font/weight", "999", "weight")]
    [InlineData("parameters", "{}", "parameters")]
    [InlineData("parameters/0", "null", "parámetro")]
    [InlineData("parameters/0/id", "\"cargo\"", "id")]
    [InlineData("parameters/0/label", "\"\"", "label")]
    [InlineData("parameters/0/type", "\"expression\"", "type")]
    [InlineData("parameters/0/targets", "[]", "targets")]
    [InlineData("parameters/0/targets", "{}", "targets")]
    [InlineData("parameters/0/targets/0", "42", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/no-existe/text\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre/no-existe\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/0/text\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"/layers/nombre/text\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre/\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre\"", "destino")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre/text/valor\"", "ruta")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre/style/size\"", "tipo")]
    [InlineData("parameters/2/type", "\"font\"", "tipo")]
    [InlineData("parameters/3/type", "\"color\"", "tipo")]
    [InlineData("parameters/4/min", "null", "min")]
    [InlineData("parameters/4/max", "null", "max")]
    [InlineData("parameters/4/min", "2001", "max")]
    [InlineData("parameters/4/min", "65", "rango")]
    [InlineData("parameters/4/step", "0", "step")]
    [InlineData("parameters/4/step", "-1", "step")]
    [InlineData("parameters/5/type", "\"number\"", "min")]
    [InlineData("parameters/6/options", "[]", "options")]
    [InlineData("parameters/6/options/0", "null", "opción")]
    [InlineData("parameters/6/options/0/label", "null", "label")]
    [InlineData("parameters/6/options/0/value", "null", "value")]
    [InlineData("parameters/6/options/0/value", "\"400\"", "tipo")]
    [InlineData("parameters/0/targets/0", "\"layers/nombre/id\"", "id de una capa")]
    [InlineData("body/layers/0/start", "4.9", "no llega a verse")]
    public void Cada_regla_rechaza_el_campo_invalido(string path, string json, string expected)
    {
        JsonObject root = Title();
        Set(root, path, JsonNode.Parse(json));
        Assert.Contains(expected, Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString())).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("kind")]
    [InlineData("body")]
    [InlineData("durationSeconds")]
    [InlineData("minSeconds")]
    public void Rechaza_campos_obligatorios_ausentes(string field)
    {
        JsonObject root = Title();
        root.Remove(field);
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("min")]
    [InlineData("max")]
    public void Number_exige_ambos_limites(string field)
    {
        JsonObject root = Title();
        root["parameters"]![4]!.AsObject().Remove(field);
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("parameters", 32)]
    [InlineData("targets", 16)]
    [InlineData("options", 16)]
    [InlineData("layers", 16)]
    public void Acepta_el_tope_y_rechaza_un_elemento_mas(string field, int limit)
    {
        JsonObject root = Title(field == "layers" ? limit : 2);
        JsonArray array;
        if (field == "parameters")
        {
            array = root["parameters"]!.AsArray();
            JsonNode parameter = array[0]!.DeepClone();
            array.Clear();
            for (int i = 0; i < limit; i++)
            {
                JsonNode copy = parameter.DeepClone();
                copy["id"] = $"p-{i}";
                array.Add(copy);
            }
        }
        else if (field == "layers")
        {
            array = root["body"]!["layers"]!.AsArray();
        }
        else
        {
            array = root["parameters"]![field == "targets" ? 0 : 6]![field]!.AsArray();
            while (array.Count < limit)
            {
                array.Add(array[0]!.DeepClone());
            }
        }

        _ = ContentReaders.ReadTemplate(root.ToJsonString());
        JsonNode extra = array[0]!.DeepClone();
        if (field is "parameters" or "layers")
        {
            extra["id"] = "extra";
        }

        array.Add(extra);
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("a", 10000)]
    [InlineData("\n", 199)]
    [InlineData("\u2028", 199)]
    [InlineData("\u2029", 199)]
    public void Limites_del_texto_y_lineas(string unit, int repeats)
    {
        JsonObject root = Title();
        string text = string.Concat(Enumerable.Repeat(unit, repeats));
        root["body"]!["layers"]![0]!["text"] = text;
        _ = ContentReaders.ReadTemplate(root.ToJsonString());
        root["body"]!["layers"]![0]!["text"] = text + unit;
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\0")]
    [InlineData("\u0085")]
    public void Texto_rechaza_los_controles_que_rechaza_generator_text(string control)
    {
        JsonObject root = Title();
        root["body"]!["layers"]![0]!["text"] = "Nombre" + control;
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Fact]
    public void Texto_conserva_espacios_tab_astral_y_marcas_bidi()
    {
        JsonObject root = Title();
        const string text = "  Ana\tRuiz\n\u200f🎬  ";
        root["body"]!["layers"]![0]!["text"] = text;
        TemplateDocument document = ContentReaders.ReadTemplate(root.ToJsonString());
        Assert.Equal(text, TemplateInstantiator.Instantiate(document).Layers[0].Text);
    }

    // Core rechaza una caja de ancho 0 (ModelValidator.ValidateBox): el lector también.
    [Theory]
    [InlineData(0.001)]
    [InlineData(1)]
    public void Caja_acepta_ambos_extremos(double fraction)
    {
        JsonObject root = Title();
        root["body"]!["layers"]![0]!["box"]!["widthFraction"] = fraction;
        _ = ContentReaders.ReadTemplate(root.ToJsonString());
    }

    [Fact]
    public void Duracion_acepta_600_y_minimo_igual_o_cero()
    {
        JsonObject root = Title();
        root["durationSeconds"] = 600;
        root["minSeconds"] = 600;
        _ = ContentReaders.ReadTemplate(root.ToJsonString());
        root["minSeconds"] = 0;
        _ = ContentReaders.ReadTemplate(root.ToJsonString());
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("effect")]
    public void Body_pasa_por_el_lector_del_tipo(string kind)
    {
        JsonObject root = Other(kind);
        TemplateInstance result = TemplateInstantiator.Instantiate(ContentReaders.ReadTemplate(root.ToJsonString()));
        Assert.Empty(result.Layers);
        Assert.Equal(kind == "transition", result.Transition is not null);
        Assert.Equal(kind == "effect", result.Effect is not null);
        root["body"]!["durationSeconds"] = -1;
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("transition", 1)]
    [InlineData("transition", 3)]
    [InlineData("effect", 2)]
    public void Valida_versiones_de_los_cuerpos(string kind, int version)
    {
        JsonObject root = Other(kind);
        root["body"]!["version"] = version;
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(root.ToJsonString()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"format\":\"rodaje.template\",\"version\":1,\"name\":\"\\ud800\"}")]
    public void Json_danado_solo_da_error_de_contenido(string json) =>
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(json));

    [Fact]
    public void Rechaza_duplicados_incluso_en_propiedades_desconocidas()
    {
        string json = Example.Insert(1, "\"extra\": {\"a\":1,\"a\":2},");
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(json));
    }

    [Fact]
    public void Limite_de_archivo_en_bytes_bom_comentarios_y_comas_finales()
    {
        byte[] example = Encoding.UTF8.GetBytes(Example);
        byte[] padded = new byte[TemplateLimits.MaxFileBytes];
        padded.AsSpan().Fill((byte)' ');
        example.CopyTo(padded, 0);
        using var stream = new MemoryStream(padded);
        _ = ContentReaders.ReadTemplate(stream);
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(Example + new string(' ', TemplateLimits.MaxFileBytes)));
        _ = ContentReaders.ReadTemplate("\uFEFF" + Example.Replace("\"version\": 1", "\"version\": 1 /* comentario */", StringComparison.Ordinal));
        _ = ContentReaders.ReadTemplate(Example[..^1] + ",}");
        _ = ContentReaders.ReadTemplate(Example.Replace("\"weight\": 700", "\"weight\": 700, /* dentro de body */", StringComparison.Ordinal));
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(Example.Insert(1, "\"extra\": \"" + new string('ñ', 140000) + "\",")));
    }

    [Fact]
    public void Lee_y_limita_flujos_sin_busqueda()
    {
        using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes(Example));
        _ = ContentReaders.ReadTemplate(stream);
        using var tooBig = new NonSeekableStream(new byte[TemplateLimits.MaxFileBytes + 1]);
        Assert.Throws<PackageContentException>(() => ContentReaders.ReadTemplate(tooBig));
    }

    [Fact]
    public void Mil_documentos_mutados_no_producen_otras_excepciones()
    {
        var random = new Random(20261116);
        byte[] valid = Encoding.UTF8.GetBytes(Example);
        for (int i = 0; i < 1000; i++)
        {
            byte[] bytes = (byte[])valid.Clone();
            if (i % 3 == 0)
            {
                bytes = bytes[..random.Next(bytes.Length)];
            }
            else
            {
                for (int j = 0; j < random.Next(1, 9); j++)
                {
                    bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                }
            }

            using var stream = new MemoryStream(bytes);
            Exception? error = Record.Exception(() =>
            {
                TemplateDocument document = ContentReaders.ReadTemplate(stream);
                _ = TemplateInstantiator.Instantiate(document);
            });
            Assert.True(error is null or PackageContentException, $"Caso {i}: {error}");
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
