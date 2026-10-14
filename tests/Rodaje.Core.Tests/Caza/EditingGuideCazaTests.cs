using System.Collections.Generic;
using System.Linq;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Una prueba por promesa de <c>docs/guia/edicion.md</c> («Cortar y borrar», «Mover, recortar, slip, roll y slide»,
/// «Grupos, colores y huecos», «Pista principal magnética» y «Edición de tres y cuatro puntos»), sobre
/// <see cref="TimelineEdits"/> sin interfaz. Cada una cita la frase de la guía que comprueba.
/// </summary>
public class EditingGuideCazaTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
        new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };

    /// <summary>v1 es la principal; v2 y a2 empiezan vacías. El medio dura 60 s.</summary>
    private static (Project Project, EditContext Ctx) Build(IEnumerable<Clip> v1, IEnumerable<Clip>? a1 = null, IEnumerable<Clip>? v2 = null, IEnumerable<Clip>? a2 = null)
    {
        var project = new Project
        {
            Id = "p",
            Media = [new MediaRef { Id = "m", Path = "m.mp4", Name = "toma", Duration = S(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }],
            Sequences =
            [
                new Sequence
                {
                    Id = "seq",
                    VideoTracks =
                    [
                        new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 1", Clips = [.. v1] },
                        new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2", Clips = [.. v2 ?? []] },
                    ],
                    AudioTracks =
                    [
                        new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [.. a1 ?? []] },
                        new Track { Id = "a2", Kind = TrackKind.Audio, Name = "Audio 2", Clips = [.. a2 ?? []] },
                    ],
                },
            ],
        };
        return (project, new EditContext(project, project.Sequences[0], new SequentialIdGenerator()));
    }

    /// <summary>Dos clips pegados en la principal (a 0–10 y b 10–20), cada uno con su audio enlazado en a1.</summary>
    private static (Project Project, EditContext Ctx) TwoLinked() => Build(
        [C("a", 0, 0, 10, "la"), C("b", 10, 20, 10, "lb")],
        [C("aa", 0, 0, 10, "la"), C("ba", 10, 20, 10, "lb")]);

    private static Clip Get(EditContext ctx, string id) => ctx.Sequence.FindClip(id)!.Value.Clip;

    private static Track T(EditContext ctx, string id) => ctx.Sequence.FindTrack(id)!;

    private static void AssertClip(EditContext ctx, string id, double start, double sourceIn, double length)
    {
        Clip clip = Get(ctx, id);
        Assert.Equal(S(start), clip.Start);
        Assert.Equal(S(sourceIn), clip.SourceIn);
        Assert.Equal(S(length), clip.Duration);
    }

    private static void AssertNoGaps(Track track)
    {
        for (int i = 1; i < track.Clips.Count; i++)
        {
            Assert.Equal(track.Clips[i - 1].End, track.Clips[i].Start);
        }
    }

    // ── Cortar y borrar ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Cortar en el cursor | S (o Ctrl+B): corta los clips seleccionados»; la cuchilla «corta el clip y sus enlazados».</summary>
    [Fact]
    public void Cortar_en_el_cursor_corta_el_clip_y_sus_enlazados()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> right = TimelineEdits.Split(ctx, "a", S(4));

        Assert.Equal(2, right.Count);
        AssertClip(ctx, "a", 0, 0, 4);
        AssertClip(ctx, "aa", 0, 0, 4);
        AssertClip(ctx, right[0], 4, 4, 6);
        Assert.Equal(Get(ctx, right[0]).LinkId, Get(ctx, right[1]).LinkId);
        Assert.Equal(3, T(ctx, "v1").Clips.Count);
    }

    /// <summary>«Cortar en todas las pistas | Ctrl+Mayús+B».</summary>
    [Fact]
    public void Cortar_en_todas_las_pistas_corta_cada_pista_que_pasa_por_el_cursor()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 10)], [C("s", 2, 30, 6)], [C("t", 5, 40, 5)]);

        IReadOnlyList<string> right = TimelineEdits.SplitAllTracks(ctx, S(6));

        Assert.Equal(3, right.Count);
        AssertClip(ctx, "a", 0, 0, 6);
        AssertClip(ctx, "t", 5, 40, 1);
        AssertClip(ctx, "s", 2, 30, 4);
    }

    /// <summary>«Q borra lo que queda a la izquierda del cursor; W, lo de la derecha».</summary>
    [Fact]
    public void Q_borra_a_la_izquierda_y_W_a_la_derecha_del_cursor()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 10), C("b", 12, 20, 10)]);

        TimelineEdits.DeleteToPoint(ctx, ["a"], S(4), TrimEdge.In, ripple: false, RippleScope.Track);
        TimelineEdits.DeleteToPoint(ctx, ["b"], S(15), TrimEdge.Out, ripple: false, RippleScope.Track);

        AssertClip(ctx, "a", 4, 4, 6);
        AssertClip(ctx, "b", 12, 20, 3);
    }

    /// <summary>«Borrar … En la pista principal magnética o con ripple automático, lo de después se corre para cerrar el hueco».</summary>
    [Fact]
    public void Borrar_con_ripple_corre_lo_de_despues_y_cierra_el_hueco()
    {
        (_, EditContext ctx) = Build(
            [C("a", 0, 0, 10, "la"), C("b", 10, 20, 10, "lb"), C("c", 20, 40, 5, "lc")],
            [C("aa", 0, 0, 10, "la"), C("ba", 10, 20, 10, "lb"), C("ca", 20, 40, 5, "lc")]);

        TimelineEdits.RippleDelete(ctx, ["b"], RippleScope.Track);

        Assert.Null(ctx.Sequence.FindClip("ba"));
        AssertClip(ctx, "c", 10, 40, 5);
        AssertClip(ctx, "ca", 10, 40, 5);
        AssertNoGaps(T(ctx, "v1"));
        AssertNoGaps(T(ctx, "a1"));
    }

    /// <summary>«Borrar … en las demás pistas, queda el hueco».</summary>
    [Fact]
    public void Borrar_sin_ripple_deja_el_hueco()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 10), C("b", 10, 20, 10), C("c", 20, 40, 5)]);

        TimelineEdits.Lift(ctx, ["b"]);

        AssertClip(ctx, "c", 20, 40, 5);
        Assert.Single(TimelineEdits.FindGaps(ctx.Sequence, ["v1"]));
    }

    // ── Mover, recortar, slip, roll y slide ──────────────────────────────────────────────────────────────────────────

    /// <summary>«Mover: arrastra el cuerpo. No pisa nada en pistas libres».</summary>
    [Fact]
    public void Mover_no_pisa_nada_en_pistas_libres()
    {
        (_, EditContext ctx) = Build([], v2: [C("x", 0, 0, 5), C("y", 10, 20, 5)]);

        Assert.Throws<EditException>(() => TimelineEdits.Move(ctx, "x", S(8), null, PlacementMode.Strict));

        AssertClip(ctx, "x", 0, 0, 5);
        AssertClip(ctx, "y", 10, 20, 5);
        TimelineEdits.Move(ctx, "x", S(3), null, PlacementMode.Strict);
        AssertClip(ctx, "x", 3, 0, 5);
    }

    /// <summary>
    /// «En la principal magnética lo reordena: sacarlo cierra el hueco y meterlo lo abre, también en las pistas de sus
    /// enlazados».
    /// </summary>
    [Fact]
    public void Mover_en_la_principal_magnetica_reordena_tambien_los_enlazados()
    {
        (_, EditContext ctx) = Build(
            [C("a", 0, 0, 4, "la"), C("b", 4, 20, 6, "lb"), C("c", 10, 40, 2, "lc")],
            [C("aa", 0, 0, 4, "la"), C("ba", 4, 20, 6, "lb"), C("ca", 10, 40, 2, "lc")]);

        TimelineEdits.ReorderOnMainTrack(ctx, "a", 2);

        Assert.Equal(["b", "c", "a"], T(ctx, "v1").Clips.Select(c => c.Id));
        AssertNoGaps(T(ctx, "v1"));
        AssertClip(ctx, "b", 0, 20, 6);
        AssertClip(ctx, "c", 6, 40, 2);
        AssertClip(ctx, "a", 8, 0, 4);
        foreach (string id in new[] { "a", "b", "c" })
        {
            Assert.Equal(Get(ctx, id).Start, Get(ctx, id + "a").Start);
        }

        Assert.Empty(LinkSync.Find(ctx.Sequence));
    }

    /// <summary>«Recortar: arrastra un borde … En la principal magnética o con ripple automático es un recorte ripple: lo de después se corre».</summary>
    [Fact]
    public void Recorte_ripple_corre_lo_de_despues()
    {
        (_, EditContext ctx) = TwoLinked();

        TimelineEdits.RippleTrim(ctx, "a", TrimEdge.Out, S(-3), RippleScope.Track);

        AssertClip(ctx, "a", 0, 0, 7);
        AssertClip(ctx, "b", 7, 20, 10);
        AssertClip(ctx, "ba", 7, 20, 10);
        AssertNoGaps(T(ctx, "v1"));
    }

    /// <summary>«Recortar» sin ripple, en una pista libre: el borde se mueve y nada más se corre.</summary>
    [Fact]
    public void Recortar_sin_ripple_no_corre_nada()
    {
        (_, EditContext ctx) = TwoLinked();

        TimelineEdits.Trim(ctx, "a", TrimEdge.Out, S(-3));

        AssertClip(ctx, "a", 0, 0, 7);
        AssertClip(ctx, "aa", 0, 0, 7);
        AssertClip(ctx, "b", 10, 20, 10);
    }

    /// <summary>«Slip: … mueve el contenido dentro del clip sin cambiar su sitio ni su duración».</summary>
    [Fact]
    public void Slip_mueve_el_contenido_sin_cambiar_sitio_ni_duracion()
    {
        (_, EditContext ctx) = TwoLinked();

        TimelineEdits.Slip(ctx, "b", S(5));

        AssertClip(ctx, "b", 10, 25, 10);
        AssertClip(ctx, "ba", 10, 25, 10);
        AssertClip(ctx, "a", 0, 0, 10);
    }

    /// <summary>«Roll: … se arrastra un corte entre dos clips pegados: uno crece lo que el otro pierde y nada más se corre».</summary>
    [Fact]
    public void Roll_uno_crece_lo_que_el_otro_pierde_y_nada_mas_se_corre()
    {
        (_, EditContext ctx) = Build(
            [C("a", 0, 0, 10, "la"), C("b", 10, 20, 10, "lb"), C("c", 20, 40, 5)],
            [C("aa", 0, 0, 10, "la"), C("ba", 10, 20, 10, "lb")]);

        TimelineEdits.Roll(ctx, "a", S(2));

        AssertClip(ctx, "a", 0, 0, 12);
        AssertClip(ctx, "b", 12, 22, 8);
        AssertClip(ctx, "aa", 0, 0, 12);
        AssertClip(ctx, "ba", 12, 22, 8);
        AssertClip(ctx, "c", 20, 40, 5);
    }

    /// <summary>
    /// «Slide: … el clip corre entre sus vecinos pegados, que se recortan para seguir tocándolo; su contenido no cambia».
    /// </summary>
    [Fact]
    public void Slide_corre_el_clip_entre_sus_vecinos_sin_cambiar_su_contenido()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 10), C("b", 10, 20, 5), C("c", 15, 40, 10)]);

        TimelineEdits.Slide(ctx, "b", S(-2));

        AssertClip(ctx, "a", 0, 0, 8);
        AssertClip(ctx, "b", 8, 20, 5);
        AssertClip(ctx, "c", 13, 38, 12);
        AssertNoGaps(T(ctx, "v1"));
    }

    /// <summary>Roll y slide «van con los enlazados … y no pasan del material que haya».</summary>
    [Fact]
    public void Roll_y_slide_no_pasan_del_material_que_haya()
    {
        // a llega al final de su medio y c empieza en su cuadro 0: no hay material para alargarlos hacia ese lado.
        (_, EditContext ctx) = Build([C("a", 0, 50, 10), C("b", 10, 20, 10), C("c", 20, 0, 10)]);

        Assert.Throws<EditException>(() => TimelineEdits.Roll(ctx, "a", S(1)));
        Assert.Throws<EditException>(() => TimelineEdits.Roll(ctx, "b", S(-1)));
        Assert.Throws<EditException>(() => TimelineEdits.Slide(ctx, "b", S(1)));
        Assert.Throws<EditException>(() => TimelineEdits.Slide(ctx, "b", S(-1)));

        AssertClip(ctx, "a", 0, 50, 10);
        AssertClip(ctx, "b", 10, 20, 10);
        AssertClip(ctx, "c", 20, 0, 10);
    }

    // ── Grupos, colores y huecos ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«Agrupar (Ctrl+G) junta los clips elegidos: … arrastrarlo mueve a todos, también si están en la misma pista».</summary>
    [Fact]
    public void Agrupar_mueve_a_todos_tambien_en_la_misma_pista()
    {
        (_, EditContext ctx) = Build([], v2: [C("x", 0, 0, 5), C("y", 10, 20, 5)], a2: [C("z", 30, 40, 5)]);

        TimelineEdits.GroupClips(ctx, ["x", "y", "z"]);
        TimelineEdits.MoveClips(ctx, ["y"], S(2));

        AssertClip(ctx, "x", 2, 0, 5);
        AssertClip(ctx, "y", 12, 20, 5);
        AssertClip(ctx, "z", 32, 40, 5);
    }

    /// <summary>«Desagrupar (Ctrl+Mayús+G) los suelta; los enlaces de audio y video siguen».</summary>
    [Fact]
    public void Desagrupar_suelta_el_grupo_y_conserva_los_enlaces()
    {
        (_, EditContext ctx) = TwoLinked();
        TimelineEdits.GroupClips(ctx, ["a", "b"]);

        TimelineEdits.UngroupClips(ctx, ["a"]);

        Assert.All(new[] { "a", "aa", "b", "ba" }, id => Assert.Null(Get(ctx, id).GroupId));
        Assert.Equal("la", Get(ctx, "aa").LinkId);
        Assert.Equal("lb", Get(ctx, "ba").LinkId);
    }

    /// <summary>«Alt+← / Alt+→ corre la selección un cuadro (con sus enlazados y su grupo), sin pisar nada».</summary>
    [Fact]
    public void Empujon_corre_con_enlazados_y_grupo_sin_pisar_nada()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 10, "la"), C("b", 10, 20, 10)], [C("aa", 0, 0, 10, "la")]);
        Time frame = Time.FromFrames(1, Rational.Fps30);

        Assert.Throws<EditException>(() => TimelineEdits.MoveClips(ctx, ["a"], frame));
        TimelineEdits.GroupClips(ctx, ["a", "b"]);
        TimelineEdits.MoveClips(ctx, ["a"], frame);

        Assert.Equal(frame, Get(ctx, "a").Start);
        Assert.Equal(frame, Get(ctx, "aa").Start);
        Assert.Equal(S(10) + frame, Get(ctx, "b").Start);
    }

    /// <summary>«Color (menú del clip): rojo, naranja, … o el de siempre».</summary>
    [Fact]
    public void Color_tine_el_clip_y_el_de_siempre_lo_quita()
    {
        (_, EditContext ctx) = TwoLinked();

        TimelineEdits.SetClipColor(ctx, ["a"], "#E5484D");
        Assert.Equal("#E5484D", Get(ctx, "a").Color);

        TimelineEdits.SetClipColor(ctx, ["a"], null);
        Assert.Null(Get(ctx, "a").Color);
        Assert.Null(Get(ctx, "b").Color);
    }

    /// <summary>«Cerrar todos los huecos de la pista … junta los clips de la pista».</summary>
    [Fact]
    public void Cerrar_todos_los_huecos_junta_los_clips_de_la_pista()
    {
        (_, EditContext ctx) = Build([C("a", 2, 0, 3), C("b", 8, 20, 4), C("c", 15, 40, 1)]);

        (int closed, int skipped) = TimelineEdits.CloseAllGaps(ctx, ["v1"], RippleScope.Track);

        Assert.Equal((3, 0), (closed, skipped));
        AssertClip(ctx, "a", 0, 0, 3);
        AssertClip(ctx, "b", 3, 20, 4);
        AssertClip(ctx, "c", 7, 40, 1);
    }

    /// <summary>
    /// «los huecos que desincronizarían clips enlazados de otras pistas se dejan y un aviso dice cuántos». Con a1
    /// bloqueada y todas las pistas, cerrar el hueco de 5 a 7 corre «y» y deja atrás su audio «y2». Otro par (w/w2) ya
    /// estaba desfasado lo mismo, −2 s: el desfase nuevo no puede pasar por el viejo.
    /// </summary>
    [Fact]
    public void Cerrar_todos_los_huecos_deja_el_que_desincronizaria_aunque_otro_par_ya_tenga_ese_desfase()
    {
        (_, EditContext ctx) = Build(
            [C("x", 0, 0, 5), C("y", 7, 20, 5, "ly"), C("z", 14, 50, 2)],
            [C("ya", 7, 20, 5, "ly")],
            [C("w", 0, 40, 3, "lw")],
            [C("wa", 2, 40, 3, "lw")]);
        T(ctx, "a1").Locked = true;
        Assert.Single(LinkSync.Find(ctx.Sequence));

        (int closed, int skipped) = TimelineEdits.CloseAllGaps(ctx, ["v1"], RippleScope.AllTracks);

        Assert.Equal((1, 1), (closed, skipped));
        AssertClip(ctx, "y", 7, 20, 5);
        AssertClip(ctx, "z", 12, 50, 2);
        Assert.Single(LinkSync.Find(ctx.Sequence));
    }

    /// <summary>«Ir al hueco siguiente / anterior» lleva el cursor al próximo tramo vacío.</summary>
    [Fact]
    public void Ir_al_hueco_siguiente_y_anterior()
    {
        (_, EditContext ctx) = Build([C("a", 0, 0, 3), C("b", 5, 20, 2), C("c", 10, 40, 1)]);

        Assert.Equal(S(7), TimelineEdits.NextGap(ctx.Sequence, S(4), forward: true)!.Value.Start);
        Assert.Equal(S(3), TimelineEdits.NextGap(ctx.Sequence, S(7), forward: false)!.Value.Start);
        Assert.Null(TimelineEdits.NextGap(ctx.Sequence, S(7), forward: true));
    }

    // ── Pista principal magnética ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«mantiene los clips de la pista principal pegados unos a otros: cortar, borrar y mover nunca dejan huecos».</summary>
    [Fact]
    public void Principal_magnetica_cortar_borrar_y_mover_nunca_dejan_huecos()
    {
        (_, EditContext ctx) = TwoLinked();
        Track main = T(ctx, "v1");

        string piece = TimelineEdits.Split(ctx, "a", S(4))[0];
        AssertNoGaps(main);
        TimelineEdits.RippleDelete(ctx, [piece], RippleScope.Track);
        AssertNoGaps(main);
        TimelineEdits.ReorderOnMainTrack(ctx, "b", 0);
        AssertNoGaps(main);

        Assert.Equal(["b", "a"], main.Clips.Select(c => c.Id));
        Assert.Equal(Time.Zero, main.Clips[0].Start);
        Assert.Empty(TimelineEdits.FindGaps(ctx.Sequence, ["v1", "a1"]));
        Assert.Empty(LinkSync.Find(ctx.Sequence));
    }

    /// <summary>«Al soltar un medio en ella se inserta en el corte más cercano».</summary>
    [Fact]
    public void Principal_magnetica_soltar_un_medio_inserta_y_corre_lo_de_despues()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(ctx, "m", S(30), S(33), "v1", S(10), PlacementMode.Insert, "a1");

        AssertClip(ctx, ids[0], 10, 30, 3);
        AssertClip(ctx, "b", 13, 20, 10);
        AssertClip(ctx, "ba", 13, 20, 10);
        AssertNoGaps(T(ctx, "v1"));
    }

    /// <summary>«en las demás pistas sobrescribe lo que haya».</summary>
    [Fact]
    public void En_las_demas_pistas_soltar_un_medio_sobrescribe()
    {
        (_, EditContext ctx) = Build([], v2: [C("x", 0, 0, 10)]);

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(ctx, "m", S(30), S(33), "v2", S(4), PlacementMode.Overwrite);

        Track v2 = T(ctx, "v2");
        Assert.Equal(3, v2.Clips.Count);
        AssertClip(ctx, "x", 0, 0, 4);
        AssertClip(ctx, ids[0], 4, 30, 3);
        Assert.Equal(S(7), v2.Clips[2].Start);
        Assert.Equal(S(7), v2.Clips[2].SourceIn);
        Assert.Equal(S(10), v2.End);
    }

    // ── Edición de tres y cuatro puntos ──────────────────────────────────────────────────────────────────────────────

    private static ThreePointRequest Request(double? sIn, double? sOut, double? qIn, double? qOut, double playhead = 0, double? sourcePlayhead = null, FourPointFit fit = FourPointFit.Ask) =>
        new("m", sIn is { } a ? S(a) : null, sOut is { } b ? S(b) : null, qIn is { } c ? S(c) : null, qOut is { } d ? S(d) : null,
            S(playhead), "v1", "a1", PlacementMode.Overwrite, fit, RippleScope.Track, sourcePlayhead is { } e ? S(e) : null);

    /// <summary>«, inserta … el tramo en el cursor»: lo que sigue se corre, enlazado.</summary>
    [Fact]
    public void Tres_puntos_insertar_en_el_cursor_corre_lo_que_sigue()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> ids = TimelineEdits.ThreePointEdit(ctx, Request(30, 32, null, null, playhead: 10), ThreePointAction.Insert);

        AssertClip(ctx, ids[0], 10, 30, 2);
        AssertClip(ctx, ids[1], 10, 30, 2);
        AssertClip(ctx, "b", 12, 20, 10);
        AssertClip(ctx, "ba", 12, 20, 10);
    }

    /// <summary>«. sobrescribe el tramo … (o entre las marcas de la secuencia)».</summary>
    [Fact]
    public void Tres_puntos_sobrescribir_entre_las_marcas_de_la_secuencia()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> ids = TimelineEdits.ThreePointEdit(ctx, Request(30, null, 8, 12), ThreePointAction.Overwrite);

        AssertClip(ctx, ids[0], 8, 30, 4);
        AssertClip(ctx, "a", 0, 0, 8);
        AssertClip(ctx, "b", 12, 22, 8);
        Assert.Equal(S(20), T(ctx, "v1").End);
    }

    /// <summary>
    /// «Reemplazar (Mayús+.): cambia el clip bajo el cursor por el origen, en su lugar y con su duración; el cuadro del
    /// cursor del origen (o su entrada, si está marcada) queda en el cursor de la secuencia».
    /// </summary>
    [Fact]
    public void Reemplazar_conserva_lugar_y_duracion_y_alinea_los_cursores()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> ids = TimelineEdits.ThreePointEdit(ctx, Request(null, null, null, null, playhead: 13, sourcePlayhead: 35), ThreePointAction.Replace);

        Clip clip = Get(ctx, ids[0]);
        Assert.Equal(S(10), clip.Start);
        Assert.Equal(S(10), clip.Duration);
        Assert.Equal(S(35), clip.ToSourceTime(S(13)));
    }

    /// <summary>«Ajustar a rellenar (Ctrl+Mayús+.): el tramo del origen llena el clip bajo el cursor … cambiando la velocidad».</summary>
    [Fact]
    public void Ajustar_a_rellenar_llena_el_clip_bajo_el_cursor_con_la_velocidad()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> ids = TimelineEdits.ThreePointEdit(ctx, Request(30, 50, null, null, playhead: 13), ThreePointAction.FitToFill);

        Clip clip = Get(ctx, ids[0]);
        Assert.Equal(S(10), clip.Start);
        Assert.Equal(S(10), clip.Duration);
        Assert.Equal(new Rational(2, 1), clip.Speed!.Rate);
        Assert.Equal(S(30), clip.SourceIn);
        Assert.Equal(S(50), clip.SourceOut);
    }

    /// <summary>«Superponer (Mayús+,): la imagen va encima, en la primera pista de video libre (o en una nueva)».</summary>
    [Fact]
    public void Superponer_va_a_la_primera_pista_de_video_libre_o_a_una_nueva()
    {
        (_, EditContext ctx) = TwoLinked();

        IReadOnlyList<string> first = TimelineEdits.ThreePointEdit(ctx, Request(30, 32, null, null, playhead: 3), ThreePointAction.Superimpose);
        IReadOnlyList<string> second = TimelineEdits.ThreePointEdit(ctx, Request(40, 42, null, null, playhead: 3), ThreePointAction.Superimpose);

        Assert.Single(first);
        Assert.Equal("v2", ctx.Sequence.FindClip(first[0])!.Value.Track.Id);
        Assert.Equal(3, ctx.Sequence.VideoTracks.Count);
        Assert.Same(ctx.Sequence.VideoTracks[2], ctx.Sequence.FindClip(second[0])!.Value.Track);
        Assert.Equal(2, T(ctx, "v1").Clips.Count);
        Assert.Equal(2, T(ctx, "a1").Clips.Count);
    }

    /// <summary>«Con cuatro puntos de distinta duración pregunta Ajustar velocidad o Recortar».</summary>
    [Fact]
    public void Cuatro_puntos_de_distinta_duracion_preguntan_y_cada_respuesta_cumple()
    {
        (Project project, EditContext ctx) = TwoLinked();

        Assert.True(TimelineEdits.PlanThreePoint(project, Request(30, 36, 2, 5)).NeedsDecision);
        Assert.Throws<EditException>(() => TimelineEdits.ThreePointEdit(ctx, Request(30, 36, 2, 5)));

        ThreePointPlan trim = TimelineEdits.PlanThreePoint(project, Request(30, 36, 2, 5, fit: FourPointFit.Trim));
        Assert.Equal((S(30), S(33), S(3), (Rational?)null), (trim.SourceIn, trim.SourceOut, trim.Duration, trim.Rate));

        ThreePointPlan fit = TimelineEdits.PlanThreePoint(project, Request(30, 36, 2, 5, fit: FourPointFit.FitToFill));
        Assert.Equal((S(30), S(36), S(3), (Rational?)new Rational(2, 1)), (fit.SourceIn, fit.SourceOut, fit.Duration, fit.Rate));
    }
}
