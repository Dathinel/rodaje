using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Una prueba por promesa verificable de la sección «Operaciones de edición (<c>Editing/</c>)» de
/// <c>docs/arquitectura/nucleo.md</c>. Cada una cita la frase que comprueba. Como en los diagramas del documento, cada
/// carácter es un segundo, con A de 0 a 4 s, B de 4 a 10 s, C de 10 a 14 s y un clip nuevo X de 3 s.
/// </summary>
public class EditingSectionCazaTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static Clip C(string id, double start, double sourceIn, double length, string? link = null) =>
        new() { Id = id, MediaId = "m", Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };

    private sealed class Fixture
    {
        public Fixture(bool linkedAudio = false)
        {
            V1 = new Track
            {
                Id = "v1",
                Kind = TrackKind.Video,
                Name = "Video 1",
                Clips = [C("A", 0, 20, 4, linkedAudio ? "la" : null), C("B", 4, 0, 6, linkedAudio ? "lb" : null), C("C", 10, 30, 4, linkedAudio ? "lc" : null)],
            };
            V2 = new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2" };
            A1 = new Track
            {
                Id = "a1",
                Kind = TrackKind.Audio,
                Name = "Audio 1",
                Clips = linkedAudio ? [C("a", 0, 20, 4, "la"), C("b", 4, 0, 6, "lb"), C("c", 10, 30, 4, "lc")] : [],
            };
            Sequence = new Sequence { Id = "s", VideoTracks = [V1, V2], AudioTracks = [A1] };
            Project = new Project
            {
                Id = "p",
                Media = [new MediaRef { Id = "m", Path = "m.mp4", Name = "toma", Duration = S(60), HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080, SampleRate = 48000, AudioChannels = 2 }],
                Sequences = [Sequence],
            };
            Ctx = new EditContext(Project, Sequence, new SequentialIdGenerator());
        }

        public Project Project { get; }

        public Sequence Sequence { get; }

        public Track V1 { get; }

        public Track V2 { get; }

        public Track A1 { get; }

        public EditContext Ctx { get; }

        public Clip Clip(string id) => Sequence.FindClip(id)!.Value.Clip;

        /// <summary>
        /// La pista como en el documento: un carácter por segundo con el id del clip (de una letra), la inicial de
        /// <paramref name="newPieceOf"/> en minúscula si es una pieza nueva y <c>·</c> en los huecos.
        /// </summary>
        public static string Draw(Track track, string? newPieceOf = null)
        {
            var text = new StringBuilder();
            long end = track.Clips.Count == 0 ? 0 : track.End.Ticks;
            for (int second = 0; S(second).Ticks < end; second++)
            {
                Time t = S(second);
                Clip? clip = track.Clips.Find(c => c.Start <= t && t < c.End);
                text.Append(clip is null ? '·' : clip.Id.Length == 1 ? clip.Id[0] : char.ToLowerInvariant(newPieceOf![0]));
            }

            return text.ToString();
        }

        public string NewX(Time at, PlacementMode mode)
        {
            string id = TimelineEdits.InsertMedia(Ctx, "m", S(50), S(53), "v1", at, mode)[0];
            Clip(id).Id = "X";
            return "X";
        }

        public string Snapshot() => string.Join(
            "|",
            Sequence.AllTracks.SelectMany(t => t.Clips.Select(c =>
                $"{t.Id}:{c.Id}:{c.Start.Ticks}:{c.SourceIn.Ticks}:{c.SourceOut.Ticks}:{c.Enabled}:{c.LinkId}:{c.Effects.Count}:{c.Speed?.Rate}:{c.Speed?.Reverse}:{c.Speed?.PreservePitch}"))
            .Concat(Sequence.AllTracks.Select(t => $"{t.Id}:{t.Name}:{t.Effects.Count}:{t.Transitions.Count}:{string.Join(",", t.Transitions.Select(x => x.Type + x.Duration.Ticks))}")));
    }

    // ── Diagramas ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«<c>Split</c> B en 7 s (la mitad derecha recibe id nuevo y efectos copiados) | <c>AAAABBBBBBCCCC</c> | <c>AAAABBBbbbCCCC</c>».</summary>
    [Fact]
    public void Split_B_en_7_s()
    {
        var f = new Fixture();
        f.Clip("B").Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform });

        string right = TimelineEdits.Split(f.Ctx, "B", S(7)).Single();

        Assert.Equal("AAAABBBbbbCCCC", Fixture.Draw(f.V1, "b"));
        Assert.NotEqual("B", right);
        Clip copy = f.Clip(right);
        EffectInstance effect = Assert.Single(copy.Effects);
        Assert.Equal(BuiltInEffects.Transform, effect.Type);
        Assert.NotSame(f.Clip("B").Effects[0], effect);
        Assert.Equal(S(3), copy.SourceIn);
    }

    /// <summary>«<c>Trim</c> final de B, −2 s (abre hueco) | <c>AAAABBBBBBCCCC</c> | <c>AAAABBBB··CCCC</c>».</summary>
    [Fact]
    public void Trim_final_de_B_abre_hueco()
    {
        var f = new Fixture();
        TimelineEdits.Trim(f.Ctx, "B", TrimEdge.Out, S(-2));
        Assert.Equal("AAAABBBB··CCCC", Fixture.Draw(f.V1));
    }

    /// <summary>«<c>RippleTrim</c> final de B, −2 s (corre lo de después) | <c>AAAABBBBBBCCCC</c> | <c>AAAABBBBCCCC</c>».</summary>
    [Theory]
    [InlineData(RippleScope.Track)]
    [InlineData(RippleScope.AllTracks)]
    [InlineData(RippleScope.AllTracksAndMarkers)]
    public void RippleTrim_final_de_B_corre_lo_de_despues(RippleScope scope)
    {
        var f = new Fixture();
        TimelineEdits.RippleTrim(f.Ctx, "B", TrimEdge.Out, S(-2), scope);
        Assert.Equal("AAAABBBBCCCC", Fixture.Draw(f.V1));
    }

    /// <summary>«<c>Roll</c> corte B/C, +2 s | <c>AAAABBBBBBCCCC</c> | <c>AAAABBBBBBBBCC</c>».</summary>
    [Fact]
    public void Roll_corte_BC()
    {
        var f = new Fixture();
        TimelineEdits.Roll(f.Ctx, "B", S(2));
        Assert.Equal("AAAABBBBBBBBCC", Fixture.Draw(f.V1));
        Assert.Equal(S(32), f.Clip("C").SourceIn);
    }

    /// <summary>«<c>Slip</c> B, +2 s (otra parte del archivo) | B muestra 0-6 s | B muestra 2-8 s».</summary>
    [Fact]
    public void Slip_B_muestra_otra_parte()
    {
        var f = new Fixture();
        TimelineEdits.Slip(f.Ctx, "B", S(2));
        Clip b = f.Clip("B");
        Assert.Equal((S(2), S(8)), (b.SourceIn, b.SourceOut));
        Assert.Equal("AAAABBBBBBCCCC", Fixture.Draw(f.V1));
    }

    /// <summary>«<c>Slide</c> B, +2 s | <c>AAAABBBBBBCCCC</c> | <c>AAAAAABBBBBBCC</c>».</summary>
    [Fact]
    public void Slide_B()
    {
        var f = new Fixture();
        TimelineEdits.Slide(f.Ctx, "B", S(2));
        Assert.Equal("AAAAAABBBBBBCC", Fixture.Draw(f.V1));
        Assert.Equal((S(0), S(6)), (f.Clip("B").SourceIn, f.Clip("B").SourceOut));
    }

    /// <summary>«<c>Move</c> B a 8 s, modo <c>Overwrite</c> | <c>AAAABBBBBBCCCC</c> | <c>AAAA····BBBBBB</c>».</summary>
    [Fact]
    public void Move_B_a_8_s_sobrescribiendo()
    {
        var f = new Fixture();
        TimelineEdits.Move(f.Ctx, "B", S(8), null, PlacementMode.Overwrite);
        Assert.Equal("AAAA····BBBBBB", Fixture.Draw(f.V1));
        Assert.Null(f.Sequence.FindClip("C"));
    }

    /// <summary>«<c>Insert</c> X en 6 s | <c>AAAABBBBBBCCCC</c> | <c>AAAABBXXXBBBBCCCC</c>».</summary>
    [Fact]
    public void Insert_X_en_6_s()
    {
        var f = new Fixture();
        f.NewX(S(6), PlacementMode.Insert);
        Assert.Equal("AAAABBXXXbbbbCCCC", Fixture.Draw(f.V1, "b"));
    }

    /// <summary>«<c>Overwrite</c> X en 6 s | <c>AAAABBBBBBCCCC</c> | <c>AAAABBXXXBCCCC</c>».</summary>
    [Fact]
    public void Overwrite_X_en_6_s()
    {
        var f = new Fixture();
        f.NewX(S(6), PlacementMode.Overwrite);
        Assert.Equal("AAAABBXXXbCCCC", Fixture.Draw(f.V1, "b"));
    }

    /// <summary>«<c>Lift</c> B / <c>RippleDelete</c> B | <c>AAAABBBBBBCCCC</c> | <c>AAAA······CCCC</c> / <c>AAAACCCC</c>».</summary>
    [Fact]
    public void Lift_y_RippleDelete_B()
    {
        var lifted = new Fixture();
        TimelineEdits.Lift(lifted.Ctx, ["B"]);
        Assert.Equal("AAAA······CCCC", Fixture.Draw(lifted.V1));

        foreach (RippleScope scope in Enum.GetValues<RippleScope>())
        {
            var rippled = new Fixture();
            TimelineEdits.RippleDelete(rippled.Ctx, ["B"], scope);
            Assert.Equal("AAAACCCC", Fixture.Draw(rippled.V1));
        }
    }

    // ── Reglas comunes ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«modifica la secuencia en el sitio».</summary>
    [Fact]
    public void Modifica_la_secuencia_en_el_sitio()
    {
        var f = new Fixture();
        Clip b = f.Clip("B");
        Track v1 = f.V1;

        TimelineEdits.Trim(f.Ctx, "B", TrimEdge.Out, S(-2));

        Assert.Same(f.Sequence, f.Project.Sequences[0]);
        Assert.Same(v1, f.Sequence.VideoTracks[0]);
        Assert.Same(b, f.Clip("B"));
        Assert.Equal(S(8), b.End);
    }

    /// <summary>«respeta los enlaces»: el enlazado se edita con el mismo <c>delta</c>.</summary>
    [Fact]
    public void Respeta_los_enlaces()
    {
        var f = new Fixture(linkedAudio: true);

        TimelineEdits.Trim(f.Ctx, "B", TrimEdge.Out, S(-2));
        TimelineEdits.Slip(f.Ctx, "A", S(1));
        TimelineEdits.Move(f.Ctx, "C", S(11), null, PlacementMode.Strict);

        Assert.Equal(Fixture.Draw(f.V1).ToLowerInvariant(), Fixture.Draw(f.A1));
        Assert.Equal(f.Clip("A").SourceIn, f.Clip("a").SourceIn);
        Assert.Equal(f.Clip("B").SourceOut, f.Clip("b").SourceOut);
    }

    private static readonly Dictionary<string, Action<Fixture>> LockedEdits = new()
    {
        ["Split"] = f => TimelineEdits.Split(f.Ctx, "B", S(7)),
        ["Trim"] = f => TimelineEdits.Trim(f.Ctx, "B", TrimEdge.Out, S(-2)),
        ["RippleTrim"] = f => TimelineEdits.RippleTrim(f.Ctx, "B", TrimEdge.Out, S(-2), RippleScope.Track),
        ["Roll"] = f => TimelineEdits.Roll(f.Ctx, "B", S(2)),
        ["Slip"] = f => TimelineEdits.Slip(f.Ctx, "B", S(2)),
        ["Slide"] = f => TimelineEdits.Slide(f.Ctx, "B", S(2)),
        ["Move"] = f => TimelineEdits.Move(f.Ctx, "B", S(8), null, PlacementMode.Overwrite),
        ["MoveToLocked"] = f => TimelineEdits.Move(f.Ctx, "Y", S(24), "v1", PlacementMode.Overwrite),
        ["Insert"] = f => TimelineEdits.InsertMedia(f.Ctx, "m", S(50), S(53), "v1", S(6), PlacementMode.Insert),
        ["Overwrite"] = f => TimelineEdits.InsertMedia(f.Ctx, "m", S(50), S(53), "v1", S(6), PlacementMode.Overwrite),
        ["AppendMedia"] = f => TimelineEdits.AppendMedia(f.Ctx, "m", S(50), S(53), "v1"),
        ["Lift"] = f => TimelineEdits.Lift(f.Ctx, ["B"]),
        ["RippleDelete"] = f => TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.Track),
        ["CloseGap"] = f => TimelineEdits.CloseGap(f.Ctx, "v1", S(19), RippleScope.Track),
        ["ReorderOnMainTrack"] = f => TimelineEdits.ReorderOnMainTrack(f.Ctx, "C", 0),
        ["RemoveTrack"] = f => TimelineEdits.RemoveTrack(f.Ctx, "v1"),
        ["MoveTrack"] = f => TimelineEdits.MoveTrack(f.Ctx, "v1", 1),
        ["Rename"] = f => TimelineEdits.SetTrackState(f.Ctx, "v1", name: "Otra"),
        ["SetClipEnabled"] = f => TimelineEdits.SetClipEnabled(f.Ctx, "B", false),
        ["LinkClips"] = f => TimelineEdits.LinkClips(f.Ctx, ["B", "X2"]),
        ["UnlinkClips"] = f => TimelineEdits.UnlinkClips(f.Ctx, "A"),
        ["SetSpeed"] = f => TimelineEdits.SetSpeed(f.Ctx, "B", new Rational(2, 1)),
        ["SetCurve"] = f => TimelineEdits.SetCurve(f.Ctx, "B", null),
        ["SetReverse"] = f => TimelineEdits.SetReverse(f.Ctx, "B", true),
        ["SetPreservePitch"] = f => TimelineEdits.SetPreservePitch(f.Ctx, "C", false),
        ["RateStretch"] = f => TimelineEdits.RateStretch(f.Ctx, "B", TrimEdge.Out, S(-2)),
        ["FreezeFrame"] = f => TimelineEdits.FreezeFrame(f.Ctx, "B", S(6)),
        ["AddEffect"] = f => EffectEdits.AddEffect(f.Ctx, "B", BuiltInEffects.Transform),
        ["AddTrackEffect"] = f => EffectEdits.AddEffect(f.Ctx, "v1", BuiltInEffects.Transform),
        ["RemoveEffect"] = f => EffectEdits.RemoveEffect(f.Ctx, "C", "fx"),
        ["SetEffectEnabled"] = f => EffectEdits.SetEffectEnabled(f.Ctx, "C", "fx", false),
        ["AddTransition"] = f => TransitionEdits.AddTransition(f.Ctx, "B", BuiltInTransitions.Crossfade, S(1)),
        ["RemoveTransition"] = f => TransitionEdits.RemoveTransition(f.Ctx, "tr"),
        ["SetTransitionDuration"] = f => TransitionEdits.SetDuration(f.Ctx, "tr", S(1)),
    };

    public static TheoryData<string> LockedEditNames => [.. LockedEdits.Keys];

    /// <summary>«rechaza tocar pistas bloqueadas»: lanza <c>EditException</c> y la secuencia no cambia.</summary>
    [Theory]
    [MemberData(nameof(LockedEditNames))]
    public void Rechaza_tocar_pistas_bloqueadas(string edit)
    {
        static Fixture Prepared()
        {
            var f = new Fixture();
            f.Clip("A").LinkId = "lx";
            f.V2.Clips.Add(C("X2", 0, 40, 2, "lx"));
            f.V2.Clips.Add(C("Y", 30, 40, 2));
            f.Clip("C").Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform });
            TimelineEdits.SetSpeed(f.Ctx, "C", new Rational(1, 2));
            f.V1.Clips.Add(C("D", 20, 40, 2));
            TransitionEdits.AddTransition(f.Ctx, "A", BuiltInTransitions.Crossfade, S(2));
            f.V1.Transitions[0].Id = "tr";
            return f;
        }

        LockedEdits[edit](Prepared());

        Fixture locked = Prepared();
        locked.V1.Locked = true;
        string before = locked.Snapshot();

        Assert.Throws<EditException>(() => LockedEdits[edit](locked));

        Assert.Equal(before, locked.Snapshot());
    }

    /// <summary>«rechaza tocar pistas bloqueadas»: también cuando la bloqueada es la de un enlazado.</summary>
    [Theory]
    [InlineData("Trim")]
    [InlineData("Split")]
    [InlineData("Move")]
    [InlineData("Lift")]
    [InlineData("RippleDelete")]
    [InlineData("SetSpeed")]
    public void Rechaza_editar_un_clip_cuyo_enlazado_esta_bloqueado(string edit)
    {
        var f = new Fixture(linkedAudio: true);
        f.A1.Locked = true;
        string before = f.Snapshot();

        Assert.Throws<EditException>(() => LockedEdits[edit](f));

        Assert.Equal(before, f.Snapshot());
    }

    /// <summary>«termina siempre con <c>EditContext.EnsureValid()</c>»: que reconcilia las transiciones (<c>TransitionOps.Reconcile</c> «reajusta las transiciones tras cada edición»).</summary>
    [Fact]
    public void Termina_con_EnsureValid_y_reajusta_las_transiciones()
    {
        var f = new Fixture();
        string tr = TransitionEdits.AddTransition(f.Ctx, "B", BuiltInTransitions.Crossfade, S(2));

        TimelineEdits.Lift(f.Ctx, ["C"]);

        Assert.Null(f.V1.FindTransition(tr));
        Assert.Empty(ModelValidator.ValidateSequence(f.Project, f.Sequence));
    }

    /// <summary>«Si no puede hacer algo, lanza <c>EditException</c> con un mensaje listo para mostrar».</summary>
    [Fact]
    public void Si_no_puede_lanza_EditException_con_mensaje()
    {
        var f = new Fixture();
        Action[] impossible =
        [
            () => TimelineEdits.Split(f.Ctx, "B", S(2)),
            () => TimelineEdits.Split(f.Ctx, "nada", S(5)),
            () => TimelineEdits.Move(f.Ctx, "A", S(2), null, PlacementMode.Strict),
            () => TimelineEdits.Move(f.Ctx, "A", S(-1), null, PlacementMode.Overwrite),
            () => TimelineEdits.CloseGap(f.Ctx, "v1", S(5), RippleScope.Track),
            () => TimelineEdits.AddRegion(f.Ctx, S(5), S(5), "r"),
            () => TimelineEdits.SetSpeed(f.Ctx, "B", new Rational(1000, 1)),
        ];
        string before = f.Snapshot();

        foreach (Action edit in impossible)
        {
            EditException e = Assert.Throws<EditException>(edit);
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
            Assert.DoesNotContain("Exception", e.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, f.Snapshot());
    }

    /// <summary>«Un <c>delta</c> positivo mueve a la derecha».</summary>
    [Fact]
    public void Delta_positivo_mueve_a_la_derecha()
    {
        var f = new Fixture();
        TimelineEdits.Trim(f.Ctx, "C", TrimEdge.Out, S(1));
        TimelineEdits.Trim(f.Ctx, "C", TrimEdge.In, S(1));
        Assert.Equal((S(11), S(15)), (f.Clip("C").Start, f.Clip("C").End));
    }

    // ── Move, AppendMedia y CloseGap ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>«<c>Move</c> tiene tres modos (<c>PlacementMode</c>: <c>Strict</c>, <c>Overwrite</c>, <c>Insert</c>)».</summary>
    [Fact]
    public void Move_tiene_tres_modos()
    {
        Assert.Equal([PlacementMode.Strict, PlacementMode.Overwrite, PlacementMode.Insert], Enum.GetValues<PlacementMode>());

        var strict = new Fixture();
        Assert.Throws<EditException>(() => TimelineEdits.Move(strict.Ctx, "A", S(8), null, PlacementMode.Strict));
        TimelineEdits.Move(strict.Ctx, "C", S(15), null, PlacementMode.Strict);
        Assert.Equal("AAAABBBBBB·····CCCC", Fixture.Draw(strict.V1));

        var insert = new Fixture();
        TimelineEdits.Move(insert.Ctx, "C", S(4), null, PlacementMode.Insert);
        Assert.Equal("AAAACCCCBBBBBB", Fixture.Draw(insert.V1));
    }

    /// <summary>«también existen <c>AppendMedia</c> y <c>CloseGap</c>».</summary>
    [Fact]
    public void AppendMedia_y_CloseGap()
    {
        var f = new Fixture();
        TimelineEdits.Lift(f.Ctx, ["B"]);
        string x = TimelineEdits.AppendMedia(f.Ctx, "m", S(50), S(53), "v1")[0];
        Assert.Equal(S(14), f.Clip(x).Start);

        TimelineEdits.CloseGap(f.Ctx, "v1", S(5), RippleScope.Track);
        Assert.Equal((S(4), S(8)), (f.Clip("C").Start, f.Clip(x).Start));
    }

    // ── RippleScope ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>«<c>Track</c> corre las pistas del grupo editado y las de los enlazados de lo que se corre, para no desincronizarlos».</summary>
    [Fact]
    public void RippleScope_Track_corre_las_pistas_de_los_enlazados()
    {
        var f = new Fixture();
        f.Clip("C").LinkId = "lc";
        f.V2.Clips.Add(C("t", 12, 40, 2, "lc"));
        f.V2.Clips.Add(C("u", 0, 40, 2));

        TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.Track);

        Assert.Equal(S(4), f.Clip("C").Start);
        Assert.Equal(S(6), f.Clip("t").Start);
        Assert.Equal(S(0), f.Clip("u").Start);
        Assert.Empty(f.A1.Clips);
    }

    /// <summary>«(si una no puede cerrar el tramo, la edición se rechaza)».</summary>
    [Fact]
    public void RippleScope_Track_rechaza_si_una_pista_no_puede_cerrar()
    {
        var f = new Fixture();
        f.Clip("C").LinkId = "lc";
        f.V2.Clips.Add(C("u", 5, 40, 2));
        f.V2.Clips.Add(C("t", 12, 40, 2, "lc"));
        string before = f.Snapshot();

        Assert.Throws<EditException>(() => TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.Track));
        Assert.Equal(before, f.Snapshot());

        // RippleTrim no deshace lo que ya recortó: de eso se encarga SequenceEditCommand al restaurar la secuencia.
        Assert.Throws<EditException>(() => TimelineEdits.RippleTrim(f.Ctx, "B", TrimEdge.Out, S(-4), RippleScope.Track));
    }

    /// <summary>«<c>AllTracks</c> corre todas las no bloqueadas».</summary>
    [Fact]
    public void RippleScope_AllTracks_corre_todas_las_no_bloqueadas()
    {
        var f = new Fixture();
        f.V2.Clips.Add(C("t", 12, 40, 2));
        var locked = new Track { Id = "a2", Kind = TrackKind.Audio, Name = "Audio 2", Locked = true, Clips = [C("l", 12, 40, 2)] };
        f.Sequence.AudioTracks.Add(locked);
        f.Sequence.Markers.Add(new Marker { Id = "mk", Time = S(12), Name = "m" });

        TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.AllTracks);

        Assert.Equal(S(4), f.Clip("C").Start);
        Assert.Equal(S(6), f.Clip("t").Start);
        Assert.Equal(S(12), f.Clip("l").Start);
        Assert.Equal(S(12), f.Sequence.Markers[0].Time);
    }

    /// <summary>
    /// «<c>AllTracksAndMarkers</c> además corre marcadores y regiones (lo que cae dentro de un tramo cerrado queda en su
    /// inicio y una región que queda vacía se borra)».
    /// </summary>
    [Fact]
    public void RippleScope_AllTracksAndMarkers_corre_marcadores_y_regiones()
    {
        var f = new Fixture();
        string after = TimelineEdits.AddMarker(f.Ctx, S(12), "después");
        string inside = TimelineEdits.AddMarker(f.Ctx, S(7), "dentro");
        string before = TimelineEdits.AddMarker(f.Ctx, S(2), "antes");
        string empty = TimelineEdits.AddRegion(f.Ctx, S(5), S(9), "vacía");
        string straddling = TimelineEdits.AddRegion(f.Ctx, S(8), S(12), "a caballo");

        TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.AllTracksAndMarkers);

        Marker M(string id) => f.Sequence.Markers.Single(m => m.Id == id);
        Assert.Equal((S(6), S(4), S(2)), (M(after).Time, M(inside).Time, M(before).Time));
        Assert.DoesNotContain(f.Sequence.Regions, r => r.Id == empty);
        Region kept = Assert.Single(f.Sequence.Regions);
        Assert.Equal((straddling, S(4), S(6)), (kept.Id, kept.Start, kept.End));
    }

    /// <summary>«<c>ReorderOnMainTrack</c> lleva un clip a otra posición de su pista y la deja sin huecos, con sus enlazados».</summary>
    [Fact]
    public void ReorderOnMainTrack_sin_huecos_con_enlazados()
    {
        var f = new Fixture(linkedAudio: true);
        TimelineEdits.Lift(f.Ctx, ["B"]);
        f.Clip("C").Start = S(11);
        f.Clip("c").Start = S(11);

        TimelineEdits.ReorderOnMainTrack(f.Ctx, "C", 0);

        Assert.Equal("CCCCAAAA", Fixture.Draw(f.V1));
        Assert.Equal((S(0), S(4)), (f.Clip("c").Start, f.Clip("a").Start));
    }

    // ── Marcadores y regiones ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// «Los marcadores y regiones tienen <c>AddMarker</c>, <c>RemoveMarker</c>, <c>MoveMarker</c>, <c>RenameMarker</c>,
    /// <c>EditMarker</c> (nombre, color y nota), <c>AddRegion</c>, <c>EditRegion</c> y <c>RemoveRegion</c>».
    /// </summary>
    [Fact]
    public void Marcadores_y_regiones()
    {
        var f = new Fixture();
        string marker = TimelineEdits.AddMarker(f.Ctx, S(3), "uno");
        TimelineEdits.MoveMarker(f.Ctx, marker, S(5));
        TimelineEdits.RenameMarker(f.Ctx, marker, "dos");
        Assert.Equal((S(5), "dos"), (f.Sequence.Markers[0].Time, f.Sequence.Markers[0].Name));

        TimelineEdits.EditMarker(f.Ctx, marker, "tres", "#FF0000", "nota");
        Marker m = Assert.Single(f.Sequence.Markers);
        Assert.Equal(("tres", "#FF0000", "nota"), (m.Name, m.Color, m.Note));

        TimelineEdits.RemoveMarker(f.Ctx, marker);
        Assert.Empty(f.Sequence.Markers);

        string region = TimelineEdits.AddRegion(f.Ctx, S(1), S(4), "r");
        TimelineEdits.EditRegion(f.Ctx, region, "s", "#00FF00", "otra", S(2), S(6));
        Region r = Assert.Single(f.Sequence.Regions);
        Assert.Equal(("s", "#00FF00", "otra", S(2), S(6)), (r.Name, r.Color, r.Note, r.Start, r.End));

        TimelineEdits.RemoveRegion(f.Ctx, region);
        Assert.Empty(f.Sequence.Regions);
    }

    // ── Pistas y estado ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// «las operaciones de pistas y estado: <c>AddTrack</c>, <c>RemoveTrack</c>, <c>MoveTrack</c>, <c>SetTrackState</c>,
    /// <c>SetClipEnabled</c>, <c>LinkClips</c> y <c>UnlinkClips</c>».
    /// </summary>
    [Fact]
    public void Pistas_y_estado()
    {
        var f = new Fixture(linkedAudio: true);

        string v3 = TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, 2, "Video 3");
        Assert.Equal(v3, f.Sequence.VideoTracks[2].Id);
        TimelineEdits.MoveTrack(f.Ctx, v3, 0);
        Assert.Equal(v3, f.Sequence.VideoTracks[0].Id);
        TimelineEdits.SetTrackState(f.Ctx, v3, muted: true, hidden: true, name: "Arriba", locked: true);
        Track top = f.Sequence.VideoTracks[0];
        Assert.True(top.Muted && top.Hidden && top.Locked);
        Assert.Equal("Arriba", top.Name);
        TimelineEdits.SetTrackState(f.Ctx, v3, locked: false);
        TimelineEdits.RemoveTrack(f.Ctx, v3);
        Assert.DoesNotContain(f.Sequence.VideoTracks, t => t.Id == v3);

        TimelineEdits.SetClipEnabled(f.Ctx, "B", false);
        Assert.False(f.Clip("B").Enabled || f.Clip("b").Enabled);

        TimelineEdits.UnlinkClips(f.Ctx, "B");
        Assert.Null(f.Clip("B").LinkId);
        Assert.Null(f.Clip("b").LinkId);

        string link = TimelineEdits.LinkClips(f.Ctx, ["B", "b"]);
        Assert.Equal(link, f.Clip("B").LinkId);
        Assert.Equal(link, f.Clip("b").LinkId);
    }

    // ── Efectos, transiciones y velocidad ────────────────────────────────────────────────────────────────────────────

    /// <summary>«<c>EffectEdits</c> (añadir, quitar, mover y activar efectos, parámetros y keyframes, en un clip o en una pista)».</summary>
    [Fact]
    public void EffectEdits_en_clip_y_en_pista()
    {
        var f = new Fixture();
        foreach (string host in new[] { "B", "v1" })
        {
            string first = EffectEdits.AddEffect(f.Ctx, host, BuiltInEffects.Transform);
            string second = EffectEdits.AddEffect(f.Ctx, host, BuiltInEffects.Transform);
            EffectEdits.MoveEffect(f.Ctx, host, second, 0);
            EffectEdits.SetEffectEnabled(f.Ctx, host, first, false);
            EffectEdits.SetParameter(f.Ctx, host, first, BuiltInEffects.TransformParameters.Opacity, 0.5, S(1));
            EffectEdits.SetKeyframe(f.Ctx, host, first, BuiltInEffects.TransformParameters.Scale, S(1), 2);
            EffectEdits.RemoveEffect(f.Ctx, host, second);

            List<EffectInstance> effects = host == "B" ? f.Clip("B").Effects : f.V1.Effects;
            EffectInstance kept = Assert.Single(effects);
            Assert.Equal(first, kept.Id);
            Assert.False(kept.Enabled);
            Assert.Equal(0.5, kept.Parameters[BuiltInEffects.TransformParameters.Opacity].Value);
            Assert.Single(kept.Parameters[BuiltInEffects.TransformParameters.Scale].Keyframes);
        }
    }

    /// <summary>«<c>TransitionEdits</c> (añadir, quitar y cambiar duración, tipo, parámetros y opciones)».</summary>
    [Fact]
    public void TransitionEdits_basicas()
    {
        var f = new Fixture();
        string tr = TransitionEdits.AddTransition(f.Ctx, "A", BuiltInTransitions.Crossfade, S(1));
        TransitionEdits.SetDuration(f.Ctx, tr, S(2));
        Assert.Equal(S(2), f.V1.FindTransition(tr)!.Duration);

        TransitionEdits.RemoveTransition(f.Ctx, tr);
        Assert.Null(f.V1.FindTransition(tr));
    }

    /// <summary>
    /// «El cambio de velocidad mantiene siempre el magnetismo (D-026), en cualquier pista …: al acortarse el clip, el
    /// siguiente se une y la transición se queda».
    /// </summary>
    [Fact]
    public void Velocidad_al_acortarse_el_siguiente_se_une_y_la_transicion_se_queda()
    {
        var f = new Fixture();
        f.V2.Clips.AddRange([C("P", 0, 0, 4), C("Q", 4, 0, 6), C("R", 10, 30, 4)]);
        string tr = TransitionEdits.AddTransition(f.Ctx, "Q", BuiltInTransitions.Crossfade, S(1));

        TimelineEdits.SetSpeed(f.Ctx, "Q", new Rational(2, 1));

        Assert.Equal(S(7), f.Clip("Q").End);
        Assert.Equal(S(7), f.Clip("R").Start);
        Assert.NotNull(f.V2.FindTransition(tr));
    }

    /// <summary>«al crecer respeta los huecos y solo empuja lo que pisa».</summary>
    [Fact]
    public void Velocidad_al_crecer_respeta_los_huecos_y_solo_empuja_lo_que_pisa()
    {
        var f = new Fixture();
        TimelineEdits.Lift(f.Ctx, ["B"]);
        f.V1.Clips.Insert(1, C("B", 4, 0, 2));

        TimelineEdits.SetSpeed(f.Ctx, "B", new Rational(1, 2));

        Assert.Equal(S(8), f.Clip("B").End);
        Assert.Equal(S(10), f.Clip("C").Start);

        TimelineEdits.SetSpeed(f.Ctx, "B", new Rational(1, 4));

        Assert.Equal(S(12), f.Clip("B").End);
        Assert.Equal(S(12), f.Clip("C").Start);
    }

    /// <summary>«respetando el <c>RippleScope</c> de la sesión».</summary>
    [Fact]
    public void Velocidad_respeta_el_RippleScope()
    {
        var track = new Fixture();
        track.V2.Clips.Add(C("t", 12, 40, 2));
        TimelineEdits.SetSpeed(track.Ctx, "B", new Rational(2, 1), RippleScope.Track);
        Assert.Equal((S(7), S(12)), (track.Clip("C").Start, track.Clip("t").Start));

        var all = new Fixture();
        all.V2.Clips.Add(C("t", 12, 40, 2));
        string marker = TimelineEdits.AddMarker(all.Ctx, S(12), "m");
        TimelineEdits.SetSpeed(all.Ctx, "B", new Rational(2, 1), RippleScope.AllTracksAndMarkers);
        Assert.Equal((S(7), S(9)), (all.Clip("C").Start, all.Clip("t").Start));
        Assert.Equal(S(9), all.Sequence.Markers.Single(m => m.Id == marker).Time);
    }

    /// <summary>«<c>TimelineEdits.Speed</c> (<c>SetSpeed</c>, <c>SetCurve</c>, <c>SetReverse</c>, <c>SetPreservePitch</c>, <c>RateStretch</c> y <c>FreezeFrame</c>)».</summary>
    [Fact]
    public void Operaciones_de_velocidad()
    {
        var f = new Fixture();

        TimelineEdits.SetSpeed(f.Ctx, "B", new Rational(2, 1));
        Assert.Equal(new Rational(2, 1), f.Clip("B").Speed!.Rate);
        TimelineEdits.SetPreservePitch(f.Ctx, "B", false);
        Assert.False(f.Clip("B").Speed!.PreservePitch);
        TimelineEdits.SetReverse(f.Ctx, "B", true);
        Assert.True(f.Clip("B").Speed!.Reverse);
        TimelineEdits.SetCurve(f.Ctx, "B", null);
        Assert.Equal(new Rational(1, 1), f.Clip("B").Speed?.Rate ?? new Rational(1, 1));

        TimelineEdits.RateStretch(f.Ctx, "A", TrimEdge.Out, S(4));
        Assert.Equal(S(8), f.Clip("A").End);
        Assert.Equal(new Rational(1, 2), f.Clip("A").Speed!.Rate);

        string frozen = TimelineEdits.FreezeFrame(f.Ctx, "A", S(2));
        Assert.True(f.Clip(frozen).Speed!.Freeze);
        Assert.Equal((S(2), S(5)), (f.Clip(frozen).Start, f.Clip(frozen).End));
    }

    /// <summary>«<c>TrackKeyframes</c> mueve los keyframes de pista con los clips».</summary>
    [Fact]
    public void Los_keyframes_de_pista_se_mueven_con_los_clips()
    {
        var f = new Fixture();
        string fx = EffectEdits.AddEffect(f.Ctx, "v1", BuiltInEffects.Transform);
        EffectEdits.SetKeyframe(f.Ctx, "v1", fx, BuiltInEffects.TransformParameters.Opacity, S(12), 0.25);

        TimelineEdits.RippleDelete(f.Ctx, ["B"], RippleScope.Track);

        List<Keyframe> keys = f.V1.Effects[0].Parameters[BuiltInEffects.TransformParameters.Opacity].Keyframes;
        Assert.Contains(keys, k => k.Time == S(6) && k.Value == 0.25);
        Assert.DoesNotContain(keys, k => k.Time == S(12));
    }

    /// <summary>«y <c>TransitionOps.Reconcile</c> reajusta las transiciones tras cada edición»: una transición que ya no cabe se acorta.</summary>
    [Fact]
    public void Las_transiciones_se_reajustan_tras_recortar()
    {
        var f = new Fixture();
        string tr = TransitionEdits.AddTransition(f.Ctx, "B", BuiltInTransitions.Crossfade, S(4));
        Time before = f.V1.FindTransition(tr)!.Duration;

        TimelineEdits.RippleTrim(f.Ctx, "C", TrimEdge.Out, S(-3), RippleScope.Track);

        TransitionInstance after = f.V1.FindTransition(tr)!;
        Assert.True(after.Duration < before);
        Assert.Empty(ModelValidator.ValidateSequence(f.Project, f.Sequence));
    }
}
