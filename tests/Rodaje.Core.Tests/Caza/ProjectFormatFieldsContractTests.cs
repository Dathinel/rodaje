using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Qué significa cada campo» de <c>docs/formato-proyecto.md</c>, comprobado al leer y escribir un
/// <c>.rodaje</c>. Cada prueba cita la frase.
/// </summary>
public sealed class ProjectFormatFieldsContractTests
{
    private const long Second = Time.TicksPerSecond;

    /// <summary>Un proyecto del formato actual: video y audio enlazados, dos clips de video seguidos y un audio suelto.</summary>
    private static JsonObject Root() => JsonNode.Parse($$"""
        {
          "schemaVersion": {{Project.CurrentSchemaVersion}},
          "id": "project-1",
          "name": "Proyecto",
          "media": [
            { "id": "media-1", "path": "material/toma.mp4", "name": "toma", "duration": {{10 * Second}},
              "hasVideo": true, "hasAudio": true, "frameRate": { "num": 30, "den": 1 },
              "width": 1920, "height": 1080, "sampleRate": 48000, "audioChannels": 2 },
            { "id": "media-2", "path": "música/fondo.wav", "name": "fondo", "duration": {{10 * Second}},
              "hasVideo": false, "hasAudio": true, "frameRate": null,
              "width": 0, "height": 0, "sampleRate": 48000, "audioChannels": 2 }
          ],
          "sequences": [
            { "id": "sequence-1", "name": "Secuencia 1", "frameRate": { "num": 30, "den": 1 },
              "width": 1920, "height": 1080, "sampleRate": 48000,
              "videoTracks": [
                { "id": "track-1", "kind": "video", "name": "Video 1", "muted": false, "hidden": false, "locked": false,
                  "clips": [
                    { "id": "clip-1", "mediaId": "media-1", "sourceIn": 0, "sourceOut": {{2 * Second}}, "start": 0,
                      "linkId": "link-1", "enabled": true, "effects": [] },
                    { "id": "clip-2", "mediaId": "media-1", "sourceIn": {{4 * Second}}, "sourceOut": {{6 * Second}},
                      "start": {{2 * Second}}, "linkId": null, "enabled": true, "effects": [] }
                  ],
                  "effects": [], "transitions": [] }
              ],
              "audioTracks": [
                { "id": "track-2", "kind": "audio", "name": "Audio 1", "muted": false, "hidden": false, "locked": false,
                  "clips": [
                    { "id": "clip-3", "mediaId": "media-1", "sourceIn": 0, "sourceOut": {{2 * Second}}, "start": 0,
                      "linkId": "link-1", "enabled": true, "effects": [] }
                  ],
                  "effects": [], "transitions": [] }
              ],
              "markers": [], "regions": [] }
          ]
        }
        """)!.AsObject();

    private static JsonObject Sequence(JsonObject root, int index = 0) => root["sequences"]![index]!.AsObject();

    private static JsonObject VideoTrack(JsonObject root) => Sequence(root)["videoTracks"]![0]!.AsObject();

    private static JsonObject AudioTrack(JsonObject root) => Sequence(root)["audioTracks"]![0]!.AsObject();

    private static JsonObject Clip(JsonObject root, int index = 0) => VideoTrack(root)["clips"]![index]!.AsObject();

    private static Project Read(JsonObject root) => ProjectSerializer.Deserialize(root.ToJsonString());

    private static JsonObject Write(Project project) => JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();

    private static JsonObject RoundTrip(JsonObject root) => Write(Read(root));

    private static ProjectFormatException Rejected(JsonObject root) =>
        Assert.Throws<ProjectFormatException>(() => Read(root));

    private static JsonObject Effect(string id, string type, string parameters = "{}", string options = "{}") =>
        JsonNode.Parse($$"""{ "id": "{{id}}", "type": "{{type}}", "enabled": true, "parameters": {{parameters}}, "options": {{options}} }""")!.AsObject();

    private static JsonObject WithClipEffect(JsonObject effect)
    {
        JsonObject root = Root();
        Clip(root)["effects"]!.AsArray().Add(effect);
        return root;
    }

    private static JsonObject Title(string id, long sourceIn, long sourceOut, long start, string text = "Hola")
    {
        JsonObject clip = JsonNode.Parse($$"""
            { "id": "{{id}}", "sourceIn": {{sourceIn}}, "sourceOut": {{sourceOut}}, "start": {{start}}, "linkId": null, "enabled": true,
              "generator": { "kind": "text", "text": "",
                "style": { "font": { "family": "Inter", "weight": 400, "italic": false }, "size": 96,
                           "reference": { "width": 1920, "height": 1080 }, "lineHeight": 1.1, "letterSpacing": 0,
                           "align": "center", "fill": { "color": "#FFFFFF" } } },
              "effects": [] }
            """)!.AsObject();
        clip["generator"]!["text"] = text;
        return clip;
    }

    // «`schemaVersion` es la versión del formato y hoy vale 13; es el primer dato que se lee»
    [Fact]
    public void SchemaVersion_vale_13_y_es_el_primer_dato()
    {
        Assert.Equal(13, Project.CurrentSchemaVersion);

        JsonObject written = RoundTrip(Root());

        Assert.Equal("schemaVersion", written.First().Key);
        Assert.Equal(13, (int)written["schemaVersion"]!);
    }

    // «`id` identifica al proyecto y `name` es el nombre que ve quien edita.»
    [Fact]
    public void Id_y_name_del_proyecto_se_conservan()
    {
        JsonObject root = Root();
        root["name"] = "Vacaciones — ñ";

        Project project = Read(root);

        Assert.Equal("project-1", project.Id);
        Assert.Equal("Vacaciones — ñ", project.Name);
    }

    // «`sequences` son las líneas de tiempo, que pueden ser varias, por ejemplo una horizontal y otra vertical del mismo
    // material.»
    [Fact]
    public void Puede_haber_varias_secuencias_horizontal_y_vertical()
    {
        JsonObject root = Root();
        JsonObject vertical = Sequence(root).DeepClone().AsObject();
        vertical["id"] = "sequence-2";
        vertical["width"] = 1080;
        vertical["height"] = 1920;
        foreach (JsonNode? list in new[] { vertical["videoTracks"], vertical["audioTracks"] })
        {
            foreach (JsonNode? track in list!.AsArray())
            {
                track!["id"] = track["id"]!.GetValue<string>() + "-v";
                foreach (JsonNode? clip in track["clips"]!.AsArray())
                {
                    clip!["id"] = clip["id"]!.GetValue<string>() + "-v";
                    if (clip["linkId"] is not null)
                    {
                        clip["linkId"] = "link-v";
                    }
                }
            }
        }

        root["sequences"]!.AsArray().Add(vertical);

        Project project = Read(root);

        Assert.Equal(["sequence-1", "sequence-2"], project.Sequences.Select(s => s.Id));
        Assert.Equal((1080, 1920), (project.Sequences[1].Width, project.Sequences[1].Height));
    }

    // «`path` es su ruta; se prefiere relativa a la carpeta donde está el `.rodaje` … aunque se aceptan rutas absolutas.
    // Se admiten tanto `/` como `\` como separador.»
    [Theory]
    [InlineData("material/toma.mp4")]
    [InlineData(@"material\toma.mp4")]
    [InlineData(@"material/sub\toma.mp4")]
    [InlineData(@"C:\Videos\toma.mp4")]
    [InlineData("C:/Videos/toma.mp4")]
    [InlineData("/home/quien/toma.mp4")]
    public void La_ruta_puede_ser_relativa_o_absoluta_con_cualquier_separador(string path)
    {
        JsonObject root = Root();
        root["media"]![0]!["path"] = path;

        Project project = Read(root);

        Assert.Equal(path, project.Media[0].Path);
    }

    // «`frameRate` es la tasa de su video o `null` si no tiene (una imagen fija o un archivo de solo audio) … Los campos
    // que no aplican valen 0.»
    [Fact]
    public void Un_medio_de_solo_audio_tiene_frameRate_null_y_dimensiones_0()
    {
        JsonObject media = RoundTrip(Root())["media"]![1]!.AsObject();

        Assert.True(media.ContainsKey("frameRate"));
        Assert.Null(media["frameRate"]);
        Assert.Equal(0, (int)media["width"]!);
        Assert.Equal(0, (int)media["height"]!);
        Assert.False((bool)media["hasVideo"]!);
        Assert.True((bool)media["hasAudio"]!);
    }

    // «`duration` la duración total del archivo en ticks»: un clip no puede pasar del final de su medio.
    [Fact]
    public void Un_clip_no_pasa_de_la_duracion_del_medio()
    {
        JsonObject root = Root();
        root["media"]![0]!["duration"] = Second;

        Assert.Contains("solo dura", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Las pistas van en dos listas separadas, `videoTracks` y `audioTracks`, y el orden dentro de cada lista importa: en
    // `videoTracks` el primer elemento es la capa de más abajo»
    [Fact]
    public void El_orden_de_las_pistas_se_conserva()
    {
        JsonObject root = Root();
        JsonArray tracks = Sequence(root)["videoTracks"]!.AsArray();
        foreach (string id in new[] { "track-a", "track-b" })
        {
            tracks.Add(JsonNode.Parse($$"""{ "id": "{{id}}", "kind": "video", "name": "{{id}}", "clips": [], "effects": [], "transitions": [] }"""));
        }

        JsonArray written = RoundTrip(root)["sequences"]![0]!["videoTracks"]!.AsArray();

        Assert.Equal(["track-1", "track-a", "track-b"], written.Select(t => t!["id"]!.GetValue<string>()));
    }

    // «`kind` (que vale `"video"` o `"audio"` y debe coincidir con la lista en la que está)»
    [Fact]
    public void Una_pista_en_la_lista_equivocada_se_rechaza()
    {
        JsonObject root = Root();
        VideoTrack(root)["kind"] = "audio";

        Assert.Contains("lista equivocada", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «tres interruptores: `muted` la silencia, `hidden` la oculta de la imagen y `locked` impide editarla.»
    [Fact]
    public void Los_tres_interruptores_de_la_pista_se_conservan()
    {
        JsonObject root = Root();
        VideoTrack(root)["muted"] = true;
        VideoTrack(root)["hidden"] = true;
        VideoTrack(root)["locked"] = true;

        Track track = Read(root).Sequences[0].VideoTracks[0];

        Assert.True(track.Muted);
        Assert.True(track.Hidden);
        Assert.True(track.Locked);
    }

    // «Su lista `clips` está ordenada por posición»
    [Fact]
    public void Clips_fuera_de_orden_se_rechazan()
    {
        JsonObject root = Root();
        JsonArray clips = VideoTrack(root)["clips"]!.AsArray();
        JsonNode first = clips[0]!;
        clips.RemoveAt(0);
        clips.Add(first);

        Assert.Contains("fuera de orden", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «`markers` guarda los marcadores, cada uno con `id`, el instante `time` y un `name` que puede ir vacío»
    [Fact]
    public void Un_marcador_puede_tener_el_nombre_vacio()
    {
        JsonObject root = Root();
        Sequence(root)["markers"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "marker-1", "time": {{Second}}, "name": "" }"""));

        Marker marker = Assert.Single(Read(root).Sequences[0].Markers);

        Assert.Equal("", marker.Name);
        Assert.Equal(Time.FromTicks(Second), marker.Time);
    }

    // «`regions` guarda las regiones, tramos con nombre `[start, end)` que pueden solaparse»
    [Fact]
    public void Las_regiones_pueden_solaparse()
    {
        JsonObject root = Root();
        JsonArray regions = Sequence(root)["regions"]!.AsArray();
        regions.Add(JsonNode.Parse($$"""{ "id": "region-1", "start": 0, "end": {{3 * Second}}, "name": "A" }"""));
        regions.Add(JsonNode.Parse($$"""{ "id": "region-2", "start": {{Second}}, "end": {{4 * Second}}, "name": "B" }"""));

        Assert.Equal(2, Read(root).Sequences[0].Regions.Count);
    }

    // «tramos con nombre `[start, end)`»: un tramo vacío no es una región.
    [Fact]
    public void Una_region_sin_duracion_se_rechaza()
    {
        JsonObject root = Root();
        Sequence(root)["regions"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "region-1", "start": {{Second}}, "end": {{Second}}, "name": "" }"""));

        Assert.Contains("duración cero", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Los dos admiten `color` (`#RRGGBB`) y `note` (texto libre de hasta 2000 caracteres), que solo se escriben si
    // tienen valor»
    [Fact]
    public void Color_y_nota_solo_se_escriben_si_tienen_valor()
    {
        JsonObject root = Root();
        JsonArray markers = Sequence(root)["markers"]!.AsArray();
        markers.Add(JsonNode.Parse("""{ "id": "marker-1", "time": 0, "name": "simple" }"""));
        markers.Add(JsonNode.Parse("""{ "id": "marker-2", "time": 1, "name": "con todo", "color": "#12AB34", "note": "nota" }"""));
        Sequence(root)["regions"]!.AsArray().Add(JsonNode.Parse("""{ "id": "region-1", "start": 0, "end": 10, "name": "" }"""));

        JsonObject written = Sequence(RoundTrip(root));
        JsonObject plain = written["markers"]![0]!.AsObject();
        JsonObject full = written["markers"]![1]!.AsObject();
        JsonObject region = written["regions"]![0]!.AsObject();

        Assert.False(plain.ContainsKey("color"));
        Assert.False(plain.ContainsKey("note"));
        Assert.False(region.ContainsKey("color"));
        Assert.False(region.ContainsKey("note"));
        Assert.Equal("#12AB34", (string)full["color"]!);
        Assert.Equal("nota", (string)full["note"]!);
    }

    // «`note` (texto libre de hasta 2000 caracteres)»
    [Theory]
    [InlineData("markers", 2000, true)]
    [InlineData("markers", 2001, false)]
    [InlineData("regions", 2000, true)]
    [InlineData("regions", 2001, false)]
    public void La_nota_admite_hasta_2000_caracteres(string list, int length, bool accepted)
    {
        JsonObject root = Root();
        JsonObject item = list == "markers"
            ? JsonNode.Parse("""{ "id": "x-1", "time": 0, "name": "" }""")!.AsObject()
            : JsonNode.Parse("""{ "id": "x-1", "start": 0, "end": 10, "name": "" }""")!.AsObject();
        item["note"] = new string('n', length);
        Sequence(root)[list]!.AsArray().Add(item);

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("nota de más de 2000", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «Los dos admiten `color` (`#RRGGBB`)»
    [Theory]
    [InlineData("#12AB3")]
    [InlineData("12AB34")]
    [InlineData("#12AB34FF")]
    [InlineData("#GGGGGG")]
    public void Un_color_que_no_es_RRGGBB_se_rechaza(string color)
    {
        JsonObject root = Root();
        Sequence(root)["markers"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "marker-1", "time": 0, "name": "", "color": "{{color}}" }"""));

        Assert.Contains("no es #RRGGBB", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «`sourceIn` es el punto de entrada dentro del archivo de origen, incluido; `sourceOut` es el punto de salida,
    // excluido; y `start` es el lugar de la pista donde se coloca el clip. Su duración es `sourceOut − sourceIn` y su
    // final en la pista es `start + duración`.»
    [Fact]
    public void Duracion_y_final_salen_de_los_tres_instantes()
    {
        Clip clip = Read(Root()).Sequences[0].VideoTracks[0].Clips[1];

        Assert.Equal(Time.FromTicks(2 * Second), clip.Duration);
        Assert.Equal(Time.FromTicks(4 * Second), clip.End);
    }

    // «Su final en la pista es `start + duración`»: dos clips que se tocan no se solapan, porque `sourceOut` es excluido.
    [Fact]
    public void Dos_clips_que_se_tocan_no_se_solapan()
    {
        JsonObject root = Root();
        Clip(root, 1)["start"] = 2 * Second - 1;

        Assert.Contains("se solapa", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Estos dos valores no se guardan porque se calculan, y un programa externo no debe escribirlos.»
    [Fact]
    public void Duracion_y_final_no_se_guardan()
    {
        JsonObject clip = RoundTrip(Root())["sequences"]![0]!["videoTracks"]![0]!["clips"]![1]!.AsObject();

        Assert.False(clip.ContainsKey("duration"));
        Assert.False(clip.ContainsKey("end"));
    }

    // «`mediaId` dice qué medio usa»
    [Fact]
    public void Un_mediaId_que_no_existe_se_rechaza()
    {
        JsonObject root = Root();
        Clip(root)["mediaId"] = "media-9";

        Assert.Contains("que no existe", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «`enabled` permite apagar un clip sin borrarlo»
    [Fact]
    public void Un_clip_apagado_se_conserva()
    {
        JsonObject root = Root();
        Clip(root, 1)["enabled"] = false;

        JsonObject written = RoundTrip(root);

        Assert.False((bool)written["sequences"]![0]!["videoTracks"]![0]!["clips"]![1]!["enabled"]!);
    }

    // «`linkId`, si no es `null`, agrupa clips que se editan juntos … El valor de `linkId` es un texto cualquiera que
    // comparten los clips del grupo y no tiene que aparecer en ningún otro lugar.»
    [Fact]
    public void LinkId_es_un_texto_cualquiera()
    {
        JsonObject root = Root();
        const string link = "toma ñ — #1 {x}";
        Clip(root)["linkId"] = link;
        AudioTrack(root)["clips"]![0]!["linkId"] = link;

        Project project = Read(root);

        Assert.Equal(link, project.Sequences[0].VideoTracks[0].Clips[0].LinkId);
        Assert.Equal(link, project.Sequences[0].AudioTracks[0].Clips[0].LinkId);
        Assert.Null(project.Sequences[0].VideoTracks[0].Clips[1].LinkId);
    }

    // «Los efectos de un clip están en `effects` y se aplican en el orden de la lista.»
    [Fact]
    public void Los_efectos_conservan_su_orden()
    {
        JsonObject root = Root();
        JsonArray effects = Clip(root)["effects"]!.AsArray();
        effects.Add(Effect("effect-c", "rodaje.vignette"));
        effects.Add(Effect("effect-a", "rodaje.transform"));
        effects.Add(Effect("effect-b", "rodaje.color"));

        JsonArray written = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]!.AsArray();

        Assert.Equal(["effect-c", "effect-a", "effect-b"], written.Select(e => e!["id"]!.GetValue<string>()));
    }

    // «un diccionario `parameters` cuyas claves son los nombres de los parámetros. Un programa no debe depender del orden
    // de esas claves.»
    [Fact]
    public void El_orden_de_las_claves_de_parameters_no_cambia_el_proyecto()
    {
        JsonObject a = WithClipEffect(Effect("effect-1", "rodaje.transform",
            """{ "x": { "value": 10, "keyframes": [] }, "scale": { "value": 2, "keyframes": [] } }"""));
        JsonObject b = WithClipEffect(Effect("effect-1", "rodaje.transform",
            """{ "scale": { "value": 2, "keyframes": [] }, "x": { "value": 10, "keyframes": [] } }"""));

        EffectInstance fromA = Read(a).Sequences[0].VideoTracks[0].Clips[0].Effects[0];
        EffectInstance fromB = Read(b).Sequences[0].VideoTracks[0].Clips[0].Effects[0];

        Assert.Equal(fromA.Parameters["x"].Value, fromB.Parameters["x"].Value);
        Assert.Equal(fromA.Parameters["scale"].Value, fromB.Parameters["scale"].Value);
    }

    // «Cada keyframe tiene `time`, `value` e `interpolation` … `hold` …, `linear` …, `easeIn` …, `easeOut` …, `easeInOut`
    // …, `bounce` …, `elastic` … y `bezier`»
    [Theory]
    [InlineData("hold", Interpolation.Hold)]
    [InlineData("linear", Interpolation.Linear)]
    [InlineData("easeIn", Interpolation.EaseIn)]
    [InlineData("easeOut", Interpolation.EaseOut)]
    [InlineData("easeInOut", Interpolation.EaseInOut)]
    [InlineData("bounce", Interpolation.Bounce)]
    [InlineData("elastic", Interpolation.Elastic)]
    [InlineData("bezier", Interpolation.Bezier)]
    public void Cada_interpolacion_se_lee_y_se_escribe_con_su_nombre(string name, Interpolation expected)
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.transform", $$"""
            { "x": { "value": 0, "keyframes": [
                { "time": 0, "value": 0, "interpolation": "{{name}}" },
                { "time": {{Second}}, "value": 100, "interpolation": "linear" } ] } }
            """));

        Project project = Read(root);
        JsonObject written = Write(project);

        Assert.Equal(expected, project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["x"].Keyframes[0].Interpolation);
        Assert.Equal(name, (string)written["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]![0]!["parameters"]!["x"]!["keyframes"]![0]!["interpolation"]!);
    }

    // «si falta, vale `linear`.»
    [Fact]
    public void Sin_interpolation_vale_linear()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.transform", $$"""
            { "x": { "value": 0, "keyframes": [ { "time": 0, "value": 0 }, { "time": {{Second}}, "value": 100 } ] } }
            """));

        Parameter x = Read(root).Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["x"];

        Assert.Equal(Interpolation.Linear, x.Keyframes[0].Interpolation);
        Assert.Equal(50, x.Evaluate(Time.FromTicks(Second / 2)), 9);
    }

    // «`hold` mantiene el valor y salta de golpe, `linear` avanza en línea recta, `easeIn` arranca despacio, `easeOut`
    // frena al final, `easeInOut` hace las dos cosas»
    [Fact]
    public void Las_curvas_basicas_hacen_lo_que_dicen()
    {
        Assert.Equal(0, KeyframeMath.Ease(Interpolation.Hold, null, 0.99));
        Assert.Equal(0.3, KeyframeMath.Ease(Interpolation.Linear, null, 0.3));
        Assert.True(KeyframeMath.Ease(Interpolation.EaseIn, null, 0.25) < 0.25);
        Assert.True(KeyframeMath.Ease(Interpolation.EaseOut, null, 0.75) > 0.75);
        Assert.True(KeyframeMath.Ease(Interpolation.EaseInOut, null, 0.25) < 0.25);
        Assert.True(KeyframeMath.Ease(Interpolation.EaseInOut, null, 0.75) > 0.75);
    }

    // «`elastic` se pasa y vuelve como un resorte»
    [Fact]
    public void Elastic_se_pasa_de_1()
    {
        double max = Enumerable.Range(1, 999).Select(i => KeyframeMath.Ease(Interpolation.Elastic, null, i / 1000.0)).Max();

        Assert.True(max > 1, $"El máximo fue {max}.");
        Assert.Equal(1, KeyframeMath.Ease(Interpolation.Elastic, null, 1), 9);
    }

    // «sin `ease`, `bezier` es como `easeInOut`»
    [Theory]
    [InlineData(0.1)]
    [InlineData(0.37)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void Bezier_sin_ease_es_como_easeInOut(double t)
    {
        Assert.Equal(KeyframeMath.Ease(Interpolation.EaseInOut, null, t), KeyframeMath.Ease(Interpolation.Bezier, null, t));
    }

    // «`ease`, los puntos de control `{x1, y1, x2, y2}` de esa curva como `cubic-bezier` de CSS (x de 0 a 1, y de −2 a 3»
    [Theory]
    [InlineData(0, -2, 1, 3, true)]
    [InlineData(0.25, 0.1, 0.25, 1, true)]
    [InlineData(-0.01, 0, 1, 1, false)]
    [InlineData(0, 0, 1.01, 1, false)]
    [InlineData(0, -2.01, 1, 1, false)]
    [InlineData(0, 0, 1, 3.01, false)]
    public void Ease_tiene_x_de_0_a_1_e_y_de_menos_2_a_3(double x1, double y1, double x2, double y2, bool accepted)
    {
        string ease = FormattableString.Invariant($$"""{ "x1": {{x1}}, "y1": {{y1}}, "x2": {{x2}}, "y2": {{y2}} }""");
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.transform", $$"""
            { "x": { "value": 0, "keyframes": [
                { "time": 0, "value": 0, "interpolation": "bezier", "ease": {{ease}} },
                { "time": {{Second}}, "value": 100 } ] } }
            """));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("curva Bézier", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «Antes del primer keyframe el parámetro vale lo del primero y después del último vale lo del último.»
    [Fact]
    public void Fuera_de_los_keyframes_vale_el_del_borde()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.transform", $$"""
            { "x": { "value": 999, "keyframes": [
                { "time": {{Second / 2}}, "value": 10 }, { "time": {{Second}}, "value": 20 } ] } }
            """));

        Parameter x = Read(root).Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["x"];

        Assert.Equal(10, x.Evaluate(Time.Zero));
        Assert.Equal(20, x.Evaluate(Time.FromTicks(2 * Second)));
    }

    // «su `time` está en **tiempo del origen**, no de la línea de tiempo, y en la misma escala que `sourceIn`. Así, si el
    // clip se mueve en la pista, la animación lo acompaña sin recalcular nada»
    [Fact]
    public void Mover_el_clip_no_cambia_sus_keyframes()
    {
        string parameters = $$"""
            { "x": { "value": 0, "keyframes": [ { "time": {{4 * Second}}, "value": 0 }, { "time": {{5 * Second}}, "value": 100 } ] } }
            """;
        JsonObject before = Root();
        Clip(before, 1)["effects"]!.AsArray().Add(Effect("effect-1", "rodaje.transform", parameters));
        JsonObject after = before.DeepClone().AsObject();
        Clip(after, 1)["start"] = 5 * Second;

        Clip moved = Read(after).Sequences[0].VideoTracks[0].Clips[1];
        Clip still = Read(before).Sequences[0].VideoTracks[0].Clips[1];

        Assert.Equal(still.Effects[0].Parameters["x"].Keyframes, moved.Effects[0].Parameters["x"].Keyframes);
        Assert.Equal(50, moved.Effects[0].Parameters["x"].Evaluate(Time.FromTicks(moved.SourceIn.Ticks + (Second / 2))), 9);
    }

    // «Los keyframes … no están en orden estricto»: la animación se lee en orden de tiempo.
    [Fact]
    public void Keyframes_desordenados_se_rechazan()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.transform", $$"""
            { "x": { "value": 0, "keyframes": [ { "time": {{Second}}, "value": 0 }, { "time": 0, "value": 1 } ] } }
            """));

        Assert.Contains("orden estricto", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Lo que no es un número va en `options`, un diccionario de textos: el modo de mezcla, la LUT o el paquete del que
    // salió el efecto.»
    [Fact]
    public void Las_opciones_son_textos_y_se_conservan()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.blend", options: """{ "blendMode": "multiply" }"""));
        Clip(root)["effects"]!.AsArray().Add(Effect("effect-2", "rodaje.color",
            options: """{ "lut": "rodaje-filtros@1.0.0/calido", "source": "rodaje-filtros@1.0.0/calido" }"""));

        JsonArray effects = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]!.AsArray();

        Assert.Equal("multiply", (string)effects[0]!["options"]!["blendMode"]!);
        Assert.Equal("rodaje-filtros@1.0.0/calido", (string)effects[1]!["options"]!["lut"]!);
        Assert.Equal("rodaje-filtros@1.0.0/calido", (string)effects[1]!["options"]!["source"]!);
    }

    // «Un tipo que no está en el registro, por ejemplo de otro programa o de un paquete que falta, se conserva tal cual
    // al guardar, con sus parámetros y opciones, pero no se aplica.»
    [Fact]
    public void Un_tipo_desconocido_se_conserva_con_parametros_y_opciones()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "otro.programa.efecto",
            """{ "fuerza": { "value": 12345, "keyframes": [ { "time": 0, "value": 1, "interpolation": "hold" } ] } }""",
            """{ "modo": "raro", "ruta": "x/y" }"""));

        JsonObject effect = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]![0]!.AsObject();

        Assert.Equal("otro.programa.efecto", (string)effect["type"]!);
        Assert.Equal(12345, (double)effect["parameters"]!["fuerza"]!["value"]!);
        Assert.Equal("hold", (string)effect["parameters"]!["fuerza"]!["keyframes"]![0]!["interpolation"]!);
        Assert.Equal("raro", (string)effect["options"]!["modo"]!);
        Assert.Equal("x/y", (string)effect["options"]!["ruta"]!);
    }

    // «`rodaje.speed` | Reservado y oculto (código 8): … un efecto de este tipo se rechaza»
    [Fact]
    public void Un_efecto_rodaje_speed_se_rechaza()
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.speed"));

        Assert.Contains("reservado", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «En los tipos registrados, una opción definida debe tener un valor aceptado: uno de la lista en las de elección
    // (`blendMode`, `direction`)»
    [Theory]
    [InlineData("multiply", true)]
    [InlineData("colorBurn", true)]
    [InlineData("Multiply", false)]
    [InlineData("burn", false)]
    public void BlendMode_debe_ser_de_la_lista(string value, bool accepted)
    {
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.blend", options: $$"""{ "blendMode": "{{value}}" }"""));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("no es un valor válido", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «en las referencias (`lut`, `preset`, `source`), la forma `paquete@versión/elemento`, con paquete y elemento en
    // minúsculas ASCII, dígitos, guiones y puntos, y una versión que empieza con un dígito. `lut` y `preset` admiten
    // además el texto vacío, y `lut` también `file:` seguido de una ruta sin caracteres de control ni `" < > |`»
    [Theory]
    [InlineData("lut", "rodaje-filtros@1.0.0/calido.v2", true)]
    [InlineData("lut", "", true)]
    [InlineData("lut", "file:luts/calido.cube", true)]
    [InlineData("lut", @"file:C:\luts\calido.cube", true)]
    [InlineData("lut", "Rodaje-filtros@1.0.0/calido", false)]
    [InlineData("lut", "rodaje-filtros@1.0.0/Calido", false)]
    [InlineData("lut", "rodaje-filtros@v1/calido", false)]
    [InlineData("lut", "rodaje-filtros/calido", false)]
    [InlineData("lut", "file:luts/a<b.cube", false)]
    [InlineData("lut", "file:luts/a|b.cube", false)]
    [InlineData("source", "rodaje-filtros@1.0.0/calido", true)]
    [InlineData("source", "", false)]
    [InlineData("source", "file:luts/calido.cube", false)]
    public void Las_referencias_tienen_la_forma_paquete_version_elemento(string option, string value, bool accepted)
    {
        var options = new JsonObject { [option] = value };
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.color", options: options.ToJsonString()));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("no es un valor válido", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «Las de texto (`path`, `matte`, `matteStart`, `target`, `name`, `color`, las curvas) admiten hasta 16 384
    // caracteres sin caracteres de control; `points`, 32 768»
    [Theory]
    [InlineData("name", 16_384, true)]
    [InlineData("name", 16_385, false)]
    [InlineData("matte", 16_385, false)]
    public void Las_opciones_de_texto_tienen_tope(string option, int length, bool accepted)
    {
        var options = new JsonObject { [option] = new string('a', length) };
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.mask", options: options.ToJsonString()));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("no es un valor válido", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «Las de texto … admiten hasta 16 384 caracteres sin caracteres de control»
    [Fact]
    public void Una_opcion_de_texto_con_un_caracter_de_control_se_rechaza()
    {
        var options = new JsonObject { ["name"] = "a\u0001b" };
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.mask", options: options.ToJsonString()));

        Assert.Contains("no es un valor válido", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Las opciones que el tipo no define se conservan sin mirarlas.»
    [Fact]
    public void Las_opciones_no_definidas_se_conservan_sin_mirarlas()
    {
        var options = new JsonObject { ["inventada"] = "a\u0001" + new string('z', 20_000) };
        JsonObject root = WithClipEffect(Effect("effect-1", "rodaje.vignette", options: options.ToJsonString()));

        JsonObject effect = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["effects"]![0]!.AsObject();

        Assert.Equal((string)options["inventada"]!, (string)effect["options"]!["inventada"]!);
    }

    // «Los efectos de una pista … solo admiten tipos de su clase: de audio en una pista de audio y de video en una de
    // video.»
    [Theory]
    [InlineData("video", "rodaje.equalizer")]
    [InlineData("video", "rodaje.audio")]
    [InlineData("audio", "rodaje.transform")]
    [InlineData("audio", "rodaje.color")]
    public void Los_efectos_de_pista_son_de_su_clase(string kind, string type)
    {
        JsonObject root = Root();
        JsonObject track = kind == "video" ? VideoTrack(root) : AudioTrack(root);
        track["effects"]!.AsArray().Add(Effect("effect-1", type));

        Assert.Contains("no se puede aplicar a la pista", Rejected(root).Message, StringComparison.Ordinal);
    }

    // «Los efectos de una pista … de audio en una pista de audio y de video en una de video.»: los de su clase se aceptan.
    [Fact]
    public void Los_efectos_de_pista_de_su_clase_se_aceptan()
    {
        JsonObject root = Root();
        VideoTrack(root)["effects"]!.AsArray().Add(Effect("effect-1", "rodaje.color"));
        AudioTrack(root)["effects"]!.AsArray().Add(Effect("effect-2", "rodaje.audio"));

        Project project = Read(root);

        Assert.Single(project.Sequences[0].VideoTracks[0].Effects);
        Assert.Single(project.Sequences[0].AudioTracks[0].Effects);
    }

    private static JsonObject WithTransition(string transition)
    {
        JsonObject root = Root();
        VideoTrack(root)["transitions"]!.AsArray().Add(JsonNode.Parse(transition));
        return root;
    }

    private static string Transition(long duration, string extra = "") =>
        $$"""{ "id": "transition-1", "type": "rodaje.wipe", "from": "clip-1", "to": "clip-2", "duration": {{duration}}, "parameters": { "angle": 90 }, "options": {} {{extra}} }""";

    // «Una transición vive en la pista y une dos clips seguidos, sin hueco: `from` es el clip que termina en el corte y
    // `to` el que empieza ahí.»
    [Fact]
    public void Una_transicion_une_dos_clips_seguidos()
    {
        Read(WithTransition(Transition(Second / 2)));

        JsonObject gap = WithTransition(Transition(Second / 2));
        Clip(gap, 1)["start"] = 3 * Second;
        Assert.Contains("no une dos clips contiguos", Rejected(gap).Message, StringComparison.Ordinal);

        JsonObject reversed = WithTransition(Transition(Second / 2).Replace("\"clip-1\"", "\"tmp\"").Replace("\"clip-2\"", "\"clip-1\"").Replace("\"tmp\"", "\"clip-2\""));
        Assert.Contains("no une dos clips contiguos", Rejected(reversed).Message, StringComparison.Ordinal);
    }

    // «`center` (por defecto, se omite) pone la mitad antes del corte y la mitad después (un tick impar va después),
    // `before` la pone toda antes … y `after` toda después»
    [Theory]
    [InlineData("", 1001, 500, 501)]
    [InlineData(""", "alignment": "center" """, 1000, 500, 500)]
    [InlineData(""", "alignment": "before" """, 1001, 1001, 0)]
    [InlineData(""", "alignment": "after" """, 1001, 0, 1001)]
    public void La_alineacion_reparte_la_duracion(string extra, long duration, long before, long after)
    {
        TransitionInstance transition = Read(WithTransition(Transition(duration, extra))).Sequences[0].VideoTracks[0].Transitions[0];

        Assert.Equal(before, transition.Before.Ticks);
        Assert.Equal(after, transition.After.Ticks);
    }

    // «`center` (por defecto, se omite)»
    [Fact]
    public void La_alineacion_centrada_no_se_escribe()
    {
        JsonObject written = RoundTrip(WithTransition(Transition(1000, """, "alignment": "center" """)));
        JsonObject transition = written["sequences"]![0]!["videoTracks"]![0]!["transitions"]![0]!.AsObject();

        Assert.False(transition.ContainsKey("alignment"));

        JsonObject after = RoundTrip(WithTransition(Transition(1000, """, "alignment": "after" """)));
        Assert.Equal("after", (string)after["sequences"]![0]!["videoTracks"]![0]!["transitions"]![0]!["alignment"]!);
    }

    // «La parte de antes cabe en el clip que sale y la de después en el que entra … centrada, además, no pasa del más
    // corto de sus dos clips.»
    [Theory]
    [InlineData("", 2, true)]
    [InlineData("", 3, false)]
    [InlineData(""", "alignment": "before" """, 2, true)]
    [InlineData(""", "alignment": "before" """, 3, false)]
    [InlineData(""", "alignment": "after" """, 2, true)]
    [InlineData(""", "alignment": "after" """, 3, false)]
    public void La_transicion_cabe_en_sus_clips(string extra, long seconds, bool accepted)
    {
        JsonObject root = WithTransition(Transition(seconds * Second, extra));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Rejected(root);
        }
    }

    // «Sus `parameters` son números sin keyframes (`{"angle": 90}`) y sus `options`, textos.»
    [Fact]
    public void Los_parametros_de_una_transicion_son_numeros()
    {
        TransitionInstance transition = Read(WithTransition(Transition(Second / 2))).Sequences[0].VideoTracks[0].Transitions[0];
        Assert.Equal(90, transition.Parameters["angle"]);

        JsonObject animated = WithTransition(Transition(Second / 2).Replace("\"angle\": 90", "\"angle\": { \"value\": 90, \"keyframes\": [] }"));
        Rejected(animated);
    }

    // «`curve` (formato 12, opcional) es la curva del avance con los nombres de las interpolaciones de los keyframes …;
    // sin ella o con `linear` el avance es lineal. … El avance vale exactamente 0 y 1 en los extremos y se recorta a 0–1
    // en medio.»
    [Theory]
    [InlineData(null)]
    [InlineData(Interpolation.Linear)]
    [InlineData(Interpolation.EaseIn)]
    [InlineData(Interpolation.Elastic)]
    [InlineData(Interpolation.Bounce)]
    [InlineData(Interpolation.Bezier)]
    public void El_avance_de_la_transicion_vale_0_y_1_en_los_extremos_y_no_sale_de_0_a_1(Interpolation? curve)
    {
        var ease = new BezierEase(0.5, -2, 0.5, 3);
        Assert.Equal(0, TransitionInstance.EaseProgress(curve, ease, 0));
        Assert.Equal(1, TransitionInstance.EaseProgress(curve, ease, 1));
        for (int i = 1; i < 100; i++)
        {
            double t = i / 100.0;
            double value = TransitionInstance.EaseProgress(curve, ease, t);
            Assert.InRange(value, 0, 1);
            if (curve is null or Interpolation.Linear)
            {
                Assert.Equal(t, value);
            }
        }
    }

    // «`curve` … con los nombres de las interpolaciones de los keyframes»: un nombre desconocido no se acepta.
    [Fact]
    public void Una_curva_de_transicion_desconocida_se_rechaza()
    {
        Rejected(WithTransition(Transition(Second / 2, """, "curve": "rara" """)));
        Read(WithTransition(Transition(Second / 2, """, "curve": "easeInOut" """)));
    }

    // «Un clip puede llevar `speed`; si no lo lleva (o es `null`) se reproduce a 1× … sin `speed`, un `duration` escrito
    // se ignora.»
    [Fact]
    public void Sin_speed_un_duration_escrito_se_ignora()
    {
        JsonObject root = Root();
        Clip(root, 1)["speed"] = null;
        Clip(root, 1)["duration"] = 7 * Second;

        Clip clip = Read(root).Sequences[0].VideoTracks[0].Clips[1];

        Assert.Null(clip.Speed);
        Assert.Equal(Time.FromTicks(2 * Second), clip.Duration);
    }

    // «Un clip puede llevar `generator` en lugar de `mediaId` … `sourceIn` puede ser negativo (al alargar el inicio de
    // un título). Va solo en pistas de video, sin `speed`, y `mediaId` no se escribe.»
    [Fact]
    public void Un_titulo_no_escribe_mediaId_y_admite_sourceIn_negativo()
    {
        JsonObject root = Root();
        VideoTrack(root)["clips"]!.AsArray().Add(Title("clip-9", -Second, Second, 5 * Second));

        JsonObject clip = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![2]!.AsObject();

        Assert.False(clip.ContainsKey("mediaId"));
        Assert.Equal(-Second, (long)clip["sourceIn"]!);
        Assert.Equal("text", (string)clip["generator"]!["kind"]!);
    }

    // «Va solo en pistas de video»
    [Fact]
    public void Un_titulo_en_una_pista_de_audio_se_rechaza()
    {
        JsonObject root = Root();
        AudioTrack(root)["clips"]!.AsArray().Add(Title("clip-9", 0, Second, 5 * Second));

        Rejected(root);
    }

    // «`kind` es abierto … y un tipo desconocido se conserva al guardar y no se dibuja.»
    [Fact]
    public void Un_generador_de_tipo_desconocido_se_conserva()
    {
        JsonObject root = Root();
        JsonObject clip = Title("clip-9", 0, Second, 5 * Second);
        clip["generator"]!["kind"] = "solid";
        clip["generator"]!.AsObject().Remove("style");
        VideoTrack(root)["clips"]!.AsArray().Add(clip);

        JsonObject written = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![2]!.AsObject();

        Assert.Equal("solid", (string)written["generator"]!["kind"]!);
    }

    // «`text` | El texto. … Hasta 10 000 caracteres y 200 líneas; los separadores de línea y de párrafo de Unicode
    // (U+2028, U+2029) también cuentan como salto.»
    [Theory]
    [InlineData(10_000, '\n', 0, true)]
    [InlineData(10_001, '\n', 0, false)]
    [InlineData(400, '\n', 199, true)]
    [InlineData(400, '\n', 200, false)]
    [InlineData(400, '\u2028', 200, false)]
    [InlineData(400, '\u2029', 200, false)]
    public void El_texto_tiene_hasta_10000_caracteres_y_200_lineas(int length, char separator, int breaks, bool accepted)
    {
        char[] text = Enumerable.Repeat('a', length).ToArray();
        for (int i = 0; i < breaks; i++)
        {
            text[(i * 2) + 1] = separator;
        }

        JsonObject root = Root();
        VideoTrack(root)["clips"]!.AsArray().Add(Title("clip-9", 0, Second, 5 * Second, new string(text)));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Rejected(root);
        }
    }

    // «Admite saltos de línea `\n` y tabulaciones, y las marcas de dirección (bidi); ningún otro carácter de control.»
    [Theory]
    [InlineData("a\nb\tc\u200Fd\u202Ee", true)]
    [InlineData("a\rb", false)]
    [InlineData("a\u0007b", false)]
    public void El_texto_solo_admite_salto_tabulacion_y_bidi(string text, bool accepted)
    {
        JsonObject root = Root();
        VideoTrack(root)["clips"]!.AsArray().Add(Title("clip-9", 0, Second, 5 * Second, text));

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Rejected(root);
        }
    }

    // «Un rol es un texto de 1 a 32 caracteres con minúsculas ASCII, dígitos y guiones; uno desconocido se conserva.»
    [Theory]
    [InlineData("subtitles", true)]
    [InlineData("mi-rol-2", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("", false)]
    [InlineData("Subtitles", false)]
    [InlineData("mi rol", false)]
    [InlineData("rol_1", false)]
    public void El_rol_es_un_texto_corto_en_minusculas(string role, bool accepted)
    {
        JsonObject root = Root();
        VideoTrack(root)["role"] = role;

        if (accepted)
        {
            JsonObject written = RoundTrip(root);
            Assert.Equal(role, (string)written["sequences"]![0]!["videoTracks"]![0]!["role"]!);
        }
        else
        {
            Rejected(root);
        }
    }

    // «Hoy se usa `subtitles` (solo en pistas de video)»
    [Fact]
    public void El_rol_subtitles_no_va_en_una_pista_de_audio()
    {
        JsonObject root = Root();
        AudioTrack(root)["role"] = "subtitles";

        Rejected(root);
    }

    // «una secuencia nunca puede llegar a contenerse a sí misma, ni directa ni indirectamente: el archivo se rechaza.»
    [Fact]
    public void Una_secuencia_que_se_contiene_a_si_misma_se_rechaza()
    {
        JsonObject direct = Root();
        Clip(direct, 1).Remove("mediaId");
        Clip(direct, 1)["sequenceId"] = "sequence-1";
        Rejected(direct);

        JsonObject indirect = Root();
        JsonObject nested = JsonNode.Parse($$"""
            { "id": "sequence-2", "name": "Anidada", "frameRate": { "num": 30, "den": 1 }, "width": 1920, "height": 1080,
              "sampleRate": 48000,
              "videoTracks": [ { "id": "track-n", "kind": "video", "name": "V", "clips": [
                { "id": "clip-n", "sequenceId": "sequence-1", "sourceIn": 0, "sourceOut": {{Second}}, "start": 0, "linkId": null, "enabled": true, "effects": [] } ],
                "effects": [], "transitions": [] } ],
              "audioTracks": [], "markers": [], "regions": [] }
            """)!.AsObject();
        indirect["sequences"]!.AsArray().Add(nested);
        Clip(indirect, 1).Remove("mediaId");
        Clip(indirect, 1)["sequenceId"] = "sequence-2";
        Rejected(indirect);
    }

    // «Un clip puede llevar `sequenceId` en lugar de `mediaId`: es un **clip compuesto** … No lleva `speed` ni
    // `generator`»
    [Fact]
    public void Un_clip_compuesto_se_lee_y_no_escribe_mediaId()
    {
        JsonObject root = Root();
        JsonObject nested = JsonNode.Parse("""
            { "id": "sequence-2", "name": "Anidada", "frameRate": { "num": 30, "den": 1 }, "width": 1920, "height": 1080,
              "sampleRate": 48000, "videoTracks": [], "audioTracks": [], "markers": [], "regions": [] }
            """)!.AsObject();
        root["sequences"]!.AsArray().Add(nested);
        Clip(root, 1).Remove("mediaId");
        Clip(root, 1)["sequenceId"] = "sequence-2";

        JsonObject clip = RoundTrip(root)["sequences"]![0]!["videoTracks"]![0]!["clips"]![1]!.AsObject();

        Assert.Equal("sequence-2", (string)clip["sequenceId"]!);
        Assert.False(clip.ContainsKey("mediaId"));
    }

    // «`clip.color` | Color de la etiqueta del clip, `#RRGGBB` (se guarda en mayúsculas).»: el color que pone Rodaje.
    // Uno en minúsculas escrito por otro programa se lee y se vuelve a guardar tal cual (pendiente en el PR).
    [Fact]
    public void El_color_que_pone_Rodaje_se_guarda_en_mayusculas()
    {
        Project project = Read(Root());
        Sequence sequence = project.Sequences[0];
        var ctx = new Rodaje.Core.Editing.EditContext(project, sequence, new SequentialIdGenerator());

        Rodaje.Core.Editing.TimelineEdits.SetClipColor(ctx, ["clip-1"], "#a1b2c3");
        JsonObject clip = Write(project)["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

        Assert.Equal("#A1B2C3", (string)clip["color"]!);
    }

    // «`clip.color` | Color de la etiqueta del clip, `#RRGGBB`»
    [Theory]
    [InlineData("#A1B2C3", true)]
    [InlineData("#a1b2c3", true)]
    [InlineData("#A1B2C", false)]
    [InlineData("rojo", false)]
    public void El_color_del_clip_es_RRGGBB(string color, bool accepted)
    {
        JsonObject root = Root();
        Clip(root)["color"] = color;

        if (accepted)
        {
            Read(root);
        }
        else
        {
            Assert.Contains("no es #RRGGBB", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «`clip.color` | … Sin él, el color de su tipo.»: sin color no se escribe.
    [Fact]
    public void Sin_color_el_clip_no_lo_escribe()
    {
        JsonObject clip = RoundTrip(Root())["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

        Assert.False(clip.ContainsKey("color"));
        Assert.False(clip.ContainsKey("groupId"));
    }

    // «`clip.groupId` | … Texto sin caracteres de control.»
    [Theory]
    [InlineData("grupo 1 — ñ", true)]
    [InlineData("a\u0001b", false)]
    [InlineData("", false)]
    public void El_grupo_es_un_texto_sin_caracteres_de_control(string group, bool accepted)
    {
        JsonObject root = Root();
        Clip(root)["groupId"] = group;

        if (accepted)
        {
            Assert.Equal(group, Read(root).Sequences[0].VideoTracks[0].Clips[0].GroupId);
        }
        else
        {
            Assert.Contains("grupo vacío o con caracteres de control", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «`track.height` | Alto de la pista en la línea de tiempo, en píxeles, de 24 a 320.»
    [Theory]
    [InlineData(24, true)]
    [InlineData(320, true)]
    [InlineData(23, false)]
    [InlineData(321, false)]
    public void El_alto_de_la_pista_va_de_24_a_320(int height, bool accepted)
    {
        JsonObject root = Root();
        VideoTrack(root)["height"] = height;

        if (accepted)
        {
            Assert.Equal(height, Read(root).Sequences[0].VideoTracks[0].Height);
        }
        else
        {
            Assert.Contains("alto de", Rejected(root).Message, StringComparison.Ordinal);
        }
    }

    // «`track.height` | … Sin él, el de siempre.»
    [Fact]
    public void Sin_alto_la_pista_no_lo_escribe()
    {
        JsonObject track = RoundTrip(Root())["sequences"]![0]!["videoTracks"]![0]!.AsObject();

        Assert.False(track.ContainsKey("height"));
    }
}
