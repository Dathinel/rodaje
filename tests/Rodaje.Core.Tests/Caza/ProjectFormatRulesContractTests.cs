using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Reglas que todo programa debe respetar» de <c>docs/formato-proyecto.md</c>, comprobado al abrir un
/// <c>.rodaje</c> con <see cref="ProjectSerializer.Deserialize(string)"/>. Cada prueba cita la frase.
/// </summary>
public sealed class ProjectFormatRulesContractTests
{
    private const long Second = Time.TicksPerSecond;

    /// <summary>
    /// Proyecto válido de partida: un medio con imagen y sonido, una foto, un sonido, dos clips contiguos en video, uno
    /// en audio y un bus.
    /// </summary>
    private static JsonObject Base()
    {
        Clip MediaClip(string id, string mediaId, long start) => new()
        {
            Id = id,
            MediaId = mediaId,
            SourceIn = Time.Zero,
            SourceOut = Time.FromTicks(2 * Second),
            Start = Time.FromTicks(start),
        };

        var project = new Project
        {
            Id = "project-1",
            Name = "Base",
            Media =
            [
                new MediaRef { Id = "media-av", Path = "toma.mp4", Name = "toma", Duration = Time.FromTicks(10 * Second), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "media-image", Path = "foto.png", Name = "foto", Duration = Time.FromTicks(10 * Second), HasVideo = true },
                new MediaRef { Id = "media-sound", Path = "musica.wav", Name = "música", Duration = Time.FromTicks(10 * Second), HasAudio = true },
            ],
            Sequences =
            [
                new Sequence
                {
                    Id = "sequence-1",
                    Name = "Secuencia 1",
                    VideoTracks =
                    [
                        new Track { Id = "track-v", Kind = TrackKind.Video, Name = "Video 1", Clips = [MediaClip("clip-1", "media-av", 0), MediaClip("clip-2", "media-av", 2 * Second)] },
                    ],
                    AudioTracks =
                    [
                        new Track { Id = "track-a", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [MediaClip("clip-3", "media-av", 0)] },
                    ],
                    AudioBuses = [new AudioBus { Id = "bus-1", Name = "Voces" }],
                },
            ],
        };

        return JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
    }

    private static JsonObject Seq(JsonObject root) => root["sequences"]![0]!.AsObject();

    private static JsonObject VideoTrack(JsonObject root) => Seq(root)["videoTracks"]![0]!.AsObject();

    private static JsonObject AudioTrack(JsonObject root) => Seq(root)["audioTracks"]![0]!.AsObject();

    private static JsonArray VideoClips(JsonObject root) => VideoTrack(root)["clips"]!.AsArray();

    private static JsonObject Clip1(JsonObject root) => VideoClips(root)[0]!.AsObject();

    private static JsonObject Clip2(JsonObject root) => VideoClips(root)[1]!.AsObject();

    private static JsonObject Media(JsonObject root, int index) => root["media"]![index]!.AsObject();

    private static JsonObject Effect(string id, string type, JsonObject? parameters = null) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["enabled"] = true,
        ["parameters"] = parameters ?? new JsonObject(),
        ["options"] = new JsonObject(),
    };

    private static JsonObject Transition(string id, string from, string to, long duration, string type = BuiltInTransitions.Crossfade) => new()
    {
        ["id"] = id,
        ["type"] = type,
        ["from"] = from,
        ["to"] = to,
        ["duration"] = duration,
        ["parameters"] = new JsonObject(),
        ["options"] = new JsonObject(),
    };

    private static Project Open(JsonObject root) => ProjectSerializer.Deserialize(root.ToJsonString());

    private static Project Open(JsonObject root, out ClampSummary clamped) => ProjectSerializer.Deserialize(root.ToJsonString(), out clamped);

    private static void Rejected(JsonObject root) =>
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));

    /// <summary>Rechazado por una regla que el mensaje enumera, no por el último recurso de un validador que lanzó.</summary>
    private static void RejectedWithProblems(JsonObject root)
    {
        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(root.ToJsonString()));
        Assert.NotEmpty(ex.Problems);
    }

    private static JsonObject With(Action<JsonObject> change)
    {
        JsonObject root = Base();
        change(root);
        return root;
    }

    [Fact]
    public void El_proyecto_de_partida_es_valido()
    {
        Project project = Open(Base(), out ClampSummary clamped);
        Assert.Equal(0, clamped.Total);
        Assert.Equal(2, project.Sequences[0].VideoTracks[0].Clips.Count);
    }

    // «Rodaje las comprueba todas al abrir un proyecto y lo rechaza con un mensaje que las enumera si alguna falla»
    [Fact]
    public void El_rechazo_enumera_cada_regla_que_falla()
    {
        JsonObject root = With(r =>
        {
            Media(r, 0)["duration"] = 0;
            Seq(r)["markers"] = new JsonArray(new JsonObject { ["id"] = "marker-1", ["time"] = -1 });
        });

        ProjectFormatException ex = Assert.Throws<ProjectFormatException>(() => Open(root));
        Assert.True(ex.Problems.Count >= 2);
        Assert.Contains(ex.Problems, p => p.Contains("media-av", StringComparison.Ordinal));
        Assert.Contains(ex.Problems, p => p.Contains("marker-1", StringComparison.Ordinal));
        Assert.All(ex.Problems, p => Assert.Contains(p, ex.Message, StringComparison.Ordinal));
    }

    // «Todos los identificadores (`id`) … deben ser textos no vacíos»
    [Theory]
    [InlineData("project")]
    [InlineData("media")]
    [InlineData("sequence")]
    [InlineData("track")]
    [InlineData("clip")]
    [InlineData("effect")]
    [InlineData("marker")]
    [InlineData("region")]
    public void Un_identificador_vacio_se_rechaza(string what) => Rejected(WithId(what, ""));

    // «… sin caracteres de control»
    [Theory]
    [InlineData("project")]
    [InlineData("media")]
    [InlineData("sequence")]
    [InlineData("track")]
    [InlineData("clip")]
    [InlineData("effect")]
    [InlineData("marker")]
    [InlineData("region")]
    public void Un_identificador_con_caracteres_de_control_se_rechaza(string what) => Rejected(WithId(what, "id\n1"));

    // «… y únicos en todo el archivo, compartiendo un mismo espacio: un clip no puede llamarse igual que una pista»
    [Theory]
    [InlineData("project")]
    [InlineData("media")]
    [InlineData("sequence")]
    [InlineData("clip")]
    [InlineData("effect")]
    [InlineData("marker")]
    [InlineData("region")]
    public void Un_identificador_igual_al_de_una_pista_se_rechaza(string what) => Rejected(WithId(what, "track-a"));

    // «otros programas pueden usar cualquier texto, por ejemplo un UUID»
    [Fact]
    public void Un_uuid_vale_como_identificador()
    {
        const string Uuid = "3f2a8c1e-5b7d-4e9a-a0c2-1d6f8b4e7a90";
        Project project = Open(WithId("clip", Uuid));
        Assert.Equal(Uuid, project.Sequences[0].VideoTracks[0].Clips[0].Id);
    }

    private static JsonObject WithId(string what, string id) => With(r =>
    {
        Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform));
        Seq(r)["markers"] = new JsonArray(new JsonObject { ["id"] = "marker-1", ["time"] = 0 });
        Seq(r)["regions"] = new JsonArray(new JsonObject { ["id"] = "region-1", ["start"] = 0, ["end"] = Second });
        JsonObject target = what switch
        {
            "project" => r,
            "media" => Media(r, 2),
            "sequence" => Seq(r),
            "track" => VideoTrack(r),
            "clip" => Clip1(r),
            "effect" => Clip1(r)["effects"]![0]!.AsObject(),
            "marker" => Seq(r)["markers"]![0]!.AsObject(),
            "region" => Seq(r)["regions"]![0]!.AsObject(),
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        target["id"] = id;
    });

    // «Cada medio debe tener una duración positiva.»
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Un_medio_sin_duracion_positiva_se_rechaza(long duration) =>
        Rejected(With(r => Media(r, 2)["duration"] = duration));

    // «Cada secuencia debe tener ancho, alto y frecuencia de muestreo positivos»
    [Theory]
    [InlineData("width", 0)]
    [InlineData("height", 0)]
    [InlineData("sampleRate", 0)]
    [InlineData("width", -1920)]
    public void Una_secuencia_sin_medidas_positivas_se_rechaza(string field, int value) =>
        Rejected(With(r => Seq(r)[field] = value));

    // «y una tasa de cuadros de 1/5 a 1000 por segundo»
    [Theory]
    [InlineData(1, 5)]
    [InlineData(1000, 1)]
    [InlineData(24000, 1001)]
    public void Una_tasa_dentro_del_rango_se_acepta_sin_acotar(long num, long den)
    {
        Project project = Open(With(r => Seq(r)["frameRate"] = new JsonObject { ["num"] = num, ["den"] = den }), out ClampSummary clamped);
        Assert.Equal(new Rational(num, den), project.Sequences[0].FrameRate);
        Assert.Equal(0, clamped.FrameRates);
    }

    // «Un archivo de la v1.0.0 fuera de estos límites se abre igual, con aviso: la tasa se acota al rango»
    [Theory]
    [InlineData(1, 6, 1, 5)]
    [InlineData(1001, 1, 1000, 1)]
    public void Una_tasa_fuera_del_rango_se_acota_con_aviso(long num, long den, long expectedNum, long expectedDen)
    {
        Project project = Open(With(r => Seq(r)["frameRate"] = new JsonObject { ["num"] = num, ["den"] = den }), out ClampSummary clamped);
        Assert.Equal(new Rational(expectedNum, expectedDen), project.Sequences[0].FrameRate);
        Assert.Equal(1, clamped.FrameRates);
    }

    // «Ningún clip termina después de 2^62 ticks (4 611 686 018 427 387 904, unos 207 años)»
    [Fact]
    public void Un_clip_puede_terminar_justo_en_2_a_la_62()
    {
        Assert.Equal(4_611_686_018_427_387_904, Sequence.MaxTime.Ticks);
        Project project = Open(With(r => AudioTrack(r)["clips"]![0]!["start"] = (1L << 62) - 2 * Second), out ClampSummary clamped);
        Assert.Equal(0, clamped.Total);
        Assert.Equal(Sequence.MaxTime, project.Sequences[0].AudioTracks[0].Clips[0].End);
    }

    // «el clip que pasa del final se recorta hasta él»
    [Fact]
    public void Un_clip_que_pasa_del_final_se_recorta_con_aviso()
    {
        Project project = Open(With(r => AudioTrack(r)["clips"]![0]!["start"] = (1L << 62) - Second), out ClampSummary clamped);
        Assert.Equal(1, clamped.TrimmedClips);
        Clip clip = Assert.Single(project.Sequences[0].AudioTracks[0].Clips);
        Assert.Equal(Sequence.MaxTime, clip.End);
        Assert.Equal(Time.FromTicks(Second), clip.SourceOut);
    }

    // «salvo que empiece después del final o tenga velocidad: entonces se quita»
    [Fact]
    public void Un_clip_que_empieza_despues_del_final_se_quita_con_aviso()
    {
        Project project = Open(With(r => AudioTrack(r)["clips"]![0]!["start"] = (1L << 62) + 1), out ClampSummary clamped);
        Assert.Equal("clip-3", Assert.Single(clamped.RemovedClips).Id);
        Assert.Empty(project.Sequences[0].AudioTracks[0].Clips);
    }

    [Fact]
    public void Un_clip_con_velocidad_que_pasa_del_final_se_quita_con_aviso()
    {
        Project project = Open(
            With(r =>
            {
                JsonObject clip = AudioTrack(r)["clips"]![0]!.AsObject();
                clip["start"] = (1L << 62) - Second;
                clip["duration"] = 2 * Second;
                clip["speed"] = new JsonObject { ["rate"] = new JsonObject { ["num"] = 1, ["den"] = 1 }, ["offset"] = 0 };
            }),
            out ClampSummary clamped);
        Assert.Equal("clip-3", Assert.Single(clamped.RemovedClips).Id);
        Assert.Empty(project.Sequences[0].AudioTracks[0].Clips);
    }

    // «Una ruta de medio más larga de lo que admite Windows se conserva»
    [Fact]
    public void Una_ruta_larguisima_se_conserva()
    {
        string path = "C:\\" + new string('a', 40_000) + ".mp4";
        Project project = Open(With(r => Media(r, 2)["path"] = path));
        Assert.Equal(path, project.Media[2].Path);
    }

    // «Los marcadores y las regiones no alargan la secuencia y pueden llegar al final del rango.»
    [Fact]
    public void Marcadores_y_regiones_llegan_al_final_sin_alargar_la_secuencia()
    {
        Project project = Open(With(r =>
        {
            Seq(r)["markers"] = new JsonArray(new JsonObject { ["id"] = "marker-1", ["time"] = 1L << 62 });
            Seq(r)["regions"] = new JsonArray(new JsonObject { ["id"] = "region-1", ["start"] = (1L << 62) - Second, ["end"] = 1L << 62 });
        }), out ClampSummary clamped);

        Assert.Equal(0, clamped.Total);
        Assert.Equal(Time.FromTicks(4 * Second), project.Sequences[0].Duration);
        Assert.Equal(Sequence.MaxTime, project.Sequences[0].Markers[0].Time);
        Assert.Equal(Sequence.MaxTime, project.Sequences[0].Regions[0].End);
    }

    // «Una pista de `videoTracks` debe ser de tipo `video` y una de `audioTracks` de tipo `audio`.»
    [Fact]
    public void Una_pista_de_audio_en_videoTracks_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["kind"] = "audio"));

    [Fact]
    public void Una_pista_de_video_en_audioTracks_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["kind"] = "video"));

    // «Cada clip debe apuntar con `mediaId` a un medio que exista en `media`»
    [Fact]
    public void Un_clip_con_un_medio_que_no_existe_se_rechaza() =>
        Rejected(With(r => Clip1(r)["mediaId"] = "media-nada"));

    // «… llevar `generator` o apuntar con `sequenceId` a otra secuencia del proyecto: exactamente una de las tres.»
    [Fact]
    public void Un_clip_sin_medio_generador_ni_secuencia_se_rechaza() =>
        Rejected(With(r => Clip1(r).Remove("mediaId")));

    [Fact]
    public void Un_clip_con_medio_y_generador_se_rechaza() =>
        Rejected(With(r => Clip1(r)["generator"] = TitleGenerator()));

    [Fact]
    public void Un_clip_con_medio_y_secuencia_se_rechaza() =>
        Rejected(With(r =>
        {
            AddInnerSequence(r);
            Clip1(r)["sequenceId"] = "sequence-2";
        }));

    [Fact]
    public void Un_clip_con_generador_y_secuencia_se_rechaza() =>
        Rejected(With(r =>
        {
            AddInnerSequence(r);
            JsonObject clip = Clip1(r);
            clip.Remove("mediaId");
            clip["generator"] = TitleGenerator();
            clip["sequenceId"] = "sequence-2";
        }));

    [Fact]
    public void Un_clip_compuesto_de_otra_secuencia_se_acepta()
    {
        Project project = Open(With(r =>
        {
            AddInnerSequence(r);
            JsonObject clip = Clip1(r);
            clip.Remove("mediaId");
            clip["sequenceId"] = "sequence-2";
        }));
        Assert.Equal("sequence-2", project.Sequences[0].VideoTracks[0].Clips[0].SequenceId);
    }

    [Fact]
    public void Un_clip_compuesto_de_una_secuencia_que_no_existe_se_rechaza() =>
        Rejected(With(r =>
        {
            JsonObject clip = Clip1(r);
            clip.Remove("mediaId");
            clip["sequenceId"] = "sequence-nada";
        }));

    private static void AddInnerSequence(JsonObject root) =>
        root["sequences"]!.AsArray().Add(new JsonObject { ["id"] = "sequence-2", ["name"] = "Interior" });

    private static JsonNode TitleGenerator()
    {
        var project = new Project
        {
            Id = "p",
            Sequences =
            [
                new Sequence
                {
                    Id = "s",
                    VideoTracks =
                    [
                        new Track
                        {
                            Id = "t",
                            Kind = TrackKind.Video,
                            Clips = [new Clip { Id = "c", SourceOut = Time.FromTicks(Second), Generator = new ClipGenerator { Text = "Hola", Style = new TextStyle() } }],
                        },
                    ],
                },
            ],
        };
        JsonObject root = JsonNode.Parse(ProjectSerializer.Serialize(project))!.AsObject();
        return root["sequences"]![0]!["videoTracks"]![0]!["clips"]![0]!["generator"]!.DeepClone();
    }

    // «El `sourceIn` de un clip de medio no puede ser negativo (el de un generador sí)»
    [Fact]
    public void Un_clip_de_medio_con_sourceIn_negativo_se_rechaza() =>
        Rejected(With(r => Clip1(r)["sourceIn"] = -1));

    [Fact]
    public void Un_generador_con_sourceIn_negativo_se_acepta()
    {
        Project project = Open(With(r =>
        {
            JsonObject clip = Clip1(r);
            clip.Remove("mediaId");
            clip["generator"] = TitleGenerator();
            clip["sourceIn"] = -Second;
            clip["sourceOut"] = Second;
        }));
        Assert.Equal(Time.FromTicks(-Second), project.Sequences[0].VideoTracks[0].Clips[0].SourceIn);
    }

    // «su `sourceOut` debe ser mayor que su `sourceIn`»
    [Theory]
    [InlineData(Second)]
    [InlineData(Second / 2)]
    public void Un_clip_con_sourceOut_no_mayor_que_sourceIn_se_rechaza(long sourceOut) =>
        Rejected(With(r =>
        {
            Clip1(r)["sourceIn"] = Second;
            Clip1(r)["sourceOut"] = sourceOut;
        }));

    // «y no puede pasar de la duración del medio»
    [Fact]
    public void Un_clip_que_pasa_de_la_duracion_del_medio_se_rechaza() =>
        Rejected(With(r => Clip2(r)["sourceOut"] = 10 * Second + 1));

    [Fact]
    public void Un_clip_que_llega_justo_al_final_del_medio_se_acepta()
    {
        Project project = Open(With(r =>
        {
            Clip2(r)["sourceIn"] = 8 * Second;
            Clip2(r)["sourceOut"] = 10 * Second;
        }));
        Assert.Equal(Time.FromTicks(10 * Second), project.Sequences[0].VideoTracks[0].Clips[1].SourceOut);
    }

    // «y su `start` no puede ser negativo»
    [Fact]
    public void Un_clip_que_empieza_antes_de_cero_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["clips"]![0]!["start"] = -1));

    // «Un clip en una pista de video necesita un medio con imagen (`hasVideo`)»
    [Fact]
    public void Un_clip_de_video_con_un_medio_sin_imagen_se_rechaza() =>
        Rejected(With(r => Clip1(r)["mediaId"] = "media-sound"));

    // «y uno en una pista de audio, un medio con sonido (`hasAudio`)»
    [Fact]
    public void Un_clip_de_audio_con_un_medio_sin_sonido_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["clips"]![0]!["mediaId"] = "media-image"));

    // «Dentro de una pista, los clips van ordenados por `start` y no se solapan: cada uno empieza en el final del
    // anterior o después.»
    [Fact]
    public void Clips_fuera_de_orden_se_rechazan() =>
        Rejected(With(r =>
        {
            JsonArray clips = VideoClips(r);
            JsonNode second = clips[1]!;
            clips.RemoveAt(1);
            clips.Insert(0, second);
        }));

    [Fact]
    public void Clips_que_se_solapan_se_rechazan() =>
        Rejected(With(r => Clip2(r)["start"] = 2 * Second - 1));

    // «Los huecos entre clips no se guardan; son simplemente el espacio que queda entre uno y el siguiente.»
    [Fact]
    public void Un_hueco_entre_clips_es_solo_espacio()
    {
        Project project = Open(With(r => Clip2(r)["start"] = 5 * Second));
        Assert.Equal(Time.FromTicks(5 * Second), project.Sequences[0].VideoTracks[0].Clips[1].Start);
        Assert.Equal(2, project.Sequences[0].VideoTracks[0].Clips.Count);
    }

    // «Cada bus tiene nombre»
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_bus_sin_nombre_se_rechaza(string name) =>
        Rejected(With(r => Seq(r)["audioBuses"]![0]!["name"] = name));

    // «y solo efectos de audio»
    [Fact]
    public void Un_bus_con_un_efecto_de_video_se_rechaza() =>
        Rejected(With(r => Seq(r)["audioBuses"]![0]!["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform))));

    [Fact]
    public void Un_bus_con_un_efecto_de_audio_se_acepta()
    {
        Project project = Open(With(r => Seq(r)["audioBuses"]![0]!["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Audio))));
        Assert.Single(project.Sequences[0].AudioBuses[0].Effects);
    }

    // «El `output` y los `sends` de una pista de audio apuntan a buses de su secuencia»
    [Fact]
    public void Una_salida_a_un_bus_que_no_existe_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["output"] = "bus-nada"));

    [Fact]
    public void Un_envio_a_un_bus_que_no_existe_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["sends"] = new JsonArray(new JsonObject { ["busId"] = "bus-nada", ["gainDb"] = 0 })));

    [Fact]
    public void Una_salida_a_un_bus_de_otra_secuencia_se_rechaza() =>
        Rejected(With(r =>
        {
            r["sequences"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "sequence-2",
                ["audioBuses"] = new JsonArray(new JsonObject { ["id"] = "bus-2", ["name"] = "Otro" }),
            });
            AudioTrack(r)["output"] = "bus-2";
        }));

    [Fact]
    public void Salida_y_envio_a_un_bus_de_la_secuencia_se_aceptan()
    {
        Project project = Open(With(r =>
        {
            AudioTrack(r)["output"] = "bus-1";
            AudioTrack(r)["sends"] = new JsonArray(new JsonObject { ["busId"] = "bus-1", ["gainDb"] = -12 });
        }));
        Assert.Equal("bus-1", project.Sequences[0].AudioTracks[0].Output);
    }

    // «con como mucho un envío por bus»
    [Fact]
    public void Dos_envios_al_mismo_bus_se_rechazan() =>
        Rejected(With(r => AudioTrack(r)["sends"] = new JsonArray(
            new JsonObject { ["busId"] = "bus-1", ["gainDb"] = 0 },
            new JsonObject { ["busId"] = "bus-1", ["gainDb"] = -6 })));

    // «una pista de video no lleva `output`, `sends`, `solo` ni `armed`»
    [Fact]
    public void Una_pista_de_video_con_salida_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["output"] = "bus-1"));

    [Fact]
    public void Una_pista_de_video_con_envios_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["sends"] = new JsonArray(new JsonObject { ["busId"] = "bus-1", ["gainDb"] = 0 })));

    [Theory]
    [InlineData("solo")]
    [InlineData("armed")]
    public void Una_pista_de_video_en_solo_o_armada_se_rechaza(string field) =>
        Rejected(With(r => VideoTrack(r)[field] = true));

    // «Un marcador no puede estar antes del instante cero»
    [Fact]
    public void Un_marcador_antes_de_cero_se_rechaza() =>
        Rejected(With(r => Seq(r)["markers"] = new JsonArray(new JsonObject { ["id"] = "marker-1", ["time"] = -1 })));

    // «y una región tampoco puede empezar antes y debe terminar después de empezar»
    [Fact]
    public void Una_region_que_empieza_antes_de_cero_se_rechaza() =>
        Rejected(With(r => Seq(r)["regions"] = new JsonArray(new JsonObject { ["id"] = "region-1", ["start"] = -1, ["end"] = Second })));

    [Theory]
    [InlineData(Second)]
    [InlineData(Second - 1)]
    public void Una_region_que_no_termina_despues_de_empezar_se_rechaza(long end) =>
        Rejected(With(r => Seq(r)["regions"] = new JsonArray(new JsonObject { ["id"] = "region-1", ["start"] = Second, ["end"] = end })));

    // «Los keyframes de cada parámetro van en orden estrictamente creciente de `time`, sin dos en el mismo instante.»
    [Theory]
    [InlineData(0, 0)]
    [InlineData(Second, 0)]
    public void Keyframes_sin_orden_estricto_se_rechazan(long first, long second) =>
        Rejected(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform, new JsonObject
        {
            ["opacity"] = new JsonObject
            {
                ["value"] = 1,
                ["keyframes"] = new JsonArray(
                    new JsonObject { ["time"] = first, ["value"] = 0 },
                    new JsonObject { ["time"] = second, ["value"] = 1 }),
            },
        }))));

    [Fact]
    public void Keyframes_sin_orden_estricto_en_un_tipo_desconocido_tambien_se_rechazan() =>
        Rejected(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", "otro.programa", new JsonObject
        {
            ["cosa"] = new JsonObject
            {
                ["value"] = 1,
                ["keyframes"] = new JsonArray(
                    new JsonObject { ["time"] = 0, ["value"] = 0 },
                    new JsonObject { ["time"] = 0, ["value"] = 1 }),
            },
        }))));

    // «Un mismo `linkId` puede repetirse en varias pistas»
    [Fact]
    public void Un_enlace_en_varias_pistas_se_acepta()
    {
        Project project = Open(With(r =>
        {
            Clip1(r)["linkId"] = "link-1";
            AudioTrack(r)["clips"]![0]!["linkId"] = "link-1";
        }));
        Assert.Equal("link-1", project.Sequences[0].AudioTracks[0].Clips[0].LinkId);
    }

    // «pero nunca en dos clips de la misma pista»
    [Fact]
    public void Un_enlace_en_dos_clips_de_la_misma_pista_se_rechaza() =>
        Rejected(With(r =>
        {
            Clip1(r)["linkId"] = "link-1";
            Clip2(r)["linkId"] = "link-1";
        }));

    // «Cada transición une dos clips contiguos de su propia pista (`to` es el siguiente de `from` y empieza justo donde
    // este termina)»
    [Fact]
    public void Una_transicion_entre_dos_clips_contiguos_se_acepta()
    {
        Project project = Open(With(r => VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second))));
        Assert.Single(project.Sequences[0].VideoTracks[0].Transitions);
    }

    [Fact]
    public void Una_transicion_entre_clips_separados_por_un_hueco_se_rechaza() =>
        Rejected(With(r =>
        {
            Clip2(r)["start"] = 3 * Second;
            VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second));
        }));

    [Fact]
    public void Una_transicion_al_reves_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-2", "clip-1", Second))));

    [Fact]
    public void Una_transicion_con_un_clip_de_otra_pista_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-3", Second))));

    [Fact]
    public void Una_transicion_en_otra_pista_que_la_de_sus_clips_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second))));

    // «cada corte tiene como mucho una»
    [Fact]
    public void Dos_transiciones_en_el_mismo_corte_se_rechazan() =>
        Rejected(With(r => VideoTrack(r)["transitions"] = new JsonArray(
            Transition("transition-1", "clip-1", "clip-2", Second / 2),
            Transition("transition-2", "clip-1", "clip-2", Second / 2))));

    // «y su duración es positiva»
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Una_transicion_sin_duracion_positiva_se_rechaza(long duration) =>
        Rejected(With(r => VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", duration))));

    // «y no pasa de la del más corto de sus clips» (centrada, la alineación por defecto)
    [Fact]
    public void Una_transicion_centrada_mas_larga_que_el_clip_mas_corto_se_rechaza() =>
        Rejected(With(r =>
        {
            Clip2(r)["sourceOut"] = Second;
            VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second + 1));
        }));

    [Fact]
    public void Una_transicion_centrada_tan_larga_como_el_clip_mas_corto_se_acepta()
    {
        Project project = Open(With(r =>
        {
            Clip2(r)["sourceOut"] = Second;
            VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second));
        }));
        Assert.Equal(Time.FromTicks(Second), project.Sequences[0].VideoTracks[0].Transitions[0].Duration);
    }

    // «En los tipos registrados, cada parámetro (y cada keyframe) es un número finito dentro de los límites del
    // registro: una opacidad de 2 o un ángulo de barrido de 720 se rechazan»
    [Fact]
    public void Una_opacidad_de_2_se_rechaza() =>
        Rejected(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform, new JsonObject
        {
            ["opacity"] = new JsonObject { ["value"] = 2, ["keyframes"] = new JsonArray() },
        }))));

    [Fact]
    public void Un_keyframe_de_opacidad_2_se_rechaza() =>
        Rejected(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform, new JsonObject
        {
            ["opacity"] = new JsonObject
            {
                ["value"] = 1,
                ["keyframes"] = new JsonArray(new JsonObject { ["time"] = 0, ["value"] = 2 }),
            },
        }))));

    [Fact]
    public void Un_angulo_de_barrido_de_720_se_rechaza() =>
        Rejected(With(r =>
        {
            JsonObject transition = Transition("transition-1", "clip-1", "clip-2", Second, BuiltInTransitions.Wipe);
            transition["parameters"] = new JsonObject { [BuiltInTransitions.WipeParameters.Angle] = 720 };
            VideoTrack(r)["transitions"] = new JsonArray(transition);
        }));

    [Fact]
    public void Un_numero_no_finito_se_rechaza() =>
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(
            With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform, new JsonObject
            {
                ["opacity"] = new JsonObject { ["value"] = 1, ["keyframes"] = new JsonArray() },
            }))).ToJsonString().Replace("\"value\":1", "\"value\":1e999", StringComparison.Ordinal)));

    // «los parámetros de tipos desconocidos no se miran»
    [Fact]
    public void Los_parametros_de_un_tipo_desconocido_no_se_miran()
    {
        Project project = Open(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", "otro.programa", new JsonObject
        {
            ["opacity"] = new JsonObject { ["value"] = 1e300, ["keyframes"] = new JsonArray() },
        }))));
        Assert.Equal(1e300, project.Sequences[0].VideoTracks[0].Clips[0].Effects[0].Parameters["opacity"].Value);
    }

    // «Un tipo registrado como transición no puede ir en una lista de efectos»
    [Fact]
    public void Una_transicion_en_la_lista_de_efectos_de_un_clip_se_rechaza() =>
        Rejected(With(r => Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInTransitions.Crossfade))));

    [Fact]
    public void Una_transicion_en_la_lista_de_efectos_de_una_pista_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInTransitions.Crossfade))));

    // «ni uno de efecto en `transitions`»
    [Fact]
    public void Un_efecto_en_transitions_se_rechaza() =>
        Rejected(With(r => VideoTrack(r)["transitions"] = new JsonArray(Transition("transition-1", "clip-1", "clip-2", Second, BuiltInEffects.Transform))));

    // «y las opciones y los efectos de pista siguen las reglas de la sección anterior» (solo efectos de su clase)
    [Fact]
    public void Un_efecto_de_video_en_una_pista_de_audio_se_rechaza() =>
        Rejected(With(r => AudioTrack(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform))));

    // «Además, el archivo debe ser JSON estricto: sin comentarios»
    [Fact]
    public void Un_comentario_se_rechaza() =>
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize("// hola\n" + Base().ToJsonString()));

    // «sin comas sobrantes»
    [Fact]
    public void Una_coma_sobrante_se_rechaza()
    {
        string json = Base().ToJsonString();
        Assert.EndsWith("}", json, StringComparison.Ordinal);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json[..^1] + ",}"));
    }

    // «y sin el mismo campo repetido dentro de un objeto, porque no habría forma de saber cuál vale»
    [Fact]
    public void Un_campo_repetido_se_rechaza()
    {
        string json = Base().ToJsonString();
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize("{\"name\":\"otro\"," + json[1..]));
    }

    [Fact]
    public void Un_campo_repetido_dentro_de_un_clip_se_rechaza()
    {
        string json = Base().ToJsonString();
        string repeated = json.Replace("\"id\":\"clip-1\"", "\"id\":\"clip-1\",\"start\":0", StringComparison.Ordinal);
        Assert.NotEqual(json, repeated);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(repeated));
    }

    // «Las listas y las `options` no pueden contener `null`» (y, como toda regla, el rechazo la enumera)
    [Theory]
    [InlineData("media")]
    [InlineData("sequences")]
    [InlineData("videoTracks")]
    [InlineData("clips")]
    [InlineData("effects")]
    [InlineData("markers")]
    [InlineData("regions")]
    [InlineData("transitions")]
    [InlineData("keyframes")]
    [InlineData("audioBuses")]
    [InlineData("busEffects")]
    [InlineData("masterEffects")]
    [InlineData("sends")]
    public void Un_null_dentro_de_una_lista_se_rechaza(string list)
    {
        JsonObject root = With(r =>
        {
            Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform, new JsonObject
            {
                ["opacity"] = new JsonObject { ["value"] = 1, ["keyframes"] = new JsonArray() },
            }));
            JsonArray target = list switch
            {
                "media" => r["media"]!.AsArray(),
                "sequences" => r["sequences"]!.AsArray(),
                "videoTracks" => Seq(r)["videoTracks"]!.AsArray(),
                "clips" => VideoClips(r),
                "effects" => Clip1(r)["effects"]!.AsArray(),
                "markers" => Seq(r)["markers"]!.AsArray(),
                "regions" => Seq(r)["regions"]!.AsArray(),
                "transitions" => VideoTrack(r)["transitions"]!.AsArray(),
                "keyframes" => Clip1(r)["effects"]![0]!["parameters"]!["opacity"]!["keyframes"]!.AsArray(),
                "audioBuses" => Seq(r)["audioBuses"]!.AsArray(),
                "busEffects" => (JsonArray)(Seq(r)["audioBuses"]![0]!["effects"] = new JsonArray()),
                "masterEffects" => (JsonArray)(Seq(r)["masterEffects"] = new JsonArray()),
                "sends" => (JsonArray)(AudioTrack(r)["sends"] = new JsonArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(list)),
            };
            target.Add(null);
        });

        if (list == "sends")
        {
            ProjectFormatException exception = Assert.Throws<ProjectFormatException>(() => Open(root));
            Assert.Contains("Hay un elemento vacío (null) en la lista de envíos.", exception.Problems);
            return;
        }

        RejectedWithProblems(root);
    }

    [Fact]
    public void Un_null_en_las_opciones_de_un_efecto_se_rechaza() =>
        Rejected(With(r =>
        {
            JsonObject effect = Effect("effect-1", "otro.programa");
            effect["options"] = new JsonObject { ["modo"] = null };
            Clip1(r)["effects"] = new JsonArray(effect);
        }));

    [Fact]
    public void Un_null_en_las_opciones_de_una_transicion_se_rechaza() =>
        Rejected(With(r =>
        {
            JsonObject transition = Transition("transition-1", "clip-1", "clip-2", Second);
            transition["options"] = new JsonObject { ["modo"] = null };
            VideoTrack(r)["transitions"] = new JsonArray(transition);
        }));

    // «y los campos de texto, listas y objetos tampoco»
    [Theory]
    [InlineData("project.name")]
    [InlineData("project.id")]
    [InlineData("project.media")]
    [InlineData("media.path")]
    [InlineData("sequence.frameRate")]
    [InlineData("sequence.videoTracks")]
    [InlineData("track.name")]
    [InlineData("track.clips")]
    [InlineData("clip.effects")]
    [InlineData("effect.parameters")]
    [InlineData("effect.options")]
    [InlineData("effect.type")]
    public void Un_null_en_un_campo_de_texto_lista_u_objeto_se_rechaza(string field) =>
        Rejected(With(r =>
        {
            Clip1(r)["effects"] = new JsonArray(Effect("effect-1", BuiltInEffects.Transform));
            (JsonObject target, string name) = field switch
            {
                "project.name" => (r, "name"),
                "project.id" => (r, "id"),
                "project.media" => (r, "media"),
                "media.path" => (Media(r, 0), "path"),
                "sequence.frameRate" => (Seq(r), "frameRate"),
                "sequence.videoTracks" => (Seq(r), "videoTracks"),
                "track.name" => (VideoTrack(r), "name"),
                "track.clips" => (VideoTrack(r), "clips"),
                "clip.effects" => (Clip1(r), "effects"),
                "effect.parameters" => (Clip1(r)["effects"]![0]!.AsObject(), "parameters"),
                "effect.options" => (Clip1(r)["effects"]![0]!.AsObject(), "options"),
                "effect.type" => (Clip1(r)["effects"]![0]!.AsObject(), "type"),
                _ => throw new ArgumentOutOfRangeException(nameof(field)),
            };
            target[name] = null;
        }));

    // «los únicos campos que admiten `null` son `linkId`, el `frameRate` de un medio, `speed`, `duration`, `curve`,
    // `mediaId`, `generator` y `sequenceId` en un clip, `role` y `output` en una pista … ; `null` vale lo mismo que no
    // escribirlo.»
    [Theory]
    [InlineData("clip.linkId")]
    [InlineData("media.frameRate")]
    [InlineData("clip.speed")]
    [InlineData("clip.duration")]
    [InlineData("clip.generator")]
    [InlineData("clip.sequenceId")]
    [InlineData("track.role")]
    [InlineData("track.output")]
    public void Un_null_admitido_vale_lo_mismo_que_no_escribirlo(string field)
    {
        (JsonObject Target, string Name) Pick(JsonObject r) => field switch
        {
            "clip.linkId" => (Clip1(r), "linkId"),
            "media.frameRate" => (Media(r, 0), "frameRate"),
            "clip.speed" => (Clip1(r), "speed"),
            "clip.duration" => (Clip1(r), "duration"),
            "clip.generator" => (Clip1(r), "generator"),
            "clip.sequenceId" => (Clip1(r), "sequenceId"),
            "track.role" => (VideoTrack(r), "role"),
            "track.output" => (AudioTrack(r), "output"),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        JsonObject withNull = With(r =>
        {
            (JsonObject target, string name) = Pick(r);
            target[name] = null;
        });
        JsonObject without = With(r =>
        {
            (JsonObject target, string name) = Pick(r);
            target.Remove(name);
        });

        Assert.Equal(ProjectSerializer.Serialize(Open(without)), ProjectSerializer.Serialize(Open(withNull)));
    }

    [Fact]
    public void Un_mediaId_null_en_un_generador_vale_lo_mismo_que_no_escribirlo()
    {
        JsonObject Title(bool writeNull) => With(r =>
        {
            JsonObject clip = Clip1(r);
            clip.Remove("mediaId");
            if (writeNull)
            {
                clip["mediaId"] = null;
            }

            clip["generator"] = TitleGenerator();
        });

        Assert.Equal(ProjectSerializer.Serialize(Open(Title(false))), ProjectSerializer.Serialize(Open(Title(true))));
    }

    [Fact]
    public void Una_curva_null_en_la_velocidad_vale_lo_mismo_que_no_escribirla()
    {
        JsonObject Speed(bool writeNull) => With(r =>
        {
            JsonObject clip = Clip1(r);
            clip["duration"] = Second;
            var speed = new JsonObject { ["rate"] = new JsonObject { ["num"] = 2, ["den"] = 1 }, ["offset"] = 0 };
            if (writeNull)
            {
                speed["curve"] = null;
            }

            clip["speed"] = speed;
        });

        Assert.Equal(ProjectSerializer.Serialize(Open(Speed(false))), ProjectSerializer.Serialize(Open(Speed(true))));
    }

    // «Un campo que falta toma su valor por defecto: listas vacías, números en 0, `enabled` en verdadero, la secuencia
    // en 1920 × 1080 a 30 fps y 48 kHz, la interpolación en `linear`.»
    [Fact]
    public void Un_campo_que_falta_toma_su_valor_por_defecto()
    {
        const string Json = """
            {
              "schemaVersion": 13,
              "id": "project-1",
              "media": [ { "id": "media-1", "path": "toma.mp4", "duration": 7056000000, "hasVideo": true } ],
              "sequences": [
                {
                  "id": "sequence-1",
                  "videoTracks": [
                    {
                      "id": "track-1",
                      "kind": "video",
                      "clips": [
                        {
                          "id": "clip-1",
                          "mediaId": "media-1",
                          "sourceOut": 705600000,
                          "effects": [
                            { "id": "effect-1", "type": "rodaje.transform",
                              "parameters": { "opacity": { "value": 1, "keyframes": [ { "time": 0, "value": 0 } ] } } }
                          ]
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;

        Project project = ProjectSerializer.Deserialize(Json);
        Sequence sequence = Assert.Single(project.Sequences);
        Assert.Equal(1920, sequence.Width);
        Assert.Equal(1080, sequence.Height);
        Assert.Equal(new Rational(30, 1), sequence.FrameRate);
        Assert.Equal(48000, sequence.SampleRate);
        Assert.Empty(sequence.AudioTracks);
        Assert.Empty(sequence.Markers);
        Assert.Empty(sequence.Regions);
        Assert.Empty(sequence.AudioBuses);

        MediaRef media = Assert.Single(project.Media);
        Assert.Equal(0, media.Width);
        Assert.Equal(0, media.Height);
        Assert.Equal(0, media.SampleRate);
        Assert.Equal(0, media.AudioChannels);
        Assert.False(media.HasAudio);

        Track track = Assert.Single(sequence.VideoTracks);
        Assert.Empty(track.Effects);
        Assert.Empty(track.Transitions);
        Clip clip = Assert.Single(track.Clips);
        Assert.True(clip.Enabled);
        Assert.Equal(Time.Zero, clip.SourceIn);
        Assert.Equal(Time.Zero, clip.Start);
        EffectInstance effect = Assert.Single(clip.Effects);
        Assert.True(effect.Enabled);
        Assert.Empty(effect.Options);
        Assert.Equal(Interpolation.Linear, Assert.Single(effect.Parameters["opacity"].Keyframes).Interpolation);
    }

    // «Los campos desconocidos se ignoran al leer … pero esos campos no se conservan cuando Rodaje vuelve a guardar.»
    [Fact]
    public void Los_campos_desconocidos_se_ignoran_y_no_se_conservan()
    {
        JsonObject root = With(r =>
        {
            r["miPrograma"] = new JsonObject { ["version"] = 3 };
            Seq(r)["miCampo"] = "x";
            Clip1(r)["etiquetas"] = new JsonArray("a", "b");
        });

        Project project = Open(root);
        string saved = ProjectSerializer.Serialize(project);

        Assert.Equal(ProjectSerializer.Serialize(Open(Base())), saved);
        Assert.DoesNotContain("miPrograma", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("miCampo", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("etiquetas", saved, StringComparison.Ordinal);
    }
}
