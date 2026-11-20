using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.MotionTemplates;

internal static class TemplateSamples
{
    public const string Barra = """
        { "format": "rodaje.template", "version": 1, "name": "Rótulo inferior Barra", "kind": "title",
          "reference": { "width": 1920, "height": 1080 }, "durationSeconds": 5, "minSeconds": 1.5,
          "body": { "layers": [
            { "id": "nombre", "text": "Nombre Apellido", "start": 0, "end": 0.1, "x": -560, "y": 380,
              "box": { "widthFraction": 0.5 },
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Nombre",
                "font": { "family": "Segoe UI", "weight": 700 }, "size": 64,
                "background": { "color": "#E63946", "padding": [24, 10] },
                "in": { "durationSeconds": 0.5, "curves": { "x": [ { "t": 0, "value": -200, "interp": "easeOut" }, { "t": 0.5, "value": 0 } ] } } } },
            { "id": "cargo", "text": "Cargo", "start": 0.2, "x": -560, "y": 450,
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Cargo",
                "font": { "family": "Segoe UI" }, "size": 40 } } ] },
          "parameters": [
            { "id": "nombre", "label": "Nombre", "type": "text", "targets": ["layers/nombre/text"] },
            { "id": "cargo", "label": "Cargo", "type": "text", "targets": ["layers/cargo/text"] },
            { "id": "acento", "label": "Color de acento", "type": "color", "targets": ["layers/nombre/style/background/color"] },
            { "id": "tamano", "label": "Tamaño", "type": "number", "min": 1, "max": 2000, "step": 1, "targets": ["layers/nombre/style/size"] } ] }
        """;

    /// <summary>La versión siguiente: sin «cargo», con «tamano» hasta 100 y un parámetro nuevo.</summary>
    public static string BarraV2 => Barra
        .Replace("""{ "id": "cargo", "label": "Cargo", "type": "text", "targets": ["layers/cargo/text"] },""", "", StringComparison.Ordinal)
        .Replace("\"max\": 2000", "\"max\": 100", StringComparison.Ordinal)
        .Replace("\"text\": \"Cargo\"", "\"text\": \"Puesto\"", StringComparison.Ordinal);

    public static string Install(PackageLocations locations, string folder, string version, string document)
    {
        string archive = Path.Combine(folder, $"plantillas-{version}.paquete");
        File.WriteAllBytes(archive, PackageWriter.Write(new SingleItemPackage("rodaje-plantillas", PackageVersion.Parse(version), "Plantillas de prueba",
            "Dathinel", "CC0-1.0", "rodaje", "barra", ItemKind.MotionTemplate, "Rótulo inferior Barra", "plantillas/barra.json"), Encoding.UTF8.GetBytes(document)));
        new PackageInstaller(locations, "rodaje").Install(archive);
        return $"rodaje-plantillas@{version}/barra";
    }

    public static JsonElement Json(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }
}

public sealed class MotionTemplateCatalogTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Lista_las_instaladas_y_las_propias_y_omite_las_que_no_se_leen()
    {
        var locations = PackageLocations.Under(_folder.File("complementos"));
        string reference = TemplateSamples.Install(locations, _folder.Path, "1.0.0", TemplateSamples.Barra);
        Directory.CreateDirectory(locations.Own);
        File.WriteAllText(Path.Combine(locations.Own, "mi-rótulo.json"), TemplateSamples.Barra.Replace("Rótulo inferior Barra", "Mi rótulo", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(locations.Own, "rota.json"), TemplateSamples.Barra.Replace("\"layers/cargo/text\"", "\"layers/no-existe/text\"", StringComparison.Ordinal));

        IReadOnlyList<MotionTemplateEntry> entries = MotionTemplateCatalog.List(PackageLibrary.Scan(locations, "rodaje"));

        Assert.Equal(2, entries.Count);
        MotionTemplateEntry installed = entries.Single(e => e.Origin == PackageOrigin.Installed);
        Assert.Equal((reference, "Rótulo inferior Barra", "Plantillas de prueba", TemplateKind.Title), (installed.Reference, installed.Name, installed.Source, installed.Kind));
        Assert.Equal(reference, installed.ProjectSource);
        Assert.Equal(TemplateSamples.Barra, installed.DocumentText);
        MotionTemplateEntry own = entries.Single(e => e.Origin == PackageOrigin.Own);
        Assert.Equal(("propios@0.0.0/mi-rotulo", "Mi rótulo", "Mío"), (own.Reference, own.Name, own.Source));
        Assert.Null(own.ProjectSource);
    }

    [Fact]
    public void Newer_solo_ofrece_una_version_instalada_mayor_que_la_del_proyecto()
    {
        var locations = PackageLocations.Under(_folder.File("complementos"));
        TemplateSamples.Install(locations, _folder.Path, "1.1.0", TemplateSamples.BarraV2);
        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");

        Assert.Equal("rodaje-plantillas@1.1.0/barra", MotionTemplateCatalog.Newer(scan, "rodaje-plantillas@1.0.0/barra")?.Reference);
        Assert.Null(MotionTemplateCatalog.Newer(scan, "rodaje-plantillas@1.1.0/barra"));
        Assert.Null(MotionTemplateCatalog.Newer(scan, "rodaje-plantillas@2.0.0/barra"));
        Assert.Null(MotionTemplateCatalog.Newer(scan, "otro-paquete@1.0.0/barra"));
        Assert.Null(MotionTemplateCatalog.Newer(scan, null));
        Assert.NotNull(MotionTemplateCatalog.Find(scan, "rodaje-plantillas@1.0.0/barra"));
    }
}

public sealed class TemplateInstancesTests
{
    private static MotionTemplateEntry Entry(string document, string reference = "rodaje-plantillas@1.0.0/barra") =>
        new(reference, "Rótulo inferior Barra", "", "Plantillas", PackageOrigin.Installed, ContentReaders.ReadTemplate(document), document);

    [Fact]
    public void Una_plantilla_seleccionada_no_es_un_titulo_para_el_editor_de_texto() => UiThread.Run(async () =>
    {
        var session = new Rodaje.App.Services.EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTemplate(Entry(TemplateSamples.Barra)));

        Assert.NotNull(session.SelectedTemplate);
        Assert.Null(new Rodaje.App.Services.Inspector.TitleEditor(session).Target());
    });

    [Fact]
    public void El_generador_nuevo_lleva_documento_origen_minimo_y_capas_por_defecto()
    {
        ClipGenerator generator = TemplateInstances.NewGenerator(Entry(TemplateSamples.Barra));

        GeneratorTemplate template = generator.Template!;
        Assert.Equal(GeneratorKinds.Template, generator.Kind);
        Assert.Equal(("rodaje-plantillas@1.0.0/barra", "Rótulo inferior Barra", 1.5, TemplateSamples.Barra), (template.Source, template.Name, template.MinSeconds, template.Document));
        Assert.Empty(template.Values);
        Assert.Equal(["nombre", "cargo"], template.Layers.Select(l => l.Id));
        TemplateLayer first = template.Layers[0];
        Assert.Equal(("Nombre Apellido", -560.0, 380.0, 0.0, 0.1, 0.5), (first.Text, first.X, first.Y, first.Start, first.End, first.Box!.WidthFraction!.Value));
        Assert.Equal(("#E63946", 64.0, 700), (first.Style.Background!.Color, first.Style.Size, first.Style.Font.Weight));
    }

    [Fact]
    public void El_generador_nuevo_se_inserta_y_cambiar_un_valor_es_un_paso_que_se_deshace()
    {
        MotionTemplateEntry entry = Entry(TemplateSamples.Barra);
        var sequence = new Sequence { Id = "s", Name = "S", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" });
        var project = new Project { Id = "p", Name = "P", Sequences = [sequence] };
        SequenceEditCommand insert = Edits.InsertTitle("s", Time.Zero, Time.FromSeconds(entry.Document.DurationSeconds), TemplateInstances.NewGenerator(entry), "Texto", new SequentialIdGenerator());
        insert.Apply(project);
        string id = insert.CreatedIds[0];
        GeneratorTemplate template = sequence.FindClip(id)!.Value.Clip.Generator!.Template!;

        Dictionary<string, JsonElement> values = TemplateInstances.With(template.Values, "nombre", JsonValue.Create("Ana Ruiz")!);
        values = TemplateInstances.With(values, "acento", JsonValue.Create("#2A9D8F")!);
        SequenceEditCommand change = Edits.SetTemplateValues("s", id, values, TemplateInstances.Layers(entry.Document, values, template.Source));
        change.Apply(project);

        GeneratorTemplate changed = sequence.FindClip(id)!.Value.Clip.Generator!.Template!;
        Assert.Equal(("Ana Ruiz", "#2A9D8F", "Cargo"), (changed.Layers[0].Text, changed.Layers[0].Style.Background!.Color, changed.Layers[1].Text));
        Assert.Equal("Ana Ruiz", TemplateInstances.Current(entry.Document, changed.Values, entry.Document.Parameters[0])!.GetValue<string>());
        Assert.Equal("Cargo", TemplateInstances.Current(entry.Document, changed.Values, entry.Document.Parameters[1])!.GetValue<string>());
        change.Revert(project);
        Assert.Equal("Nombre Apellido", sequence.FindClip(id)!.Value.Clip.Generator!.Template!.Layers[0].Text);
    }

    [Fact]
    public void Un_valor_fuera_de_rango_o_una_plantilla_que_no_es_de_titulos_se_rechazan()
    {
        MotionTemplateEntry entry = Entry(TemplateSamples.Barra);
        Dictionary<string, JsonElement> bad = TemplateInstances.With(new Dictionary<string, JsonElement>(), "tamano", JsonValue.Create(5000)!);

        Assert.Throws<PackageContentException>(() => TemplateInstances.Layers(entry.Document, bad, null));
    }

    [Fact]
    public void Actualizar_conserva_los_valores_cuyo_id_sigue_y_dice_cuales_volvieron_a_su_defecto()
    {
        MotionTemplateEntry old = Entry(TemplateSamples.Barra);
        var values = new Dictionary<string, JsonElement>
        {
            ["nombre"] = TemplateSamples.Json("\"Ana Ruiz\""),
            ["cargo"] = TemplateSamples.Json("\"Directora\""),
            ["tamano"] = TemplateSamples.Json("180"),
            ["acento"] = TemplateSamples.Json("\"#2A9D8F\""),
        };
        GeneratorTemplate current = TemplateInstances.Create(old.Document, old.DocumentText, old.ProjectSource, old.Name, values);

        (GeneratorTemplate updated, IReadOnlyList<string> reset) = TemplateInstances.Update(current, Entry(TemplateSamples.BarraV2, "rodaje-plantillas@1.1.0/barra"));

        Assert.Equal(["Cargo", "Tamaño"], reset.Order());
        Assert.Equal(["acento", "nombre"], updated.Values.Keys.Order());
        Assert.Equal(("rodaje-plantillas@1.1.0/barra", TemplateSamples.BarraV2), (updated.Source, updated.Document));
        Assert.Equal(("Ana Ruiz", "#2A9D8F", 64.0, "Puesto"), (updated.Layers[0].Text, updated.Layers[0].Style.Background!.Color, updated.Layers[0].Style.Size, updated.Layers[1].Text));
        Assert.Equal("Directora", current.Layers[1].Text);
    }
}

public sealed class TemplateAuthoringTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static (Sequence Sequence, IReadOnlyList<Clip> Titles) TwoTitles()
    {
        TextStyle upper = TitleStyles.Default();
        upper.Size = 96;
        upper.Background = new StyleBackground { Color = "#E63946", Padding = [24, 10], Radius = 4 };
        TextStyle lower = TitleStyles.Default();
        lower.Stroke = new StyleStroke { Color = "#101010", Width = 3 };
        var name = new Clip
        {
            Id = "nombre", Start = Time.FromSeconds(10), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(5),
            Generator = new ClipGenerator { Text = "Ana Ruiz", Style = upper, Box = new TextFrame { WidthFraction = 0.5 } },
            Effects = [new EffectInstance { Id = "e1", Type = BuiltInEffects.Transform, Parameters = { [BuiltInEffects.TransformParameters.X] = new Parameter { Value = -280 }, [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = 190 } } }],
        };
        var role = new Clip
        {
            Id = "cargo", Start = Time.FromSeconds(10.5), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(4),
            Generator = new ClipGenerator { Text = "Directora\nde fotografía", Style = lower },
        };
        var sequence = new Sequence { Id = "s", Name = "S", FrameRate = Rational.Fps30, Width = 960, Height = 540 };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" });
        sequence.VideoTracks.Add(new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2", Clips = [name] });
        sequence.VideoTracks.Add(new Track { Id = "v3", Kind = TrackKind.Video, Name = "V3", Clips = [role] });
        return (sequence, TemplateAuthoring.Titles(sequence, ["cargo", "nombre", "no-existe"]));
    }

    [Fact]
    public void El_lector_acepta_el_documento_e_instancia_las_mismas_capas()
    {
        (Sequence sequence, IReadOnlyList<Clip> titles) = TwoTitles();
        Assert.Equal(["nombre", "cargo"], titles.Select(t => t.Id));
        IReadOnlyList<TemplateFieldChoice> fields = [.. TemplateAuthoring.Fields(titles).Select(f => f with { Exposed = true })];
        Assert.Equal(
            [TemplateField.Text, TemplateField.FillColor, TemplateField.BackgroundColor, TemplateField.Font, TemplateField.Size,
             TemplateField.Text, TemplateField.FillColor, TemplateField.StrokeColor, TemplateField.Font, TemplateField.Size],
            fields.Select(f => f.Field));

        string json = TemplateAuthoring.Build("Rótulo de Ana", sequence, titles, fields);
        TemplateDocument document = ContentReaders.ReadTemplate(json);
        List<TemplateLayer> layers = TemplateInstances.Layers(document, new Dictionary<string, JsonElement>(), null);

        Assert.Equal(("Rótulo de Ana", TemplateKind.Title, 5.0), (document.Name, document.Kind, document.DurationSeconds));
        Assert.Equal(10, document.Parameters.Count);
        Assert.Equal(2, layers.Count);
        for (int i = 0; i < layers.Count; i++)
        {
            ClipGenerator original = titles[i].Generator!;
            Assert.Equal(original.Text, layers[i].Text);
            TextStyle expected = original.Style!.Clone();
            expected.Source = null;
            layers[i].Style.Name = expected.Name;
            Assert.True(expected.ContentEquals(layers[i].Style), $"El estilo de la capa {i + 1} cambió al guardarlo.");
        }

        // 960 × 540 con referencia 1920 × 1080: la posición del clip (px de la secuencia) va en px de la referencia.
        Assert.Equal((-560.0, 380.0, 0.0, 0.0, 0.5), (layers[0].X, layers[0].Y, layers[0].Start, layers[0].End, layers[0].Box!.WidthFraction!.Value));
        Assert.Equal((0.0, 0.0, 0.5, 0.5), (layers[1].X, layers[1].Y, layers[1].Start, layers[1].End));
        Assert.Null(layers[1].Box);

        var values = TemplateInstances.With(new Dictionary<string, JsonElement>(), "capa-2-borde", JsonValue.Create("#FF0000")!);
        Assert.Equal("#FF0000", TemplateInstances.Layers(document, values, null)[1].Style.Stroke!.Color);
    }

    [Fact]
    public void Solo_lo_expuesto_es_parametro_y_un_nombre_vacio_se_rechaza()
    {
        (Sequence sequence, IReadOnlyList<Clip> titles) = TwoTitles();
        IReadOnlyList<TemplateFieldChoice> fields = TemplateAuthoring.Fields(titles);

        TemplateDocument document = ContentReaders.ReadTemplate(TemplateAuthoring.Build("Solo textos", sequence, titles, fields));

        Assert.Equal(["capa-1-texto", "capa-2-texto"], document.Parameters.Select(p => p.Id));
        Assert.Equal(["Texto 1", "Texto 2"], document.Parameters.Select(p => p.Label));
        Assert.Throws<ArgumentException>(() => TemplateAuthoring.Build("x", sequence, titles, [fields[0] with { Label = " " }]));
        Assert.Throws<ArgumentException>(() => TemplateAuthoring.Build("x", sequence, [], fields));
    }

    [Fact]
    public void Guardada_en_propios_sale_en_el_catalogo_y_exportada_la_acepta_el_instalador()
    {
        (Sequence sequence, IReadOnlyList<Clip> titles) = TwoTitles();
        var locations = PackageLocations.Under(_folder.File("complementos"));
        string json = TemplateAuthoring.Build("Rótulo de Ana", sequence, titles, TemplateAuthoring.Fields(titles));

        (string path, string reference) = TemplateAuthoring.Save(locations.Own, "Rótulo de Ana", json);
        (string second, string secondReference) = TemplateAuthoring.Save(locations.Own, "Rótulo de Ana", json);

        Assert.Equal(("rotulo-de-ana.json", "propios@0.0.0/rotulo-de-ana"), (Path.GetFileName(path), reference));
        Assert.Equal(("rotulo-de-ana-2.json", "propios@0.0.0/rotulo-de-ana-2"), (Path.GetFileName(second), secondReference));
        MotionTemplateEntry own = MotionTemplateCatalog.Find(PackageLibrary.Scan(locations, "rodaje"), reference)!;
        Assert.Equal(("Rótulo de Ana", PackageOrigin.Own), (own.Name, own.Origin));

        byte[] package = TemplateAuthoring.Export(own, "Ana Ñandú");
        Assert.Equal(package, TemplateAuthoring.Export(own, "Ana Ñandú"));
        string archive = _folder.File("rótulo.paquete");
        File.WriteAllBytes(archive, package);
        var other = PackageLocations.Under(_folder.File("otro equipo"));
        InstallResult result = new PackageInstaller(other, "rodaje").Install(archive);
        Assert.Empty(result.Warnings);
        MotionTemplateEntry imported = Assert.Single(MotionTemplateCatalog.List(PackageLibrary.Scan(other, "rodaje")));
        Assert.Equal(("plantilla.rotulo-de-ana@1.0.0/rotulo-de-ana", "Rótulo de Ana", json), (imported.Reference, imported.Name, imported.DocumentText));
    }
}

[Collection(GpuCompositionGroup.Name)]
public sealed class TemplateThumbnailTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void La_tira_va_y_vuelve_por_el_png_y_un_archivo_ajeno_no_se_lee()
    {
        var rgba = new byte[4 * 3 * 2 * 4];
        new Random(7).NextBytes(rgba);
        var strip = new TemplateStrip(4, 2, 3, rgba);

        byte[] png = TemplateThumbnails.EncodePng(strip);
        TemplateStrip read = TemplateThumbnails.DecodePng(png)!;

        Assert.Equal((4, 2, 3), (read.FrameWidth, read.FrameHeight, read.Frames));
        Assert.Equal(rgba, read.Rgba);
        byte[] second = strip.FrameBgra(1);
        Assert.Equal((rgba[16 + 2], rgba[16 + 1], rgba[16], rgba[16 + 3]), (second[0], second[1], second[2], second[3]));
        Assert.Null(TemplateThumbnails.DecodePng(png.AsSpan(0, png.Length - 20)));
        png[^30] ^= 0xFF;
        Assert.Null(TemplateThumbnails.DecodePng(png));
        Assert.Null(TemplateThumbnails.DecodePng("no es un png"u8));
    }

    [Fact]
    public void La_cache_va_por_el_sha256_del_documento()
    {
        var strip = new TemplateStrip(2, 1, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        Assert.Null(TemplateThumbnails.Load(_folder.Path, "documento"));

        TemplateThumbnails.Save(_folder.Path, "documento", strip);

        Assert.Matches("^[0-9a-f]{64}[.]png$", Path.GetFileName(TemplateThumbnails.CachePath(_folder.Path, "documento")));
        Assert.Equal(strip.Rgba, TemplateThumbnails.Load(_folder.Path, "documento")!.Rgba);
        Assert.Null(TemplateThumbnails.Load(_folder.Path, "otro documento"));
        Assert.NotEqual(TemplateThumbnails.CachePath(_folder.Path, "documento"), TemplateThumbnails.CachePath(_folder.Path, "otro documento"));
    }

    [Fact]
    public Task La_miniatura_tiene_12_cuadros_de_192x108_y_el_rotulo_se_ve() => OwnThread.Run(() =>
    {
        var entry = new MotionTemplateEntry("propios@0.0.0/barra", "Barra", "", "Mío", PackageOrigin.Own, ContentReaders.ReadTemplate(TemplateSamples.Barra), TemplateSamples.Barra);
        TemplateStrip? strip = null;
        TemplateThumbnails.Render([entry], [], (_, s) => strip = s);
        var watch = Stopwatch.StartNew();
        TemplateThumbnails.Render([entry], [], (_, s) => strip = s);
        output.WriteLine($"Miniatura de 12 cuadros (segunda vez, shaders en caché): {watch.Elapsed.TotalMilliseconds:0} ms");

        Assert.NotNull(strip);
        Assert.Equal((192, 108, 12), (strip.FrameWidth, strip.FrameHeight, strip.Frames));
        byte[] middle = strip.FrameBgra(6);
        int red = 0;
        for (int i = 0; i < middle.Length; i += 4)
        {
            red += middle[i + 2] > 180 && middle[i] < 120 ? 1 : 0;
        }

        Assert.True(red > 50, $"El fondo rojo del rótulo no se ve en el cuadro del medio: {red} píxeles.");
        Assert.False(strip.FrameBgra(0).AsSpan().SequenceEqual(strip.FrameBgra(11)), "La tira no anima: el primer cuadro y el último son iguales.");
    });
}
