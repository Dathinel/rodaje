using System.Text.Json.Nodes;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Qué significa cada campo» de <c>docs/formato-proyecto.md</c>, comprobado al leer y escribir un
/// <c>.rodaje</c>. Cada prueba cita la frase. La de los separadores <c>/</c> y <c>\</c> no está: solo se comprueba en
/// Windows.
/// </summary>
public sealed class ProjectFormatFieldsContractTests
{
    private static JsonObject Root() => JsonNode.Parse(ProjectSerializer.Serialize(ValidProject()))!.AsObject();

    private static JsonObject Sequence(JsonObject root) => root["sequences"]![0]!.AsObject();

    private static JsonArray VideoTracks(JsonObject root) => Sequence(root)["videoTracks"]!.AsArray();

    private static JsonObject VideoClip(JsonObject root, int track = 0, int clip = 0) =>
        VideoTracks(root)[track]!["clips"]![clip]!.AsObject();

    private static Project Read(JsonObject root) => ProjectSerializer.Deserialize(root.ToJsonString());

    private static void Rejected(JsonObject root) =>
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

    private static JsonObject Saved(Project project) => JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();

    private static EditContext Context(Project project) =>
        new(project, project.Sequences[0], new SequentialIdGenerator());

    // «`schemaVersion` es la versión del formato y hoy vale 13; es el primer dato que se lee»
    [Fact]
    public void SchemaVersion_vale_13_y_va_primero()
    {
        JsonObject root = Root();
        Assert.Equal(13, Project.CurrentSchemaVersion);
        Assert.Equal("schemaVersion", root.First().Key);
        Assert.Equal(13, (int)root["schemaVersion"]!);
        Assert.Equal(["schemaVersion", "id", "name", "media", "sequences"], root.Select(p => p.Key).Take(5));
    }

    // «`sequences` son las líneas de tiempo, que pueden ser varias, por ejemplo una horizontal y otra vertical del
    // mismo material»
    [Fact]
    public void Puede_haber_varias_secuencias_del_mismo_material()
    {
        JsonObject root = Root();
        var vertical = (JsonObject)Sequence(root).DeepClone();
        vertical["id"] = "s2";
        vertical["width"] = 1080;
        vertical["height"] = 1920;
        foreach (JsonNode? track in vertical["videoTracks"]!.AsArray().Concat(vertical["audioTracks"]!.AsArray()))
        {
            track!["id"] = "vertical-" + (string)track["id"]!;
            foreach (JsonNode? clip in track["clips"]!.AsArray())
            {
                clip!["id"] = "vertical-" + (string)clip["id"]!;
                if (clip["linkId"] is JsonNode link)
                {
                    clip["linkId"] = "vertical-" + (string)link!;
                }

                foreach (JsonNode? effect in clip["effects"]!.AsArray())
                {
                    effect!["id"] = "vertical-" + (string)effect["id"]!;
                }
            }
        }

        root["sequences"]!.AsArray().Add(vertical);
        Project project = Read(root);
        Assert.Equal(2, project.Sequences.Count);
        Assert.Equal(1920, project.Sequences[1].Height);
        Assert.Equal("m1", project.Sequences[1].VideoTracks[0].Clips[0].MediaId);
    }

    // «`path` es su ruta; se prefiere relativa a la carpeta donde está el `.rodaje` … aunque se aceptan rutas absolutas.»
    [Fact]
    public void Una_ruta_absoluta_se_acepta_y_se_guarda_tal_cual()
    {
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "material", "toma 1.mp4"));
        JsonObject root = Root();
        root["media"]![0]!["path"] = absolute;
        Project project = Read(root);
        Assert.Equal(absolute, project.Media[0].Path);
        Assert.Equal(absolute, project.Media[0].ResolvePath(Path.Combine(Path.GetTempPath(), "otra")));
        Assert.Equal(absolute, (string)Saved(project)["media"]![0]!["path"]!);
    }

    // «`path` … se prefiere relativa a la carpeta donde está el `.rodaje`, para que el proyecto se pueda mover junto con
    // su material a otro PC»
    [Fact]
    public void Una_ruta_relativa_se_resuelve_contra_la_carpeta_del_proyecto()
    {
        string first = Path.Combine(Path.GetTempPath(), "proyecto-a");
        string second = Path.Combine(Path.GetTempPath(), "otro-pc", "proyecto-a");
        var media = new MediaRef { Path = "material/toma 1.mp4" };
        Assert.Equal(Path.Combine(first, "material", "toma 1.mp4"), media.ResolvePath(first));
        Assert.Equal(Path.Combine(second, "material", "toma 1.mp4"), media.ResolvePath(second));
    }

    // «`frameRate` es la tasa de su video o `null` si no tiene (una imagen fija o un archivo de solo audio) … Los campos
    // que no aplican valen 0.»
    [Fact]
    public void Un_medio_de_solo_audio_tiene_frameRate_null_y_ceros_en_lo_que_no_aplica()
    {
        JsonObject audio = Root()["media"]![1]!.AsObject();
        Assert.Equal("m2", (string)audio["id"]!);
        Assert.True(audio.ContainsKey("frameRate"));
        Assert.Null(audio["frameRate"]);
        Assert.Equal(0, (int)audio["width"]!);
        Assert.Equal(0, (int)audio["height"]!);
        Assert.False((bool)audio["hasVideo"]!);

        Project project = Read(Root());
        Assert.Null(project.Media[1].FrameRate);
        Assert.Equal(0, project.Media[1].Width);
    }

    // «Cada secuencia tiene su propio `id` y `name`, su tasa de cuadros `frameRate`, su resolución `width` × `height` y la
    // frecuencia de muestreo `sampleRate` … Las pistas van en dos listas separadas, `videoTracks` y `audioTracks`»
    [Fact]
    public void La_secuencia_tiene_sus_campos_y_dos_listas_de_pistas()
    {
        JsonObject sequence = Sequence(Root());
        Assert.Equal(
            ["id", "name", "frameRate", "width", "height", "sampleRate", "videoTracks", "audioTracks"],
            sequence.Select(p => p.Key).Take(8));
    }

    // «en `videoTracks` el primer elemento es la capa de más abajo y cada pista siguiente se compone encima de la
    // anterior» (el orden de la lista se conserva tal cual)
    [Fact]
    public void El_orden_de_las_pistas_se_conserva()
    {
        JsonObject root = Root();
        JsonArray tracks = VideoTracks(root);
        JsonNode first = tracks[0]!;
        tracks.RemoveAt(0);
        tracks.Add(first);
        Project project = Read(root);
        Assert.Equal(["v2", "v1"], project.Sequences[0].VideoTracks.Select(t => t.Id));
        Assert.Equal(["v2", "v1"], VideoTracks(Saved(project)).Select(t => (string)t!["id"]!));
    }

    // «`kind` (que vale `"video"` o `"audio"` y debe coincidir con la lista en la que está)»
    [Fact]
    public void Kind_debe_coincidir_con_la_lista()
    {
        JsonObject root = Root();
        Assert.Equal("video", (string)VideoTracks(root)[0]!["kind"]!);
        Assert.Equal("audio", (string)Sequence(root)["audioTracks"]![0]!["kind"]!);

        VideoTracks(root)[1]!["kind"] = "audio";
        VideoTracks(root)[1]!["clips"] = new JsonArray();
        Rejected(root);
    }

    // «tres interruptores: `muted` la silencia, `hidden` la oculta de la imagen y `locked` impide editarla»
    [Fact]
    public void Los_tres_interruptores_se_leen_y_se_escriben()
    {
        JsonObject root = Root();
        JsonObject track = VideoTracks(root)[0]!.AsObject();
        track["muted"] = true;
        track["hidden"] = true;
        track["locked"] = true;
        Project project = Read(root);
        Track read = project.Sequences[0].VideoTracks[0];
        Assert.True(read.Muted && read.Hidden && read.Locked);
        Assert.Throws<EditException>(() => TimelineEdits.Trim(Context(project), "c1", TrimEdge.Out, -Sec(1)));
    }

    // «Su lista `clips` está ordenada por posición»
    [Fact]
    public void Clips_fuera_de_orden_se_rechazan()
    {
        JsonObject root = Root();
        JsonArray clips = VideoTracks(root)[0]!["clips"]!.AsArray();
        JsonNode first = clips[0]!;
        clips.RemoveAt(0);
        clips.Add(first);
        Rejected(root);
    }

    // «`markers` guarda los marcadores, cada uno con `id`, el instante `time` y un `name` que puede ir vacío, ordenados
    // por tiempo»
    [Fact]
    public void Marcadores_con_nombre_vacio_y_ordenados_por_tiempo()
    {
        Project project = ValidProject();
        EditContext ctx = Context(project);
        TimelineEdits.AddMarker(ctx, Sec(5), "");
        TimelineEdits.AddMarker(ctx, Sec(2), "dos");
        string late = TimelineEdits.AddMarker(ctx, Sec(8), "ocho");
        TimelineEdits.MoveMarker(ctx, late, Sec(1));

        JsonArray markers = Sequence(Saved(project))["markers"]!.AsArray();
        Assert.Equal([Sec(1).Ticks, Sec(2).Ticks, Sec(5).Ticks], markers.Select(m => (long)m!["time"]!));
        Assert.Equal(["id", "time", "name"], markers[0]!.AsObject().Select(p => p.Key));
        Assert.Equal("", (string)markers[2]!["name"]!);
        Assert.Equal(3, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Sequences[0].Markers.Count);
    }

    // «`regions` guarda las regiones, tramos con nombre `[start, end)` que pueden solaparse, ordenadas por inicio»
    [Fact]
    public void Regiones_que_se_solapan_ordenadas_por_inicio()
    {
        Project project = ValidProject();
        EditContext ctx = Context(project);
        TimelineEdits.AddRegion(ctx, Sec(4), Sec(9), "b");
        TimelineEdits.AddRegion(ctx, Sec(1), Sec(6), "a");

        JsonArray regions = Sequence(Saved(project))["regions"]!.AsArray();
        Assert.Equal(["a", "b"], regions.Select(r => (string)r!["name"]!));
        Assert.Equal(["id", "start", "end", "name"], regions[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(2, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)).Sequences[0].Regions.Count);

        JsonObject root = Saved(project);
        Sequence(root)["regions"]![0]!["end"] = Sec(1).Ticks;
        Rejected(root);
    }

    // «Los dos admiten `color` (`#RRGGBB`) y `note` (texto libre de hasta 2000 caracteres), que solo se escriben si
    // tienen valor»
    [Fact]
    public void Color_y_nota_solo_se_escriben_si_tienen_valor_y_tienen_limites()
    {
        Project project = ValidProject();
        project.Sequences[0].Markers.Add(new Marker { Id = "mk1", Time = Sec(1) });
        project.Sequences[0].Markers.Add(new Marker { Id = "mk2", Time = Sec(2), Color = "#12AB9F", Note = new string('n', 2000) });
        project.Sequences[0].Regions.Add(new Region { Id = "rg1", Start = Sec(1), End = Sec(2) });
        project.Sequences[0].Regions.Add(new Region { Id = "rg2", Start = Sec(1), End = Sec(3), Color = "#000000", Note = "nota" });

        JsonObject root = Saved(project);
        JsonArray markers = Sequence(root)["markers"]!.AsArray();
        JsonArray regions = Sequence(root)["regions"]!.AsArray();
        Assert.False(markers[0]!.AsObject().ContainsKey("color") || markers[0]!.AsObject().ContainsKey("note"));
        Assert.False(regions[0]!.AsObject().ContainsKey("color") || regions[0]!.AsObject().ContainsKey("note"));
        Assert.Equal("#12AB9F", (string)markers[1]!["color"]!);
        Assert.Equal("nota", (string)regions[1]!["note"]!);
        Read(root);

        markers[1]!["note"] = new string('n', 2001);
        Rejected(root);

        root = Saved(project);
        Sequence(root)["regions"]![1]!["color"] = "rojo";
        Rejected(root);
    }

    // «Su duración es `sourceOut − sourceIn` y su final en la pista es `start + duración`. Estos dos valores no se
    // guardan porque se calculan»
    [Fact]
    public void Duracion_y_final_se_calculan_y_no_se_guardan()
    {
        JsonObject clip = VideoClip(Root(), clip: 1);
        Assert.Equal(["id", "mediaId", "sourceIn", "sourceOut", "start", "linkId", "enabled", "effects"], clip.Select(p => p.Key));

        Clip read = Read(Root()).Sequences[0].VideoTracks[0].Clips[1];
        Assert.Equal(Sec(25) - Sec(20), read.Duration);
        Assert.Equal(Sec(12) + Sec(5), read.End);
    }

    // «y un programa externo no debe escribirlos» (formato 4: «sin `speed`, un `duration` escrito se ignora»)
    [Fact]
    public void Una_duracion_escrita_sin_velocidad_se_ignora()
    {
        JsonObject root = Root();
        VideoClip(root, clip: 1)["duration"] = Sec(99).Ticks;
        VideoClip(root, clip: 1)["end"] = Sec(500).Ticks;
        Project project = Read(root);
        Clip clip = project.Sequences[0].VideoTracks[0].Clips[1];
        Assert.Equal(Sec(5), clip.Duration);
        Assert.False(VideoClip(Saved(project), clip: 1).ContainsKey("duration"));
    }

    // «Toda la edición no destructiva consiste en cambiar estos tres números: recortar mueve `sourceIn` o `sourceOut`,
    // deslizar mueve `start`.»
    [Fact]
    public void Recortar_y_deslizar_solo_cambian_los_tres_numeros()
    {
        Project project = ValidProject();
        EditContext ctx = Context(project);
        TimelineEdits.Trim(ctx, "c3", TrimEdge.Out, -Sec(1));
        TimelineEdits.Move(ctx, "c3", Sec(6), null, PlacementMode.Strict);

        JsonObject clip = VideoClip(Saved(project), track: 1);
        Assert.Equal(["id", "mediaId", "sourceIn", "sourceOut", "start", "linkId", "enabled", "effects"], clip.Select(p => p.Key));
        Assert.Equal(Sec(30).Ticks, (long)clip["sourceIn"]!);
        Assert.Equal(Sec(32).Ticks, (long)clip["sourceOut"]!);
        Assert.Equal(Sec(6).Ticks, (long)clip["start"]!);
        Assert.Equal("m1", (string)clip["mediaId"]!);
    }

    // «`mediaId` dice qué medio usa»
    [Fact]
    public void Un_mediaId_que_no_existe_se_rechaza()
    {
        JsonObject root = Root();
        VideoClip(root)["mediaId"] = "no-existe";
        Rejected(root);
    }

    // «`enabled` permite apagar un clip sin borrarlo»
    [Fact]
    public void Un_clip_apagado_se_conserva()
    {
        JsonObject root = Root();
        VideoClip(root)["enabled"] = false;
        Project project = Read(root);
        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.False(clip.Enabled);
        Assert.False((bool)VideoClip(Saved(project))["enabled"]!);
    }

    // «`linkId`, si no es `null`, agrupa clips que se editan juntos … El valor de `linkId` es un texto cualquiera que
    // comparten los clips del grupo y no tiene que aparecer en ningún otro lugar.»
    [Fact]
    public void LinkId_es_un_texto_cualquiera_y_agrupa()
    {
        JsonObject root = Root();
        const string link = "toma ñ 1 — {cualquier} cosa";
        VideoClip(root)["linkId"] = link;
        Sequence(root)["audioTracks"]![0]!["clips"]![0]!["linkId"] = link;
        Project project = Read(root);
        Assert.Equal(link, project.Sequences[0].VideoTracks[0].Clips[0].LinkId);
        Assert.Null(project.Sequences[0].VideoTracks[0].Clips[1].LinkId);
        Assert.True(VideoClip(Saved(project), clip: 1).ContainsKey("linkId"));

        TimelineEdits.Trim(Context(project), "c1", TrimEdge.Out, -Sec(1));
        Assert.Equal(Sec(9), project.Sequences[0].AudioTracks[0].Clips[0].Duration);
    }

    // «Los efectos de un clip están en `effects` y se aplican en el orden de la lista. Cada efecto tiene `id`, un `type`
    // con espacio de nombres, como `rodaje.transform`, un interruptor `enabled` y un diccionario `parameters`»
    [Fact]
    public void Un_efecto_tiene_id_type_enabled_parameters_y_options_en_orden()
    {
        Project project = ValidProject();
        project.Sequences[0].VideoTracks[0].Clips[0].Effects.Add(new EffectInstance { Id = "fx2", Type = "rodaje.blur", Enabled = false });
        JsonArray effects = VideoClip(Saved(project))["effects"]!.AsArray();
        Assert.Equal(["fx1", "fx2"], effects.Select(e => (string)e!["id"]!));
        Assert.Equal(["id", "type", "enabled", "parameters", "options"], effects[0]!.AsObject().Select(p => p.Key));
        Assert.False((bool)effects[1]!["enabled"]!);
    }

    // «Un programa no debe depender del orden de esas claves.»
    [Fact]
    public void El_orden_de_las_claves_de_parameters_no_cambia_nada()
    {
        JsonObject root = Root();
        JsonObject parameters = VideoClip(root)["effects"]![0]!["parameters"]!.AsObject();
        List<KeyValuePair<string, JsonNode?>> entries = [.. parameters.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))];
        parameters.Clear();
        foreach ((string key, JsonNode? value) in Enumerable.Reverse(entries))
        {
            parameters[key] = value;
        }

        Clip clip = Read(root).Sequences[0].VideoTracks[0].Clips[0];
        Clip original = ValidProject().Sequences[0].VideoTracks[0].Clips[0];
        foreach ((string name, Parameter p) in original.Effects[0].Parameters)
        {
            Assert.Equal(p.Value, clip.Effects[0].Parameters[name].Value);
            Assert.Equal(p.Keyframes, clip.Effects[0].Parameters[name].Keyframes);
        }
    }

    // «Cada parámetro es numérico: `value` es su valor cuando no está animado y `keyframes` es la animación. Cada keyframe
    // tiene `time`, `value` e `interpolation`»
    [Fact]
    public void Parametro_con_value_y_keyframes()
    {
        JsonObject opacity = VideoClip(Root())["effects"]![0]!["parameters"]!["opacity"]!.AsObject();
        Assert.Equal(["value", "keyframes"], opacity.Select(p => p.Key));
        Assert.Equal(["time", "value", "interpolation"], opacity["keyframes"]![0]!.AsObject().Select(p => p.Key));

        Parameter still = new() { Value = 0.4 };
        Assert.Equal(0.4, still.Evaluate(Sec(3)));
    }

    // «`hold` mantiene el valor y salta de golpe, `linear` avanza en línea recta, `easeIn` …, `easeOut` …, `easeInOut` …,
    // `bounce` …, `elastic` … y `bezier`»
    [Theory]
    [InlineData("hold", Interpolation.Hold)]
    [InlineData("linear", Interpolation.Linear)]
    [InlineData("easeIn", Interpolation.EaseIn)]
    [InlineData("easeOut", Interpolation.EaseOut)]
    [InlineData("easeInOut", Interpolation.EaseInOut)]
    [InlineData("bounce", Interpolation.Bounce)]
    [InlineData("elastic", Interpolation.Elastic)]
    [InlineData("bezier", Interpolation.Bezier)]
    public void Los_nombres_de_las_interpolaciones(string name, Interpolation expected)
    {
        JsonObject root = Root();
        JsonObject keyframe = VideoClip(root)["effects"]![0]!["parameters"]!["opacity"]!["keyframes"]![0]!.AsObject();
        keyframe["interpolation"] = name;
        Project project = Read(root);
        Assert.Equal(expected, project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"].Keyframes[0].Interpolation);
        Assert.Equal(name, (string)VideoClip(Saved(project))["effects"]![0]!["parameters"]!["opacity"]!["keyframes"]![0]!["interpolation"]!);
    }

    // «`hold` mantiene el valor y salta de golpe, `linear` avanza en línea recta»
    [Fact]
    public void Hold_salta_y_linear_avanza_recto()
    {
        var hold = new Parameter { Keyframes = [new Keyframe(Sec(0), 2, Interpolation.Hold), new Keyframe(Sec(2), 6)] };
        Assert.Equal(2, hold.Evaluate(Sec(1.999)));
        Assert.Equal(6, hold.Evaluate(Sec(2)));

        var linear = new Parameter { Keyframes = [new Keyframe(Sec(0), 2), new Keyframe(Sec(2), 6)] };
        Assert.Equal(3, linear.Evaluate(Sec(0.5)), 9);
        Assert.Equal(4, linear.Evaluate(Sec(1)), 9);
    }

    // «si falta, vale `linear`»
    [Fact]
    public void Sin_interpolation_vale_linear()
    {
        JsonObject root = Root();
        JsonObject keyframe = VideoClip(root)["effects"]![0]!["parameters"]!["opacity"]!["keyframes"]![1]!.AsObject();
        Assert.Equal("easeOut", (string)keyframe["interpolation"]!);
        keyframe.Remove("interpolation");
        Project project = Read(root);
        Assert.Equal(Interpolation.Linear, project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"].Keyframes[1].Interpolation);
    }

    // «`ease`, los puntos de control `{x1, y1, x2, y2}` de esa curva como `cubic-bezier` de CSS (x de 0 a 1, y de −2 a 3»
    [Theory]
    [InlineData(0, -2, 1, 3, true)]
    [InlineData(-0.01, 0, 1, 1, false)]
    [InlineData(0, 0, 1.01, 1, false)]
    [InlineData(0, -2.01, 1, 1, false)]
    [InlineData(0, 0, 1, 3.01, false)]
    public void Ease_x_de_0_a_1_e_y_de_menos_2_a_3(double x1, double y1, double x2, double y2, bool accepted)
    {
        JsonObject root = Root();
        JsonObject keyframe = VideoClip(root)["effects"]![0]!["parameters"]!["opacity"]!["keyframes"]![0]!.AsObject();
        keyframe["interpolation"] = "bezier";
        keyframe["ease"] = new JsonObject { ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2 };
        if (accepted)
        {
            Keyframe read = Read(root).Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"].Keyframes[0];
            Assert.Equal(new BezierEase(x1, y1, x2, y2), read.Ease);
        }
        else
        {
            Rejected(root);
        }
    }

    // «sin `ease`, `bezier` es como `easeInOut`»
    [Fact]
    public void Bezier_sin_ease_es_como_easeInOut()
    {
        for (double t = 0; t <= 1; t += 0.125)
        {
            Assert.Equal(
                KeyframeMath.Ease(Interpolation.EaseInOut, null, t),
                KeyframeMath.Ease(Interpolation.Bezier, null, t),
                9);
        }
    }

    // «una tangente que falta deja recto ese lado» (y `spatialIn`/`spatialOut` solo se escriben si están)
    [Fact]
    public void Las_tangentes_que_faltan_no_se_escriben()
    {
        Project project = ValidProject();
        Parameter x = new() { Keyframes = [new Keyframe(Sec(0), 0) { SpatialOut = 40 }, new Keyframe(Sec(1), 100)] };
        project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["x"] = x;
        JsonArray keyframes = VideoClip(Saved(project))["effects"]![0]!["parameters"]!["x"]!["keyframes"]!.AsArray();
        Assert.Equal(40, (double)keyframes[0]!["spatialOut"]!);
        Assert.False(keyframes[0]!.AsObject().ContainsKey("spatialIn"));
        Assert.False(keyframes[1]!.AsObject().ContainsKey("spatialIn") || keyframes[1]!.AsObject().ContainsKey("spatialOut"));
        Assert.Equal(25, KeyframeMath.Segment(0, null, null, 100, 0.25), 9);
    }

    // «Antes del primer keyframe el parámetro vale lo del primero y después del último vale lo del último.»
    [Fact]
    public void Antes_del_primero_y_despues_del_ultimo()
    {
        var p = new Parameter { Value = 99, Keyframes = [new Keyframe(Sec(2), 3), new Keyframe(Sec(4), 7, Interpolation.Bounce)] };
        Assert.Equal(3, p.Evaluate(Sec(0)));
        Assert.Equal(3, p.Evaluate(Time.FromTicks(-5)));
        Assert.Equal(7, p.Evaluate(Sec(4)));
        Assert.Equal(7, p.Evaluate(Sec(100)));
    }

    // «su `time` está en **tiempo del origen**, no de la línea de tiempo, y en la misma escala que `sourceIn`. Así, si el
    // clip se mueve en la pista, la animación lo acompaña sin recalcular nada»
    [Fact]
    public void Los_keyframes_estan_en_tiempo_del_origen_y_no_cambian_al_mover()
    {
        Project project = ValidProject();
        Clip clip = project.Sequences[0].VideoTracks[1].Clips[0];
        clip.Effects.Add(new EffectInstance
        {
            Id = "fx-c3",
            Type = "rodaje.transform",
            Parameters = { ["opacity"] = new Parameter { Keyframes = [new Keyframe(Sec(31), 0), new Keyframe(Sec(32), 1)] } },
        });
        Assert.Equal(0.5, clip.Effects[0].Parameters["opacity"].Evaluate(clip.ToEffectTime(Sec(3.5))), 9);

        JsonObject before = Saved(project);
        TimelineEdits.Move(Context(project), "c3", Sec(20), null, PlacementMode.Strict);
        JsonObject after = Saved(project);
        Assert.Equal(
            VideoClip(before, track: 1)["effects"]!.ToJsonString(),
            VideoClip(after, track: 1)["effects"]!.ToJsonString());
        Assert.Equal(0.5, clip.Effects[0].Parameters["opacity"].Evaluate(clip.ToEffectTime(Sec(21.5))), 9);
    }

    // «Lo que no es un número va en `options`, un diccionario de textos»
    [Fact]
    public void Options_es_un_diccionario_de_textos()
    {
        JsonObject root = Root();
        VideoClip(root)["effects"]![0]!["options"] = new JsonObject { ["flipX"] = "on", ["nota"] = "libre" };
        Project project = Read(root);
        Assert.Equal("on", project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Options["flipX"]);

        root = Root();
        VideoClip(root)["effects"]![0]!["options"] = new JsonObject { ["flipX"] = 1 };
        Rejected(root);
    }

    // «Un tipo que no está en el registro … se conserva tal cual al guardar, con sus parámetros y opciones»
    [Fact]
    public void Un_tipo_desconocido_se_conserva_al_guardar()
    {
        JsonObject root = Root();
        var foreign = new JsonObject
        {
            ["id"] = "fx-ajeno",
            ["type"] = "otro.programa.brillo",
            ["enabled"] = true,
            ["parameters"] = new JsonObject { ["fuerza"] = new JsonObject { ["value"] = 12345.5, ["keyframes"] = new JsonArray() } },
            ["options"] = new JsonObject { ["modo"] = "raro" },
        };
        VideoClip(root)["effects"]!.AsArray().Add(foreign.DeepClone());
        JsonNode saved = VideoClip(Saved(Read(root)))["effects"]![1]!;
        Assert.True(JsonNode.DeepEquals(foreign, saved));
    }

    // «Las opciones que el tipo no define se conservan sin mirarlas.»
    [Fact]
    public void Las_opciones_no_definidas_se_conservan()
    {
        JsonObject root = Root();
        VideoClip(root)["effects"]![0]!["options"] = new JsonObject { ["dePlugin"] = "valorñ cualquiera" };
        Project project = Read(root);
        Assert.Equal("valorñ cualquiera", (string)VideoClip(Saved(project))["effects"]![0]!["options"]!["dePlugin"]!);
    }

    // «En los tipos registrados, una opción definida debe tener un valor aceptado: uno de la lista en las de elección»
    [Fact]
    public void Una_opcion_de_eleccion_con_un_valor_fuera_de_la_lista_se_rechaza()
    {
        JsonObject root = Root();
        VideoClip(root)["effects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "fx-blend",
            ["type"] = "rodaje.blend",
            ["enabled"] = true,
            ["parameters"] = new JsonObject(),
            ["options"] = new JsonObject { ["blendMode"] = "screen" },
        });
        Read(root);
        VideoClip(root)["effects"]![1]!["options"]!["blendMode"] = "pantalla";
        Rejected(root);
    }

    // «y solo admiten tipos de su clase: de audio en una pista de audio y de video en una de video»
    [Fact]
    public void Un_efecto_de_audio_en_una_pista_de_video_se_rechaza()
    {
        JsonObject root = Root();
        VideoTracks(root)[0]!["effects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "fx-pista",
            ["type"] = "rodaje.audio",
            ["enabled"] = true,
            ["parameters"] = new JsonObject(),
            ["options"] = new JsonObject(),
        });
        Rejected(root);

        root = Root();
        Sequence(root)["audioTracks"]![0]!["effects"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "fx-pista",
            ["type"] = "rodaje.audio",
            ["enabled"] = true,
            ["parameters"] = new JsonObject { ["gainDb"] = new JsonObject { ["value"] = -6, ["keyframes"] = new JsonArray() } },
            ["options"] = new JsonObject(),
        });
        Read(root);
    }

    // «Un clip puede llevar `speed`; si no lo lleva (o es `null`) se reproduce a 1×»
    [Fact]
    public void Speed_null_es_1x()
    {
        JsonObject root = Root();
        VideoClip(root, clip: 1)["speed"] = null;
        Clip clip = Read(root).Sequences[0].VideoTracks[0].Clips[1];
        Assert.Null(clip.Speed);
        Assert.Equal(Sec(21), clip.ToSourceTime(Sec(13)));
        Assert.False(VideoClip(Saved(Read(root)), clip: 1).ContainsKey("speed"));
    }

    // «Un generador no tiene medio … Va solo en pistas de video, sin `speed`, y `mediaId` no se escribe.»
    [Fact]
    public void Un_generador_no_escribe_mediaId_y_va_solo_en_video()
    {
        Project project = ValidProject();
        project.Sequences[0].VideoTracks[1].Clips.Add(MakeTitle("t1", 10, -1, 2));
        JsonObject root = Saved(project);
        Assert.False(VideoClip(root, track: 1, clip: 1).ContainsKey("mediaId"));
        Read(root);

        project = ValidProject();
        project.Sequences[0].AudioTracks[0].Clips.Add(MakeTitle("t1", 50, 0, 2));
        Rejected(Saved(project));
    }

    // «Un rol es un texto de 1 a 32 caracteres con minúsculas ASCII, dígitos y guiones; uno desconocido se conserva.»
    [Theory]
    [InlineData("rol-propio-7", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("Subtitulos", false)]
    [InlineData("rol_propio", false)]
    public void El_rol_de_la_pista(string role, bool accepted)
    {
        JsonObject root = Root();
        VideoTracks(root)[1]!["role"] = role;
        if (accepted)
        {
            Project project = Read(root);
            Assert.Equal(role, (string)VideoTracks(Saved(project))[1]!["role"]!);
        }
        else
        {
            Rejected(root);
        }
    }

    // «una secuencia nunca puede llegar a contenerse a sí misma, ni directa ni indirectamente: el archivo se rechaza»
    [Fact]
    public void Una_secuencia_que_se_contiene_a_si_misma_se_rechaza()
    {
        JsonObject root = Root();
        VideoTracks(root)[1]!["clips"] = new JsonArray(new JsonObject
        {
            ["id"] = "cc",
            ["sequenceId"] = "s1",
            ["sourceIn"] = 0,
            ["sourceOut"] = Sec(5).Ticks,
            ["start"] = 0,
            ["linkId"] = null,
            ["enabled"] = true,
            ["effects"] = new JsonArray(),
        });
        Rejected(root);
    }

    // «`clip.color` | Color de la etiqueta del clip, `#RRGGBB` (se guarda en mayúsculas). Sin él, el color de su tipo.»
    [Fact]
    public void El_color_del_clip_se_guarda_en_mayusculas()
    {
        Project project = ValidProject();
        Assert.False(VideoClip(Saved(project)).ContainsKey("color"));
        TimelineEdits.SetClipColor(Context(project), ["c1"], "#a1b2c3");
        Assert.Equal("#A1B2C3", (string)VideoClip(Saved(project))["color"]!);

        JsonObject root = Saved(project);
        VideoClip(root)["color"] = "#A1B2C";
        Rejected(root);
    }

    // «`clip.groupId` | … Texto sin caracteres de control.»
    [Fact]
    public void El_grupo_no_admite_caracteres_de_control()
    {
        JsonObject root = Root();
        VideoClip(root)["groupId"] = "grupo ñ";
        Assert.Equal("grupo ñ", Read(root).Sequences[0].VideoTracks[0].Clips[0].GroupId);
        VideoClip(root)["groupId"] = "gru\u0001po";
        Rejected(root);
    }

    // «`track.height` | Alto de la pista en la línea de tiempo, en píxeles, de 24 a 320. Sin él, el de siempre.»
    [Theory]
    [InlineData(24, true)]
    [InlineData(320, true)]
    [InlineData(23, false)]
    [InlineData(321, false)]
    public void El_alto_de_la_pista_va_de_24_a_320(int height, bool accepted)
    {
        Assert.False(VideoTracks(Root())[0]!.AsObject().ContainsKey("height"));
        JsonObject root = Root();
        VideoTracks(root)[0]!["height"] = height;
        if (accepted)
        {
            Assert.Equal(height, Read(root).Sequences[0].VideoTracks[0].Height);
        }
        else
        {
            Rejected(root);
        }
    }
}
