using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Services;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.MotionTemplates;

/// <summary>Los casos de la revisión de bloque de la fase 5 (D-064).</summary>
public sealed class MotionTemplateReviewTests : IDisposable
{
    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    private static MotionTemplateEntry Entry(string document, string reference = "rodaje-plantillas@1.0.0/barra") =>
        new(reference, "Rótulo inferior Barra", "", "Plantillas", PackageOrigin.Installed, ContentReaders.ReadTemplate(document), document);

    private static (Sequence Sequence, Clip Title) OneTitle(Action<Clip>? change = null)
    {
        var title = new Clip
        {
            Id = "t", Start = Time.FromSeconds(2), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(4),
            Generator = new ClipGenerator { Text = "Hola", Style = TitleStyles.Default() },
        };
        change?.Invoke(title);
        var sequence = new Sequence { Id = "s", Name = "S", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        sequence.VideoTracks.Add(new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" });
        sequence.VideoTracks.Add(new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2", Clips = [title] });
        return (sequence, title);
    }

    private static IReadOnlyList<TemplateFieldChoice> AllExposed(IReadOnlyList<Clip> titles) =>
        [.. TemplateAuthoring.Fields(titles).Select(f => f with { Exposed = true })];

    [Fact]
    public void El_tamano_expuesto_no_baja_de_lo_que_admite_el_borde()
    {
        (Sequence sequence, Clip title) = OneTitle(t =>
        {
            t.Generator!.Style!.Size = 72;
            t.Generator.Style.Stroke = new StyleStroke { Color = "#000000", Width = 6 };
        });

        TemplateDocument document = ContentReaders.ReadTemplate(TemplateAuthoring.Build("Con borde", sequence, [title], AllExposed([title])));

        TemplateParameter size = document.Parameters.Single(p => p.Type == TemplateParameterType.Number);
        Assert.Equal(12, size.Min);
        var smallest = TemplateInstances.With(new Dictionary<string, JsonElement>(), size.Id, JsonValue.Create(12));
        Assert.Equal(12, TemplateInstances.Layers(document, smallest, null)[0].Style.Size);

        (_, Clip thick) = OneTitle(t =>
        {
            t.Generator!.Style!.Size = 100;
            t.Generator.Style.Stroke = new StyleStroke { Color = "#000000", Width = 50 };
            t.Generator.Style.ExtraStrokes = [new StyleStroke { Color = "#FFFFFF", Width = 50 }];
            t.Generator.Style.Size = 90;
        });
        Assert.DoesNotContain(TemplateAuthoring.Fields([thick]), f => f.Field == TemplateField.Size);
    }

    [Theory]
    [InlineData("Árbol Ñandú", "arbol-nandu")]
    [InlineData("CON", "con-1")]
    [InlineData("¿¡!?", "elemento")]
    public void El_id_pliega_mayusculas_con_tilde_y_evita_nombres_reservados(string name, string expected)
    {
        Assert.Equal(expected, TemplateAuthoring.Identifier(name));
    }

    [Fact]
    public void Un_nombre_de_80_caracteres_se_guarda_dos_veces_se_exporta_y_se_instala()
    {
        (Sequence sequence, Clip title) = OneTitle();
        string name = string.Join(' ', Enumerable.Repeat("Rótulo", 12))[..80];
        var locations = PackageLocations.Under(_folder.File("complementos"));
        string json = TemplateAuthoring.Build(name, sequence, [title], TemplateAuthoring.Fields([title]));

        TemplateAuthoring.Save(locations.Own, name, json);
        (_, string reference) = TemplateAuthoring.Save(locations.Own, name, json);

        Assert.True(PackageReference.TryParse(reference, out PackageReference? parsed));
        Assert.EndsWith("-2", parsed!.ItemId, StringComparison.Ordinal);
        MotionTemplateEntry own = MotionTemplateCatalog.Find(PackageLibrary.Scan(locations, "rodaje"), reference)!;
        string archive = _folder.File("largo.paquete");
        File.WriteAllBytes(archive, TemplateAuthoring.Export(own, "Ana"));
        InstallResult result = new PackageInstaller(PackageLocations.Under(_folder.File("otro")), "rodaje").Install(archive);
        Assert.True(result.Package.Id.Length <= Identifiers.MaxLength);
        Assert.Single(result.Package.Manifest.Items);
    }

    [Fact]
    public void Actualizar_restablece_un_valor_cuyo_parametro_cambio_de_tipo()
    {
        MotionTemplateEntry old = Entry(TemplateSamples.Barra);
        var values = new Dictionary<string, JsonElement> { ["acento"] = TemplateSamples.Json("\"#2A9D8F\""), ["nombre"] = TemplateSamples.Json("\"Ana\"") };
        GeneratorTemplate current = TemplateInstances.Create(old.Document, old.DocumentText, old.ProjectSource, old.Name, values);
        string retyped = TemplateSamples.Barra.Replace(
            """{ "id": "acento", "label": "Color de acento", "type": "color", "targets": ["layers/nombre/style/background/color"] }""",
            """{ "id": "acento", "label": "Lema", "type": "text", "targets": ["layers/cargo/text"] }""", StringComparison.Ordinal);
        Assert.NotEqual(TemplateSamples.Barra, retyped);

        (GeneratorTemplate updated, IReadOnlyList<string> reset) = TemplateInstances.Update(current, Entry(retyped, "rodaje-plantillas@1.1.0/barra"));

        Assert.Equal(["Color de acento"], reset);
        Assert.Equal(["nombre"], updated.Values.Keys);
        Assert.Equal(("Ana", "Cargo"), (updated.Layers[0].Text, updated.Layers[1].Text));
    }

    [Fact]
    public void Actualizar_restablece_el_valor_que_choca_con_otro_conservado_en_vez_de_fallar()
    {
        // El tamaño 20 y el grosor 30 valen cada uno con los valores por defecto del otro, pero no juntos (borde ≤ tamaño / 2).
        var root = JsonNode.Parse(TemplateSamples.Barra)!.AsObject();
        root["body"]!["layers"]![0]!["style"]!["stroke"] = new JsonObject { ["color"] = "#000000", ["width"] = 2 };
        root["parameters"]!.AsArray().Add((JsonNode)new JsonObject
        {
            ["id"] = "grosor", ["label"] = "Grosor", ["type"] = "number", ["min"] = 0, ["max"] = 100,
            ["targets"] = new JsonArray("layers/nombre/style/stroke/width"),
        });
        string document = root.ToJsonString();
        MotionTemplateEntry entry = Entry(document);
        var current = new GeneratorTemplate
        {
            Source = entry.Reference, Name = entry.Name, Document = document,
            Values = new Dictionary<string, JsonElement> { ["tamano"] = TemplateSamples.Json("20"), ["grosor"] = TemplateSamples.Json("30") },
        };

        (GeneratorTemplate updated, IReadOnlyList<string> reset) = TemplateInstances.Update(current, Entry(document, "rodaje-plantillas@1.1.0/barra"));

        Assert.Equal(["Grosor"], reset);
        Assert.Equal((20.0, 2.0), (updated.Layers[0].Style.Size, updated.Layers[0].Style.Stroke!.Width));
    }

    [Fact]
    public void En_la_sesion_un_valor_igual_no_es_un_paso_y_los_seguidos_del_mismo_parametro_se_funden() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        MotionTemplateEntry entry = Entry(TemplateSamples.Barra);
        Assert.True(session.AddTemplate(entry));
        string first = session.SelectedTemplate!.Id;
        Assert.True(session.AddTemplate(entry, at: Time.FromSeconds(20)));
        string second = session.SelectedTemplate!.Id;
        session.BreakMerge();
        int steps = session.History.Count;

        Assert.False(session.SetTemplateValue(first, "nombre", JsonValue.Create("Nombre Apellido")!));
        Assert.False(session.SetTemplateValue(first, "acento", JsonValue.Create("#E63946")!));
        Assert.Equal(steps, session.History.Count);

        Assert.True(session.SetTemplateValue(first, "nombre", JsonValue.Create("A")!));
        Assert.True(session.SetTemplateValue(first, "nombre", JsonValue.Create("An")!));
        Assert.True(session.SetTemplateValue(first, "nombre", JsonValue.Create("Ana")!));
        Assert.Equal(steps + 1, session.History.Count);

        // Otro parámetro y el mismo parámetro en otro clip son pasos aparte.
        Assert.True(session.SetTemplateValue(first, "cargo", JsonValue.Create("Directora")!));
        Assert.True(session.SetTemplateValue(second, "cargo", JsonValue.Create("Productora")!));
        Assert.Equal(steps + 3, session.History.Count);

        session.Undo();
        session.Undo();
        session.Undo();
        Assert.Equal("Nombre Apellido", session.Sequence.GetClip(first).Clip.Generator!.Template!.Layers[0].Text);

        Assert.True(session.ConvertTemplateToTitles(second));
        Assert.Null(session.Sequence.FindClip(second));
        Assert.Equal(2, session.SelectedClipIds.Count);
    });

    [Fact]
    public void En_la_sesion_actualizar_es_un_paso_que_se_deshace() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTemplate(Entry(TemplateSamples.Barra)));
        string id = session.SelectedTemplate!.Id;
        Assert.True(session.SetTemplateValue(id, "cargo", JsonValue.Create("Directora")!));
        int steps = session.History.Count;

        IReadOnlyList<string>? reset = session.UpdateTemplate(id, Entry(TemplateSamples.BarraV2, "rodaje-plantillas@1.1.0/barra"));

        Assert.Equal(["Cargo"], reset);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("rodaje-plantillas@1.1.0/barra", session.Sequence.GetClip(id).Clip.Generator!.Template!.Source);
        session.Undo();
        Assert.Equal("rodaje-plantillas@1.0.0/barra", session.Sequence.GetClip(id).Clip.Generator!.Template!.Source);
    });

    [Fact]
    public void Un_borde_con_la_animacion_desactivada_no_la_lleva_y_el_minimo_cuenta_solo_las_activas()
    {
        static StyleAnimation Fade(double seconds) => new()
        {
            DurationSeconds = seconds,
            Curves = { ["opacity"] = [new StyleKey(0, 0), new StyleKey(seconds, 1)] },
        };
        (Sequence sequence, Clip title) = OneTitle(t =>
        {
            t.Generator!.Style!.In = Fade(0.5);
            t.Generator.Style.Out = Fade(0.75);
            t.Generator.Edges.Out = false;
        });

        TemplateDocument document = ContentReaders.ReadTemplate(TemplateAuthoring.Build("Entrada", sequence, [title], TemplateAuthoring.Fields([title])));
        TemplateLayer layer = TemplateInstances.Layers(document, new Dictionary<string, JsonElement>(), null)[0];

        Assert.Equal(0.5, document.MinSeconds);
        Assert.Equal(0.5, layer.Style.In!.DurationSeconds);
        Assert.Null(layer.Style.Out);
        Assert.NotNull(title.Generator!.Style!.Out);
    }

    [Fact]
    public void Titulos_que_abarcan_mas_de_diez_minutos_se_rechazan_con_un_mensaje_claro()
    {
        (Sequence sequence, Clip title) = OneTitle();
        var far = new Clip
        {
            Id = "lejos", Start = Time.FromSeconds(700), SourceIn = Time.Zero, SourceOut = Time.FromSeconds(4),
            Generator = new ClipGenerator { Text = "Lejos", Style = TitleStyles.Default() },
        };
        sequence.VideoTracks[1].Clips.Add(far);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => TemplateAuthoring.Build("Largo", sequence, [title, far], TemplateAuthoring.Fields([title, far])));

        Assert.Contains("10 minutos", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Una_plantilla_mas_larga_que_el_hueco_de_la_pista_no_cabe_en_ella()
    {
        (Sequence sequence, _) = OneTitle();
        Track track = sequence.VideoTracks[1];

        Assert.True(TemplateDrops.FitsIn(track, Time.FromSeconds(6), 5));
        Assert.True(TemplateDrops.FitsIn(track, Time.FromSeconds(6), 8));
        Assert.False(TemplateDrops.FitsIn(track, Time.Zero, 8));
        Assert.True(TemplateDrops.FitsIn(track, Time.Zero, 2));
        Assert.False(TemplateDrops.FitsIn(null, Time.Zero, 2));
    }

    [Fact]
    public void Guardar_la_misma_tira_varias_veces_no_deja_temporales()
    {
        var strip = new TemplateStrip(2, 1, 2, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

        Parallel.For(0, 8, _ => TemplateThumbnails.Save(_folder.Path, "documento", strip));

        Assert.Equal([Path.GetFileName(TemplateThumbnails.CachePath(_folder.Path, "documento"))], Directory.GetFiles(_folder.Path).Select(Path.GetFileName));
        Assert.Equal(strip.Rgba, TemplateThumbnails.Load(_folder.Path, "documento")!.Rgba);
    }
}
