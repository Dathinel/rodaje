using System.Text;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Operaciones de edición (<c>Editing/</c>)» de <c>docs/arquitectura/nucleo.md</c>. Cada prueba cita la
/// frase. En los diagramas cada carácter es un segundo: A de 0 a 4 s, B de 4 a 10 s, C de 10 a 14 s y X de 3 s.
/// </summary>
public sealed class EditingSectionContractTests
{
    private const string V1 = "v1";
    private const string V2 = "v2";
    private const string A1 = "a1";

    private readonly Project _project;
    private readonly Sequence _seq;
    private readonly EditContext _ctx;

    public EditingSectionContractTests()
    {
        static MediaRef Media(string id) => new() { Id = id, Name = id, Duration = S(60), HasVideo = true, HasAudio = true };
        _project = new Project { Id = "p", Name = "Prueba", Media = [Media("mA"), Media("mB"), Media("mC"), Media("mX")] };
        _seq = new Sequence
        {
            Id = "seq",
            Name = "Secuencia",
            FrameRate = Rational.Fps25,
            VideoTracks =
            [
                new Track { Id = V1, Kind = TrackKind.Video, Name = "V1", Clips = [Clip("A", "mA", 0, 0, 4), Clip("B", "mB", 4, 0, 6), Clip("C", "mC", 10, 0, 4)] },
                new Track { Id = V2, Kind = TrackKind.Video, Name = "V2" },
            ],
            AudioTracks = [new Track { Id = A1, Kind = TrackKind.Audio, Name = "A1" }],
        };
        _project.Sequences.Add(_seq);
        _ctx = new EditContext(_project, _seq, new SequentialIdGenerator());
    }

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip Clip(string id, string media, double start, double sourceIn, double length, string? link = null) => new()
    {
        Id = id,
        MediaId = media,
        Start = S(start),
        SourceIn = S(sourceIn),
        SourceOut = S(sourceIn + length),
        LinkId = link,
    };

    private Track T(string id) => _seq.GetTrack(id);

    private Clip Get(string id) => _seq.GetClip(id).Clip;

    /// <summary>Un carácter por segundo: la letra del medio (o la de <paramref name="overrides"/>) y «·» en los huecos.</summary>
    private string Render(string trackId = V1, Dictionary<string, char>? overrides = null)
    {
        Track track = T(trackId);
        var text = new StringBuilder();
        for (int s = 0; S(s) < track.End; s++)
        {
            Clip? clip = track.Clips.Find(c => c.Start <= S(s) && S(s) < c.End);
            text.Append(clip is null ? '·'
                : overrides is not null && overrides.TryGetValue(clip.Id, out char c) ? c
                : clip.MediaId[1]);
        }

        return text.ToString();
    }

    private void Link(string videoId, string audioId, string media, double start, double length, string link)
    {
        Get(videoId).LinkId = link;
        T(A1).Clips.Add(Clip(audioId, media, start, Get(videoId).SourceIn.ToSeconds(), length, link));
        T(A1).SortClips();
    }

    // «`Split` B en 7 s (la mitad derecha recibe id nuevo y efectos copiados) | `AAAABBBBBBCCCC` | `AAAABBBbbbCCCC`»
    [Fact]
    public void Split_B_en_7_da_la_mitad_derecha_con_id_nuevo_y_efectos_copiados()
    {
        Get("B").Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform });

        IReadOnlyList<string> created = TimelineEdits.Split(_ctx, "B", S(7));

        string right = Assert.Single(created);
        Assert.NotEqual("B", right);
        Assert.Equal("AAAABBBbbbCCCC", Render(overrides: new Dictionary<string, char> { [right] = 'b' }));
        EffectInstance copy = Assert.Single(Get(right).Effects);
        Assert.Equal(BuiltInEffects.Transform, copy.Type);
        Assert.NotSame(Get("B").Effects[0], copy);
    }

    // «`Trim` final de B, −2 s (abre hueco) | `AAAABBBBBBCCCC` | `AAAABBBB··CCCC`»
    [Fact]
    public void Trim_final_de_B_menos_2_abre_hueco()
    {
        TimelineEdits.Trim(_ctx, "B", TrimEdge.Out, S(-2));
        Assert.Equal("AAAABBBB··CCCC", Render());
    }

    [Fact]
    public void Trim_que_deja_duracion_cero_falla_sin_modificar_el_clip()
    {
        Clip clip = Get("B");

        Assert.Throws<EditException>(() => TimelineEdits.Trim(_ctx, "B", TrimEdge.In, S(6)));

        Assert.Equal((S(4), S(10), S(0), S(6)), (clip.Start, clip.End, clip.SourceIn, clip.SourceOut));
    }

    // «`RippleTrim` final de B, −2 s (corre lo de después) | `AAAABBBBBBCCCC` | `AAAABBBBCCCC`»
    [Fact]
    public void RippleTrim_final_de_B_menos_2_corre_lo_de_despues()
    {
        TimelineEdits.RippleTrim(_ctx, "B", TrimEdge.Out, S(-2), RippleScope.Track);
        Assert.Equal("AAAABBBBCCCC", Render());
    }

    // «`Roll` corte B/C, +2 s | `AAAABBBBBBCCCC` | `AAAABBBBBBBBCC`»
    [Fact]
    public void Roll_corte_B_C_mas_2()
    {
        TimelineEdits.Roll(_ctx, "B", S(2));
        Assert.Equal("AAAABBBBBBBBCC", Render());
    }

    // «`Slip` B, +2 s (otra parte del archivo) | B muestra 0-6 s | B muestra 2-8 s»
    [Fact]
    public void Slip_B_mas_2_muestra_otra_parte_del_archivo()
    {
        TimelineEdits.Slip(_ctx, "B", S(2));

        Assert.Equal((S(2), S(8)), (Get("B").SourceIn, Get("B").SourceOut));
        Assert.Equal("AAAABBBBBBCCCC", Render());
    }

    // «`Slide` B, +2 s | `AAAABBBBBBCCCC` | `AAAAAABBBBBBCC`»
    [Fact]
    public void Slide_B_mas_2()
    {
        TimelineEdits.Slide(_ctx, "B", S(2));
        Assert.Equal("AAAAAABBBBBBCC", Render());
    }

    // «`Move` B a 8 s, modo `Overwrite` | `AAAABBBBBBCCCC` | `AAAA····BBBBBB`»
    [Fact]
    public void Move_B_a_8_en_modo_Overwrite()
    {
        TimelineEdits.Move(_ctx, "B", S(8), null, PlacementMode.Overwrite);
        Assert.Equal("AAAA····BBBBBB", Render());
    }

    // «`Insert` X en 6 s | `AAAABBBBBBCCCC` | `AAAABBXXXBBBBCCCC`»
    [Fact]
    public void Insert_X_en_6()
    {
        TimelineEdits.InsertMedia(_ctx, "mX", S(0), S(3), V1, S(6), PlacementMode.Insert);
        Assert.Equal("AAAABBXXXBBBBCCCC", Render());
    }

    // «`Overwrite` X en 6 s | `AAAABBBBBBCCCC` | `AAAABBXXXBCCCC`»
    [Fact]
    public void Overwrite_X_en_6()
    {
        TimelineEdits.InsertMedia(_ctx, "mX", S(0), S(3), V1, S(6), PlacementMode.Overwrite);
        Assert.Equal("AAAABBXXXBCCCC", Render());
    }

    // «`Lift` B / `RippleDelete` B | `AAAABBBBBBCCCC` | `AAAA······CCCC` / `AAAACCCC`»
    [Fact]
    public void Lift_B_deja_hueco()
    {
        TimelineEdits.Lift(_ctx, ["B"]);
        Assert.Equal("AAAA······CCCC", Render());
    }

    // «`Lift` B / `RippleDelete` B | `AAAABBBBBBCCCC` | `AAAA······CCCC` / `AAAACCCC`»
    [Fact]
    public void RippleDelete_B_cierra_el_hueco()
    {
        TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.Track);
        Assert.Equal("AAAACCCC", Render());
    }

    // «Un `delta` positivo mueve a la derecha.»
    [Fact]
    public void Un_delta_positivo_mueve_a_la_derecha_y_uno_negativo_a_la_izquierda()
    {
        TimelineEdits.Trim(_ctx, "C", TrimEdge.In, S(1));
        Assert.Equal(S(11), Get("C").Start);
        TimelineEdits.Trim(_ctx, "C", TrimEdge.In, S(-1));
        Assert.Equal(S(10), Get("C").Start);
    }

    // «`TimelineEdits` … modifica la secuencia en el sitio, respeta los enlaces»
    [Fact]
    public void Las_ediciones_modifican_la_secuencia_en_el_sitio_y_llevan_a_los_enlazados()
    {
        Link("B", "B2", "mB", 4, 6, "lb");
        Clip b = Get("B");
        Clip b2 = Get("B2");

        TimelineEdits.Trim(_ctx, "B", TrimEdge.Out, S(-2));
        Assert.Same(b, Get("B"));
        Assert.Equal((S(8), S(8)), (b.End, b2.End));

        TimelineEdits.Move(_ctx, "B", S(20), null, PlacementMode.Strict);
        Assert.Equal((S(20), S(20)), (Get("B").Start, Get("B2").Start));

        TimelineEdits.Split(_ctx, "B", S(22));
        Assert.Equal(2, T(A1).Clips.Count);
    }

    public static TheoryData<string> LockedOperations() =>
    [
        "Split", "Trim", "RippleTrim", "Roll", "Slip", "Slide", "Move", "Insert", "Overwrite", "Lift", "RippleDelete",
        "CloseGap", "Reorder", "SetSpeed", "SetReverse", "SetPreservePitch", "RateStretch", "FreezeFrame",
        "SetClipEnabled", "UnlinkClips", "RemoveTrack", "MoveTrack", "AddEffect", "AddTrackEffect", "AddTransition",
    ];

    // «… rechaza tocar pistas bloqueadas … Si no puede hacer algo, lanza `EditException` con un mensaje listo para mostrar.»
    [Theory]
    [MemberData(nameof(LockedOperations))]
    public void En_una_pista_bloqueada_la_edicion_se_rechaza_con_EditException(string operation)
    {
        Link("B", "B2", "mB", 4, 6, "lb");
        T(V1).Locked = true;

        Action act = operation switch
        {
            "Split" => () => TimelineEdits.Split(_ctx, "B", S(7)),
            "Trim" => () => TimelineEdits.Trim(_ctx, "B", TrimEdge.Out, S(-2)),
            "RippleTrim" => () => TimelineEdits.RippleTrim(_ctx, "B", TrimEdge.Out, S(-2), RippleScope.Track),
            "Roll" => () => TimelineEdits.Roll(_ctx, "B", S(2)),
            "Slip" => () => TimelineEdits.Slip(_ctx, "B", S(2)),
            "Slide" => () => TimelineEdits.Slide(_ctx, "B", S(2)),
            "Move" => () => TimelineEdits.Move(_ctx, "B", S(20), null, PlacementMode.Strict),
            "Insert" => () => TimelineEdits.InsertMedia(_ctx, "mX", S(0), S(3), V1, S(6), PlacementMode.Insert),
            "Overwrite" => () => TimelineEdits.InsertMedia(_ctx, "mX", S(0), S(3), V1, S(6), PlacementMode.Overwrite),
            "Lift" => () => TimelineEdits.Lift(_ctx, ["B"]),
            "RippleDelete" => () => TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.Track),
            "CloseGap" => () => TimelineEdits.CloseGap(_ctx, V1, S(15), RippleScope.Track),
            "Reorder" => () => TimelineEdits.ReorderOnMainTrack(_ctx, "C", 0),
            "SetSpeed" => () => TimelineEdits.SetSpeed(_ctx, "B", new Rational(2, 1)),
            "SetReverse" => () => TimelineEdits.SetReverse(_ctx, "B", true),
            "SetPreservePitch" => () => TimelineEdits.SetPreservePitch(_ctx, "B", false),
            "RateStretch" => () => TimelineEdits.RateStretch(_ctx, "B", TrimEdge.Out, S(-2)),
            "FreezeFrame" => () => TimelineEdits.FreezeFrame(_ctx, "B", S(6)),
            "SetClipEnabled" => () => TimelineEdits.SetClipEnabled(_ctx, "B", false),
            "UnlinkClips" => () => TimelineEdits.UnlinkClips(_ctx, "B"),
            "RemoveTrack" => () => TimelineEdits.RemoveTrack(_ctx, V1),
            "MoveTrack" => () => TimelineEdits.MoveTrack(_ctx, V1, 1),
            "AddEffect" => () => EffectEdits.AddEffect(_ctx, "B", BuiltInEffects.Transform),
            "AddTrackEffect" => () => EffectEdits.AddEffect(_ctx, V1, BuiltInEffects.Transform),
            "AddTransition" => () => TransitionEdits.AddTransition(_ctx, "B", BuiltInTransitions.Crossfade),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        EditException error = Assert.Throws<EditException>(act);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    // «… respeta los enlaces, rechaza tocar pistas bloqueadas»: también si la bloqueada es la del enlazado.
    [Fact]
    public void Editar_un_clip_cuyo_enlazado_esta_en_una_pista_bloqueada_se_rechaza()
    {
        Link("B", "B2", "mB", 4, 6, "lb");
        T(A1).Locked = true;

        Assert.Throws<EditException>(() => TimelineEdits.Trim(_ctx, "B", TrimEdge.Out, S(-2)));
        Assert.Throws<EditException>(() => TimelineEdits.Move(_ctx, "B", S(20), null, PlacementMode.Strict));
    }

    // «… y termina siempre con `EditContext.EnsureValid()`» (que llama a `TransitionOps.Reconcile`): el corte que
    // desaparece se lleva su transición.
    [Fact]
    public void Al_terminar_la_edicion_se_reconcilian_las_transiciones()
    {
        string transition = TransitionEdits.AddTransition(_ctx, "B", BuiltInTransitions.Crossfade, S(1));

        TimelineEdits.Trim(_ctx, "B", TrimEdge.Out, S(-2));

        Assert.Null(T(V1).FindTransition(transition));
    }

    // «`Move` tiene tres modos (`PlacementMode`: `Strict`, `Overwrite`, `Insert`)»
    [Fact]
    public void Move_Strict_rechaza_un_lugar_ocupado_e_Insert_hace_sitio()
    {
        Assert.Throws<EditException>(() => TimelineEdits.Move(_ctx, "C", S(6), null, PlacementMode.Strict));

        TimelineEdits.Move(_ctx, "C", S(4), null, PlacementMode.Insert);
        Assert.Equal("AAAACCCCBBBBBB", Render());
    }

    // «también existen `AppendMedia` y `CloseGap`»
    [Fact]
    public void AppendMedia_pone_al_final_y_CloseGap_cierra_el_hueco()
    {
        TimelineEdits.AppendMedia(_ctx, "mX", S(0), S(3), V1);
        Assert.Equal("AAAABBBBBBCCCCXXX", Render());

        TimelineEdits.Lift(_ctx, ["B"]);
        TimelineEdits.CloseGap(_ctx, V1, S(5), RippleScope.Track);
        Assert.Equal("AAAACCCCXXX", Render());
    }

    // «`Track` corre las pistas del grupo editado y las de los enlazados de lo que se corre, para no desincronizarlos»
    [Fact]
    public void Ripple_de_pista_corre_las_de_los_enlazados_de_lo_que_se_corre_y_no_las_demas()
    {
        Link("C", "C2", "mC", 10, 4, "lc");
        T(V2).Clips.Add(Clip("Z", "mX", 12, 0, 2));

        TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.Track);

        Assert.Equal((S(4), S(4)), (Get("C").Start, Get("C2").Start));
        Assert.Equal(S(12), Get("Z").Start);
    }

    // «(si una no puede cerrar el tramo, la edición se rechaza)»
    [Fact]
    public void Ripple_de_pista_se_rechaza_si_la_pista_de_un_enlazado_no_puede_cerrar_el_tramo()
    {
        Link("C", "C2", "mC", 10, 4, "lc");
        T(A1).Clips.Insert(0, Clip("D2", "mX", 7, 0, 2));

        Assert.Throws<EditException>(() => TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.Track));
        Assert.Throws<EditException>(() => TimelineEdits.RippleTrim(_ctx, "B", TrimEdge.Out, S(-2), RippleScope.Track));
    }

    // «`AllTracks` corre todas las no bloqueadas»
    [Fact]
    public void Ripple_de_todas_las_pistas_corre_las_no_bloqueadas()
    {
        T(V2).Clips.Add(Clip("Z", "mX", 12, 0, 2));
        T(A1).Clips.Add(Clip("Q", "mX", 12, 0, 2));
        T(A1).Locked = true;
        _seq.Markers.Add(new Marker { Id = "mk", Time = S(12), Name = "m" });

        TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.AllTracks);

        Assert.Equal("AAAACCCC", Render());
        Assert.Equal(S(6), Get("Z").Start);
        Assert.Equal(S(12), Get("Q").Start);
        Assert.Equal(S(12), _seq.Markers[0].Time);
    }

    // «`AllTracksAndMarkers` además corre marcadores y regiones (lo que cae dentro de un tramo cerrado queda en su inicio
    // y una región que queda vacía se borra)»
    [Fact]
    public void Ripple_con_marcadores_corre_marcadores_y_regiones_y_borra_las_que_quedan_vacias()
    {
        _seq.Markers.AddRange(
        [
            new Marker { Id = "antes", Time = S(2), Name = "antes" },
            new Marker { Id = "dentro", Time = S(6), Name = "dentro" },
            new Marker { Id = "despues", Time = S(12), Name = "despues" },
        ]);
        _seq.Regions.AddRange(
        [
            new Region { Id = "vacia", Start = S(5), End = S(9), Name = "vacia" },
            new Region { Id = "cruza", Start = S(8), End = S(12), Name = "cruza" },
        ]);

        TimelineEdits.RippleDelete(_ctx, ["B"], RippleScope.AllTracksAndMarkers);

        Assert.Equal([S(2), S(4), S(6)], _seq.Markers.Select(m => m.Time));
        Region region = Assert.Single(_seq.Regions);
        Assert.Equal(("cruza", S(4), S(6)), (region.Id, region.Start, region.End));
    }

    // «`ReorderOnMainTrack` lleva un clip a otra posición de su pista y la deja sin huecos, con sus enlazados»
    [Fact]
    public void ReorderOnMainTrack_deja_la_pista_sin_huecos_y_lleva_a_los_enlazados()
    {
        TimelineEdits.Lift(_ctx, ["B"]);
        T(V1).Clips.Add(Clip("B", "mB", 6, 0, 6));
        T(V1).SortClips();
        Link("C", "C2", "mC", 12, 4, "lc");
        Get("C").Start = S(12);

        TimelineEdits.ReorderOnMainTrack(_ctx, "C", 0);

        Assert.Equal("CCCCAAAABBBBBB", Render());
        Assert.Equal(S(0), Get("C2").Start);
    }

    // «Los marcadores y regiones tienen `AddMarker`, `RemoveMarker`, `MoveMarker`, `RenameMarker`, `EditMarker` (nombre,
    // color y nota), `AddRegion`, `EditRegion` y `RemoveRegion`.»
    [Fact]
    public void Operaciones_de_marcadores_y_regiones()
    {
        string marker = TimelineEdits.AddMarker(_ctx, S(3), "uno");
        TimelineEdits.MoveMarker(_ctx, marker, S(5));
        TimelineEdits.RenameMarker(_ctx, marker, "dos");
        Assert.Equal((S(5), "dos"), (_seq.Markers[0].Time, _seq.Markers[0].Name));

        TimelineEdits.EditMarker(_ctx, marker, "tres", "#ff0000", "nota");
        Assert.Equal(("tres", "#FF0000", "nota"), (_seq.Markers[0].Name, _seq.Markers[0].Color, _seq.Markers[0].Note));
        TimelineEdits.RemoveMarker(_ctx, marker);
        Assert.Empty(_seq.Markers);

        string region = TimelineEdits.AddRegion(_ctx, S(1), S(2), "r");
        TimelineEdits.EditRegion(_ctx, region, "r2", null, "n", S(3), S(4));
        Assert.Equal(("r2", "n", S(3), S(4)), (_seq.Regions[0].Name, _seq.Regions[0].Note, _seq.Regions[0].Start, _seq.Regions[0].End));
        TimelineEdits.RemoveRegion(_ctx, region);
        Assert.Empty(_seq.Regions);

        Assert.Throws<EditException>(() => TimelineEdits.AddRegion(_ctx, S(4), S(4), "vacia"));
        Assert.Throws<EditException>(() => TimelineEdits.AddMarker(_ctx, S(-1), "antes"));
    }

    // «… las operaciones de pistas y estado: `AddTrack`, `RemoveTrack`, `MoveTrack`, `SetTrackState`, `SetClipEnabled`,
    // `LinkClips` y `UnlinkClips`.»
    [Fact]
    public void Operaciones_de_pistas_y_estado()
    {
        string added = TimelineEdits.AddTrack(_ctx, TrackKind.Video, 0, "V0");
        Assert.Equal(added, _seq.VideoTracks[0].Id);
        TimelineEdits.MoveTrack(_ctx, added, 2);
        Assert.Equal(added, _seq.VideoTracks[2].Id);
        TimelineEdits.SetTrackState(_ctx, added, muted: true, hidden: true, name: "Otra");
        Assert.True(T(added).Muted && T(added).Hidden && T(added).Name == "Otra");
        TimelineEdits.RemoveTrack(_ctx, added);
        Assert.Null(_seq.FindTrack(added));

        T(A1).Clips.Add(Clip("B2", "mB", 4, 0, 6));
        string link = TimelineEdits.LinkClips(_ctx, ["B", "B2"]);
        Assert.Equal((link, link), (Get("B").LinkId, Get("B2").LinkId));
        TimelineEdits.SetClipEnabled(_ctx, "B", false);
        Assert.False(Get("B").Enabled || Get("B2").Enabled);
        TimelineEdits.UnlinkClips(_ctx, "B2");
        Assert.Null(Get("B").LinkId);
        Assert.Null(Get("B2").LinkId);
    }

    // «El cambio de velocidad mantiene siempre el magnetismo (D-026), en cualquier pista …: al acortarse el clip, el
    // siguiente se une y la transición se queda»
    [Theory]
    [InlineData(V1)]
    [InlineData(V2)]
    public void Acelerar_une_el_siguiente_y_la_transicion_se_queda(string trackId)
    {
        if (trackId == V2)
        {
            T(V2).Clips.AddRange(T(V1).Clips);
            T(V1).Clips.Clear();
        }

        string transition = TransitionEdits.AddTransition(_ctx, "B", BuiltInTransitions.Crossfade, S(1));

        TimelineEdits.SetSpeed(_ctx, "B", new Rational(2, 1));

        Assert.Equal("AAAABBBCCCC", Render(trackId));
        TransitionInstance kept = Assert.Single(T(trackId).Transitions);
        Assert.Equal((transition, "B", "C"), (kept.Id, kept.From, kept.To));
    }

    // «al crecer respeta los huecos y solo empuja lo que pisa»
    [Fact]
    public void Frenar_respeta_los_huecos_y_solo_empuja_lo_que_pisa()
    {
        Get("C").Start = S(12);

        // B pasa de 6 a 12 s (4-16): pisa 4 s de C, que se corre esos 4 s y no los 6 que creció B.
        TimelineEdits.SetSpeed(_ctx, "B", new Rational(1, 2));
        Assert.Equal((S(16), S(16)), (Get("B").End, Get("C").Start));

        // Con hueco suficiente nada se mueve.
        TimelineEdits.SetSpeed(_ctx, "B", new Rational(1, 1));
        Get("C").Start = S(20);
        TimelineEdits.SetSpeed(_ctx, "B", new Rational(1, 2));
        Assert.Equal((S(16), S(20)), (Get("B").End, Get("C").Start));
    }

    // «… y respetando el `RippleScope` de la sesión»
    [Fact]
    public void El_cambio_de_velocidad_respeta_el_alcance_del_ripple()
    {
        T(V2).Clips.Add(Clip("Z", "mX", 12, 0, 2));
        _seq.Markers.Add(new Marker { Id = "mk", Time = S(12), Name = "m" });
        TimelineEdits.SetSpeed(_ctx, "B", new Rational(2, 1), RippleScope.Track);
        Assert.Equal((S(12), S(12)), (Get("Z").Start, _seq.Markers[0].Time));

        TimelineEdits.SetSpeed(_ctx, "B", new Rational(1, 1), RippleScope.AllTracksAndMarkers);
        Assert.Equal((S(15), S(15)), (Get("Z").Start, _seq.Markers[0].Time));
    }

    // «[`TrackKeyframes`] mueve los keyframes de pista con los clips»
    [Fact]
    public void Los_keyframes_de_pista_se_mueven_con_los_clips()
    {
        const string gain = BuiltInEffects.AudioParameters.GainDb;
        Link("C", "C2", "mC", 10, 4, "lc");
        T(A1).Effects.Add(new EffectInstance
        {
            Id = "vol",
            Type = BuiltInEffects.Audio,
            Parameters = { [gain] = new Parameter { Keyframes = [new Keyframe(S(0), 0), new Keyframe(S(12), -6), new Keyframe(S(30), 0)] } },
        });
        Parameter before = T(A1).Effects[0].Parameters[gain].Clone();

        TimelineEdits.SetSpeed(_ctx, "C", new Rational(1, 1)); // sin cambios
        TimelineEdits.Lift(_ctx, ["B"]);
        TimelineEdits.CloseGap(_ctx, V1, S(5), RippleScope.Track);

        Parameter after = T(A1).Effects[0].Parameters[gain];
        Assert.Equal(S(4), Get("C2").Start);
        Assert.Contains(after.Keyframes, k => k.Time == S(6) && k.Value == -6);
        Assert.Equal(before.Evaluate(S(13)), after.Evaluate(S(7)), 9);
    }

    // «`TimelineEdits.Speed` (`SetSpeed`, `SetCurve`, `SetReverse`, `SetPreservePitch`, `RateStretch` y `FreezeFrame`)»
    [Fact]
    public void Operaciones_de_velocidad()
    {
        TimelineEdits.SetReverse(_ctx, "B", true);
        Assert.True(Get("B").Speed!.Reverse);
        TimelineEdits.SetReverse(_ctx, "B", false);

        TimelineEdits.SetSpeed(_ctx, "B", new Rational(2, 1));
        TimelineEdits.SetPreservePitch(_ctx, "B", false);
        Assert.False(Get("B").Speed!.PreservePitch);

        TimelineEdits.SetCurve(_ctx, "B", null);
        Assert.Equal(S(10), Get("B").End);

        TimelineEdits.RateStretch(_ctx, "B", TrimEdge.Out, S(-3));
        Assert.Equal((S(7), S(7)), (Get("B").End, Get("C").Start));

        string frozen = TimelineEdits.FreezeFrame(_ctx, "A", S(2), S(1));
        Assert.True(Get(frozen).Speed!.Freeze);
        Assert.Equal((S(2), S(3)), (Get(frozen).Start, Get(frozen).End));
        Assert.Equal(S(8), Get("C").Start);
    }
}
