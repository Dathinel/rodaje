using System;
using System.Collections.Generic;
using System.Linq;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Una prueba por promesa verificable de la sección «Modelo (<c>Model/</c>)» de <c>docs/arquitectura/nucleo.md</c>, sin
/// interfaz. Cada una cita la frase que comprueba.
/// </summary>
public class ModelSectionCazaTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
        new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };

    private static Clip Title(string id, double start, double sourceIn, double length) =>
        new() { Id = id, Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), Generator = new ClipGenerator { Text = "hola", Style = Rodaje.Core.Tests.Model.ModelSamples.FullStyle() } };

    private static MediaRef Media(string id = "m", double seconds = 60) =>
        new() { Id = id, Path = id + ".mp4", Name = id, Duration = S(seconds), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 };

    private static Sequence Seq(string id, IEnumerable<Clip>? v1 = null, IEnumerable<Clip>? a1 = null, string track = "") => new()
    {
        Id = id,
        VideoTracks = [new Track { Id = id + "-v1" + track, Kind = TrackKind.Video, Name = "Video 1", Clips = [.. v1 ?? []] }],
        AudioTracks = [new Track { Id = id + "-a1" + track, Kind = TrackKind.Audio, Name = "Audio 1", Clips = [.. a1 ?? []] }],
    };

    private static Project P(params Sequence[] sequences) => new() { Id = "p", Media = [Media()], Sequences = [.. sequences] };

    private static EditContext Ctx(Project project) => new(project, project.Sequences[0], new SequentialIdGenerator());

    private static IReadOnlyList<string> Errors(Project project) => ModelValidator.Validate(project);

    // ── Árbol y no destrucción ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«<c>SchemaVersion</c> (hoy 13)».</summary>
    [Fact]
    public void Project_SchemaVersion_es_13()
    {
        Assert.Equal(13, Project.CurrentSchemaVersion);
        Assert.Equal(13, new Project().SchemaVersion);
    }

    /// <summary>«La edición es no destructiva: un clip es una referencia a un medio (<c>MediaId</c>) más tres tiempos».</summary>
    [Fact]
    public void Cortar_un_clip_no_toca_el_medio()
    {
        Project project = P(Seq("s", [C("a", 0, 0, 10)]));
        MediaRef before = project.Media[0].Clone();

        TimelineEdits.Split(Ctx(project), "a", S(4));

        MediaRef after = project.Media[0];
        Assert.Equal(before.Path, after.Path);
        Assert.Equal(before.Duration, after.Duration);
        Assert.All(project.Sequences[0].VideoTracks[0].Clips, c => Assert.Equal("m", c.MediaId));
    }

    /// <summary>«sin velocidad <c>ToSourceTime(t) = SourceIn + (t − Start)</c> y <c>Duration = SourceOut − SourceIn</c>».</summary>
    [Fact]
    public void Clip_sin_velocidad_mapea_por_desplazamiento()
    {
        Clip clip = C("a", 5, 2, 10);

        Assert.Equal(S(10), clip.Duration);
        Assert.Equal(S(2 + (8 - 5)), clip.ToSourceTime(S(8)));
    }

    /// <summary>«con ella la duración se guarda y los tiempos pasan por <c>TimeMap</c>».</summary>
    [Fact]
    public void Clip_con_velocidad_guarda_la_duracion_y_pasa_por_TimeMap()
    {
        Clip clip = C("a", 0, 0, 10);
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        clip.Duration = S(5);

        Assert.Equal(S(5), clip.Duration);
        Assert.Equal(clip.TimeMap.ExtendedSource(S(1)), clip.ToSourceTime(S(1)));
        Assert.Equal(S(2), clip.ToSourceTime(S(1)));
    }

    /// <summary>«<c>Speed</c> (nula a 1×)».</summary>
    [Fact]
    public void Velocidad_nula_es_1x()
    {
        Project project = P(Seq("s", [C("a", 0, 0, 10)]));

        TimelineEdits.SetSpeed(Ctx(project), "a", new Rational(2, 1));
        TimelineEdits.SetSpeed(Ctx(project), "a", new Rational(1, 1));

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.True(clip.Speed is null || clip.Speed.IsIdentity);
        Assert.Equal(S(10), clip.Duration);
    }

    // ── Títulos ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Un título no tiene medio: lleva un <c>ClipGenerator</c> […] y un origen virtual sin límites, así que su <c>SourceIn</c> puede ser negativo».</summary>
    [Fact]
    public void Titulo_sin_medio_admite_SourceIn_negativo()
    {
        Project project = P(Seq("s", [Title("t", 0, -3, 5)]));

        Assert.Empty(Errors(project));

        Project media = P(Seq("s", [C("a", 0, -3, 5)]));
        Assert.Contains(Errors(media), e => e.Contains("punto de entrada negativo", StringComparison.Ordinal));
    }

    /// <summary>«un origen virtual sin límites, así que su <c>SourceIn</c> puede ser negativo»: recortar el inicio de un título lo deja negativo.</summary>
    [Fact]
    public void Recortar_el_inicio_de_un_titulo_deja_SourceIn_negativo()
    {
        Project project = P(Seq("s", [Title("t", 5, 0, 5)]));

        TimelineEdits.Trim(Ctx(project), "t", TrimEdge.In, S(-2));

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.Equal(S(-2), clip.SourceIn);
        Assert.Equal(S(3), clip.Start);

        Project media = P(Seq("s", [C("a", 5, 0, 5)]));
        Assert.ThrowsAny<EditException>(() => TimelineEdits.Trim(Ctx(media), "a", TrimEdge.In, S(-2)));
    }

    /// <summary>«dividirlo apaga los bordes internos».</summary>
    [Fact]
    public void Dividir_un_titulo_apaga_los_bordes_internos()
    {
        Project project = P(Seq("s", [Title("t", 0, 0, 10)]));

        string right = TimelineEdits.Split(Ctx(project), "t", S(4))[0];

        Track track = project.Sequences[0].VideoTracks[0];
        GeneratorEdges left = track.Clips.Single(c => c.Id == "t").Generator!.Edges;
        GeneratorEdges rightEdges = track.Clips.Single(c => c.Id == right).Generator!.Edges;
        Assert.True(left.In);
        Assert.False(left.Out);
        Assert.False(rightEdges.In);
        Assert.True(rightEdges.Out);
    }

    /// <summary>«el slip y la velocidad no se le aplican».</summary>
    [Fact]
    public void Slip_y_velocidad_no_se_aplican_a_un_titulo()
    {
        Project project = P(Seq("s", [Title("t", 0, 0, 10)]));

        Assert.Throws<EditException>(() => TimelineEdits.Slip(Ctx(project), "t", S(1)));
        Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(Ctx(project), "t", new Rational(2, 1)));

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.Equal(S(0), clip.SourceIn);
        Assert.Null(clip.Speed);
    }

    // ── Medios, secuencias y pistas ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Ruta (relativa al proyecto si se puede)»: una ruta relativa se resuelve contra la carpeta del proyecto.</summary>
    [Fact]
    public void MediaRef_resuelve_la_ruta_relativa_contra_el_proyecto()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "proyecto");
        var media = new MediaRef { Path = System.IO.Path.Combine("tomas", "a.mp4") };

        Assert.Equal(System.IO.Path.Combine(dir, "tomas", "a.mp4"), media.ResolvePath(dir));
    }

    /// <summary>«<c>FindClip</c> y <c>GetLinkGroup</c>»: el clip con su pista, y el clip pedido primero con sus enlazados.</summary>
    [Fact]
    public void Sequence_FindClip_y_GetLinkGroup()
    {
        Project project = P(Seq("s", [C("v", 0, 0, 10, "l"), C("w", 10, 10, 5)], [C("a", 0, 0, 10, "l")]));
        Sequence sequence = project.Sequences[0];

        Assert.Equal("s-a1", sequence.FindClip("a")!.Value.Track.Id);
        Assert.Null(sequence.FindClip("nada"));
        List<(Track Track, Clip Clip)> group = sequence.GetLinkGroup("a");
        Assert.Equal(["a", "v"], group.Select(g => g.Clip.Id));
        Assert.Single(sequence.GetLinkGroup("w"));
    }

    /// <summary>«<c>Markers</c> (<c>Time</c>, <c>Name</c>, <c>Color</c> y <c>Note</c> opcionales) y <c>Regions</c> (<c>[Start, End)</c>, […])».</summary>
    [Fact]
    public void Marcadores_y_regiones_validos_e_invalidos()
    {
        Sequence sequence = Seq("s");
        sequence.Markers.Add(new Marker { Id = "mk", Time = S(1), Name = "uno" });
        sequence.Regions.Add(new Region { Id = "rg", Start = S(1), End = S(2), Name = "dos", Color = "#FF0000", Note = "nota" });
        Assert.Empty(Errors(P(sequence)));

        sequence.Regions[0].End = S(1);
        Assert.Contains(Errors(P(sequence)), e => e.Contains("«rg»", StringComparison.Ordinal));
    }

    /// <summary>«<c>Height</c> (formato 11, solo vista: vale aunque esté bloqueada)».</summary>
    [Fact]
    public void Alto_de_pista_vale_aunque_este_bloqueada()
    {
        Project project = P(Seq("s"));
        Track track = project.Sequences[0].VideoTracks[0];
        track.Locked = true;

        TimelineEdits.SetTrackHeight(Ctx(project), track.Id, 100);

        Assert.Equal(100, track.Height);
    }

    /// <summary>«los clips ordenados por <c>Start</c> y sin solaparse (los huecos no son objetos)».</summary>
    [Fact]
    public void Clips_ordenados_sin_solaparse_y_huecos_sin_objeto()
    {
        Project gap = P(Seq("s", [C("a", 0, 0, 5), C("b", 8, 5, 5)]));
        Assert.Empty(Errors(gap));
        Assert.Equal(2, gap.Sequences[0].VideoTracks[0].Clips.Count);

        Project overlap = P(Seq("s", [C("a", 0, 0, 5), C("b", 4, 5, 5)]));
        Assert.Contains(Errors(overlap), e => e.Contains("se solapa", StringComparison.Ordinal));

        Project unordered = P(Seq("s", [C("b", 8, 5, 5), C("a", 0, 0, 5)]));
        Assert.Contains(Errors(unordered), e => e.Contains("fuera de orden", StringComparison.Ordinal));
    }

    /// <summary>«<c>ClipSpeed</c>: Velocidad constante (<c>Rate</c>, de 1/10× a 100×)».</summary>
    [Fact]
    public void Velocidad_constante_de_un_decimo_a_cien()
    {
        foreach ((Rational rate, bool ok) in new[] { (new Rational(1, 10), true), (new Rational(100, 1), true), (new Rational(1, 11), false), (new Rational(101, 1), false) })
        {
            Clip clip = C("a", 0, 0, 10);
            clip.Speed = new ClipSpeed { Rate = rate };
            clip.Duration = S(1);
            List<string> errors = [.. Errors(P(Seq("s", [clip])))];
            Assert.Equal(ok, !errors.Exists(e => e.Contains("velocidad fuera", StringComparison.Ordinal)));
        }

        Project project = P(Seq("s", [C("a", 0, 0, 10)]));
        Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(Ctx(project), "a", new Rational(101, 1)));
    }

    // ── Efectos ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«keyframes en tiempo del origen (al recortar, la animación sigue pegada al material)».</summary>
    [Fact]
    public void Recortar_deja_la_animacion_pegada_al_material()
    {
        Clip clip = C("a", 0, 0, 10);
        var opacity = new Parameter { Value = 1, Keyframes = [new Keyframe(S(5), 0.25), new Keyframe(S(8), 0.75)] };
        clip.Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform, Parameters = { [BuiltInEffects.TransformParameters.Opacity] = opacity } });
        Project project = P(Seq("s", [clip]));
        Time sourceTime = S(5);
        Time timelineBefore = clip.Start + (sourceTime - clip.SourceIn);

        TimelineEdits.Trim(Ctx(project), "a", TrimEdge.In, S(2));

        Clip trimmed = project.Sequences[0].VideoTracks[0].Clips[0];
        Parameter kept = trimmed.Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity];
        Assert.Equal(S(5), kept.Keyframes[0].Time);
        Assert.Equal(sourceTime, trimmed.ToEffectTime(timelineBefore));
        Assert.Equal(0.25, kept.Evaluate(trimmed.ToEffectTime(timelineBefore)));
    }

    /// <summary>«interpolación <c>Hold</c>, <c>Linear</c>, <c>EaseIn</c>, <c>EaseOut</c>, <c>EaseInOut</c>, <c>Bezier</c> […], <c>Bounce</c> o <c>Elastic</c>».</summary>
    [Fact]
    public void Las_ocho_interpolaciones()
    {
        Assert.Equal(
            [Interpolation.Hold, Interpolation.Linear, Interpolation.EaseIn, Interpolation.EaseOut, Interpolation.EaseInOut, Interpolation.Bezier, Interpolation.Bounce, Interpolation.Elastic],
            Enum.GetValues<Interpolation>());

        var hold = new Parameter { Keyframes = [new Keyframe(S(0), 0, Interpolation.Hold), new Keyframe(S(10), 1)] };
        Assert.Equal(0, hold.Evaluate(S(9)));
        var linear = new Parameter { Keyframes = [new Keyframe(S(0), 0), new Keyframe(S(10), 1)] };
        Assert.Equal(0.5, linear.Evaluate(S(5)), 9);
    }

    // ── Enlaces y validador ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Los clips con el mismo <c>LinkId</c> […] se editan juntos y con el mismo desplazamiento».</summary>
    [Fact]
    public void Enlazados_se_mueven_juntos_con_el_mismo_desplazamiento()
    {
        Project project = P(Seq("s", [C("v", 0, 0, 10, "l")], [C("a", 2, 2, 6, "l")]));

        TimelineEdits.Move(Ctx(project), "v", S(5), null, PlacementMode.Overwrite);

        Sequence sequence = project.Sequences[0];
        Assert.Equal(S(5), sequence.FindClip("v")!.Value.Clip.Start);
        Assert.Equal(S(7), sequence.FindClip("a")!.Value.Clip.Start);
    }

    /// <summary>«un grupo no puede tener dos clips en la misma pista».</summary>
    [Fact]
    public void Un_enlace_no_tiene_dos_clips_en_la_misma_pista()
    {
        Project project = P(Seq("s", [C("v", 0, 0, 5, "l"), C("w", 5, 5, 5, "l")]));

        Assert.Contains(Errors(project), e => e.Contains("dos clips en la misma pista", StringComparison.Ordinal));
    }

    /// <summary>«ids únicos en todo el archivo».</summary>
    [Fact]
    public void Ids_unicos_en_todo_el_archivo()
    {
        Project project = P(Seq("s1", [C("x", 0, 0, 5)]), Seq("s2", [C("x", 0, 0, 5)], track: "b"));

        Assert.Contains(Errors(project), e => e.Contains("«x» está repetido", StringComparison.Ordinal));

        Sequence other = Seq("s3");
        other.VideoTracks[0].Id = "x";
        Assert.Contains(Errors(P(Seq("s1", [C("x", 0, 0, 5)]), other)), e => e.Contains("«x» está repetido", StringComparison.Ordinal));
    }

    /// <summary>«clips dentro de su medio».</summary>
    [Fact]
    public void Clips_dentro_de_su_medio()
    {
        Assert.Empty(Errors(P(Seq("s", [C("a", 0, 50, 10)]))));
        Assert.Contains(Errors(P(Seq("s", [C("a", 0, 55, 10)]))), e => e.Contains("el medio solo dura", StringComparison.Ordinal));
    }

    /// <summary>«keyframes en orden».</summary>
    [Fact]
    public void Keyframes_en_orden()
    {
        Clip clip = C("a", 0, 0, 10);
        clip.Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Transform,
            Parameters = { [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = 1, Keyframes = [new Keyframe(S(5), 1), new Keyframe(S(5), 0)] } },
        });

        Assert.Contains(Errors(P(Seq("s", [clip]))), e => e.Contains("no están en orden estricto", StringComparison.Ordinal));
    }

    /// <summary>«con mensajes en español»: cada mensaje nombra el objeto y explica el problema en español.</summary>
    [Fact]
    public void Mensajes_en_espanol()
    {
        Project bad = P(Seq("s", [C("a", 0, 0, 5)]));
        bad.Sequences[0].VideoTracks[0].Clips[0].MediaId = "otro";
        Assert.Equal(["El clip «a» de la pista «Video 1» usa el medio «otro», que no existe."], Errors(bad));
    }

    /// <summary>«también rechaza los parámetros (y sus keyframes) fuera de su rango».</summary>
    [Fact]
    public void Parametros_y_keyframes_fuera_de_rango()
    {
        Project Make(Parameter opacity)
        {
            Clip clip = C("a", 0, 0, 10);
            clip.Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform, Parameters = { [BuiltInEffects.TransformParameters.Opacity] = opacity } });
            return P(Seq("s", [clip]));
        }

        Assert.Empty(Errors(Make(new Parameter { Value = 0.5 })));
        Assert.Contains(Errors(Make(new Parameter { Value = 5 })), e => e.Contains("fuera de su rango", StringComparison.Ordinal));
        Assert.Contains(Errors(Make(new Parameter { Value = 0.5, Keyframes = [new Keyframe(S(1), 0.5), new Keyframe(S(2), -3)] })), e => e.Contains("fuera de su rango", StringComparison.Ordinal));
    }

    /// <summary>«los ids con caracteres de control».</summary>
    [Fact]
    public void Ids_con_caracteres_de_control()
    {
        Project project = P(Seq("s", [C("a\nb", 0, 0, 5)]));

        Assert.Contains(Errors(project), e => e.Contains("caracteres de control", StringComparison.Ordinal));
    }

    /// <summary>«revisa la velocidad, los generadores (texto, estilo y sus topes) y los roles de pista».</summary>
    [Fact]
    public void Generadores_y_roles_de_pista()
    {
        Clip longText = Title("t", 0, 0, 5);
        longText.Generator!.Text = new string('a', ModelValidator.MaxTextLength + 1);
        Assert.NotEmpty(Errors(P(Seq("s", [longText]))));

        Sequence roles = Seq("s");
        roles.VideoTracks[0].Role = "Rol con espacios";
        Assert.NotEmpty(Errors(P(roles)));

        Sequence subtitles = Seq("s");
        subtitles.AudioTracks[0].Role = TrackRoles.Subtitles;
        Assert.NotEmpty(Errors(P(subtitles)));
    }

    /// <summary>«<c>IdGenerator</c> da ids GUID al programa e ids secuenciales (<c>clip-1</c>) a las pruebas».</summary>
    [Fact]
    public void Generadores_de_ids()
    {
        var sequential = new SequentialIdGenerator();
        Assert.Equal("clip-1", sequential.NewId("clip"));
        Assert.Equal("clip-2", sequential.NewId("clip"));

        string guid = GuidIdGenerator.Instance.NewId("clip");
        Assert.StartsWith("clip-", guid, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(guid["clip-".Length..], "N", out _));
    }

    // ── Clips compuestos ─────────────────────────────────────────────────────────────────────────────────────────────

    private static Clip Compound(string id, string sequenceId, double length) =>
        new() { Id = id, SequenceId = sequenceId, SourceIn = S(0), SourceOut = S(length) };

    /// <summary>«el validador rechaza los ciclos».</summary>
    [Fact]
    public void Compuestos_ciclicos_se_rechazan()
    {
        Project project = P(Seq("a", [Compound("ca", "b", 5)]), Seq("b", [Compound("cb", "a", 5)]));

        Assert.Contains(Errors(project), e => e.Contains("se contiene a sí mismo", StringComparison.Ordinal));
    }

    /// <summary>«y más de 16 niveles»: 16 niveles valen, 17 no.</summary>
    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Compuestos_hasta_16_niveles(int levels, bool valid)
    {
        var sequences = new List<Sequence>();
        for (int i = 0; i < levels; i++)
        {
            sequences.Add(Seq("s" + i, [Compound("c" + i, "s" + (i + 1), 5)]));
        }

        sequences.Add(Seq("s" + levels, [C("hoja", 0, 0, 5)]));

        Assert.Equal(valid, !Errors(P([.. sequences])).Any(e => e.Contains("niveles", StringComparison.Ordinal)));
    }

    /// <summary>«crear y descomponer es la identidad».</summary>
    [Fact]
    public void Crear_y_descomponer_es_la_identidad()
    {
        Project project = P(Seq("s", [C("v", 0, 0, 5, "l"), C("w", 5, 10, 5)], [C("a", 0, 0, 5, "l")]));
        string before = Rodaje.Core.Serialization.ProjectSerializer.Serialize(project);
        EditContext ctx = Ctx(project);

        IReadOnlyList<string> created = TimelineEdits.CreateCompound(ctx, ["v", "w"], "anidada");
        string compound = created.First(id => project.Sequences[0].FindClip(id)!.Value.Track.Kind == TrackKind.Video);
        TimelineEdits.DecomposeCompound(ctx, compound);

        Assert.Equal(before, Rodaje.Core.Serialization.ProjectSerializer.Serialize(project));
    }

    /// <summary>«Un compuesto no admite velocidad ni reemplazar medio».</summary>
    [Fact]
    public void Compuesto_sin_velocidad_ni_reemplazo_de_medio()
    {
        Project project = P(Seq("s", [Compound("c", "n", 5)]), Seq("n", [C("x", 0, 0, 5)]));

        Assert.Empty(Errors(project));
        Assert.Throws<EditException>(() => TimelineEdits.SetSpeed(Ctx(project), "c", new Rational(2, 1)));
        Assert.Throws<EditException>(() => TimelineEdits.ReplaceMedia(Ctx(project), "c", "m"));
    }

    /// <summary>«y recortarlo está acotado por la duración de su secuencia».</summary>
    [Fact]
    public void Recortar_un_compuesto_esta_acotado_por_su_secuencia()
    {
        Project project = P(Seq("s", [Compound("c", "n", 5)]), Seq("n", [C("x", 0, 0, 8)]));

        try
        {
            TimelineEdits.Trim(Ctx(project), "c", TrimEdge.Out, S(10));
        }
        catch (EditException)
        {
        }

        Clip clip = project.Sequences[0].VideoTracks[0].Clips[0];
        Assert.True(clip.SourceOut <= project.Sequences[1].Duration, $"El compuesto termina en {clip.SourceOut}.");
    }

    // ── Registro ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Tipos de efecto y de transición (las seis primeras, las nueve de calidad de D-042, <c>Preset</c>, […] y los tres cruces de audio)».</summary>
    [Fact]
    public void Registro_tiene_diecinueve_transiciones_con_tres_de_audio()
    {
        Assert.Equal(6 + 9 + 1 + 3, Rodaje.Core.Effects.EffectRegistry.Transitions.Length);
        Assert.Equal(3, Rodaje.Core.Effects.EffectRegistry.Transitions.Count(t => BuiltInTransitions.IsAudio(t.Type)));
        Assert.Contains(Rodaje.Core.Effects.EffectRegistry.Transitions, t => t.Type == BuiltInTransitions.Preset);
    }

    /// <summary>«con código estable».</summary>
    [Fact]
    public void Registro_codigos_unicos()
    {
        var all = Rodaje.Core.Effects.EffectRegistry.All;
        Assert.Equal(all.Length, all.Select(d => d.Code).Distinct().Count());
        Assert.All(all, d => Assert.Same(d, Rodaje.Core.Effects.EffectRegistry.Find(d.Code)));
    }

    /// <summary>«y los 13 modos de mezcla».</summary>
    [Fact]
    public void Registro_trece_modos_de_mezcla()
    {
        Assert.Equal(13, Rodaje.Core.Effects.EffectRegistry.BlendModes.Distinct().Count());
        Assert.Equal(13, Rodaje.Core.Effects.EffectRegistry.BlendModes.Length);
    }

    /// <summary>«un tipo desconocido se conserva sin aplicarse».</summary>
    [Fact]
    public void Tipo_desconocido_se_conserva()
    {
        Clip clip = C("a", 0, 0, 10);
        clip.Effects.Add(new EffectInstance { Id = "fx", Type = "otro.futuro", Parameters = { ["x"] = new Parameter { Value = 1e9 } }, Options = { ["o"] = "?" } });
        Project project = P(Seq("s", [clip]));

        Assert.Empty(Errors(project));
        Project loaded = Rodaje.Core.Serialization.ProjectSerializer.Deserialize(Rodaje.Core.Serialization.ProjectSerializer.Serialize(project));
        EffectInstance kept = Assert.Single(loaded.Sequences[0].VideoTracks[0].Clips[0].Effects);
        Assert.Equal("otro.futuro", kept.Type);
        Assert.Equal(1e9, kept.Parameters["x"].Value);
        Assert.Equal("?", kept.Options["o"]);
    }
}
