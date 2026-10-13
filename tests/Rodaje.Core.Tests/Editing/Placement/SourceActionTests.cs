using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Placement;

/// <summary>Reemplazar, ajustar a rellenar y superponer desde el origen, y cortar entre ángulos de una multicámara.</summary>
public sealed class SourceActionTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>V1: a [0,10) del medio «m» (0-10) y b [10,20) (20-30), con audio enlazado; V2: t [12,14). «n» dura 30 s.</summary>
    private static (Project Project, EditContext Ctx) Fixture()
    {
        static Clip C(string id, string media, double start, double sourceIn, double length, string? link = null) =>
            new() { Id = id, MediaId = media, Start = S(start), SourceIn = S(sourceIn), SourceOut = S(sourceIn + length), LinkId = link };
        var sequence = new Sequence
        {
            Id = "seq",
            VideoTracks =
            [
                new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 1", Clips = [C("a", "m", 0, 0, 10, "la"), C("b", "m", 10, 20, 10, "lb")] },
                new Track { Id = "v2", Kind = TrackKind.Video, Name = "Video 2", Clips = [C("t", "m", 12, 40, 2)] },
            ],
            AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio, Name = "Audio 1", Clips = [C("a2", "m", 0, 0, 10, "la"), C("b2", "m", 10, 20, 10, "lb")] }],
        };
        var project = new Project
        {
            Id = "p",
            Media =
            [
                new MediaRef { Id = "m", Name = "toma", Duration = S(60), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "n", Name = "otra", Duration = S(30), HasVideo = true, HasAudio = true },
                new MediaRef { Id = "s", Name = "sonido", Duration = S(30), HasAudio = true },
            ],
            Sequences = [sequence],
        };
        return (project, new EditContext(project, sequence, new SequentialIdGenerator()));
    }

    private static ThreePointRequest Request(string media, double? sIn, double? sOut, double? qIn, double? qOut, double playhead, double? sourcePlayhead = null, string? video = "v1", string? audio = "a1") =>
        new(media, sIn is { } a ? S(a) : null, sOut is { } b ? S(b) : null, qIn is { } c ? S(c) : null, qOut is { } d ? S(d) : null,
            S(playhead), video, audio, PlacementMode.Overwrite, SourcePlayhead: sourcePlayhead is { } p ? S(p) : null);

    private static Clip Get(EditContext ctx, string id) => ctx.Sequence.GetClip(id).Clip;

    [Fact]
    public void Reemplazar_alinea_el_cursor_del_origen_con_el_de_la_secuencia_y_conserva_el_lugar()
    {
        (_, EditContext ctx) = Fixture();

        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, Request("n", null, null, null, null, playhead: 14, sourcePlayhead: 9), ThreePointAction.Replace);

        Clip video = Get(ctx, created[0]);
        Assert.Equal((S(10), S(20), S(5), S(15)), (video.Start, video.End, video.SourceIn, video.SourceOut));
        Assert.Equal(video.LinkId, Get(ctx, created[1]).LinkId);
        Assert.Null(ctx.Sequence.FindClip("b"));
    }

    [Fact]
    public void Reemplazar_con_entrada_marcada_la_pone_al_inicio_del_clip()
    {
        (_, EditContext ctx) = Fixture();

        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, Request("n", 2, null, null, null, playhead: 3), ThreePointAction.Replace);

        Clip video = Get(ctx, created[0]);
        Assert.Equal((S(0), S(10), S(2)), (video.Start, video.End, video.SourceIn));
    }

    [Fact]
    public void Reemplazar_sin_material_o_sin_clip_falla_sin_tocar_nada()
    {
        (_, EditContext ctx) = Fixture();

        Assert.Contains("material", Assert.Throws<EditException>(() =>
            TimelineEdits.ThreePointEdit(ctx, Request("n", 25, null, null, null, playhead: 12), ThreePointAction.Replace)).Message, StringComparison.Ordinal);
        Assert.Contains("No hay un clip", Assert.Throws<EditException>(() =>
            TimelineEdits.ThreePointEdit(ctx, Request("n", null, null, null, null, playhead: 40), ThreePointAction.Replace)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ajustar_a_rellenar_cambia_la_velocidad_para_llenar_el_clip_bajo_el_cursor()
    {
        (_, EditContext ctx) = Fixture();

        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, Request("n", 0, 20, null, null, playhead: 5), ThreePointAction.FitToFill);

        Clip video = Get(ctx, created[0]);
        Assert.Equal((S(0), S(10)), (video.Start, video.End));
        Assert.Equal(new Rational(2, 1), video.Speed!.Rate);
        Assert.Equal((S(0), S(20)), (video.SourceIn, video.SourceOut));
    }

    [Fact]
    public void Ajustar_a_rellenar_con_las_marcas_de_la_secuencia()
    {
        (_, EditContext ctx) = Fixture();

        IReadOnlyList<string> created = TimelineEdits.ThreePointEdit(ctx, Request("n", 0, 4, 2, 10, playhead: 0), ThreePointAction.FitToFill);

        Clip video = Get(ctx, created[0]);
        Assert.Equal((S(2), S(10)), (video.Start, video.End));
        Assert.Equal(new Rational(1, 2), video.Speed!.Rate);
    }

    [Fact]
    public void Superponer_usa_la_pista_libre_de_encima_o_crea_una()
    {
        (_, EditContext ctx) = Fixture();

        // De 0 a 5 la V2 está libre: va ahí.
        IReadOnlyList<string> first = TimelineEdits.ThreePointEdit(ctx, Request("n", 0, 5, null, null, playhead: 0), ThreePointAction.Superimpose);
        Assert.Equal("v2", ctx.Sequence.GetClip(first[0]).Track.Id);
        Assert.Single(first);

        // De 11 a 16 la V2 tiene «t»: se crea una tercera pista.
        IReadOnlyList<string> second = TimelineEdits.ThreePointEdit(ctx, Request("n", 0, 5, null, null, playhead: 11), ThreePointAction.Superimpose);
        Assert.Equal(3, ctx.Sequence.VideoTracks.Count);
        Assert.Same(ctx.Sequence.VideoTracks[2], ctx.Sequence.GetClip(second[0]).Track);
        Assert.Throws<EditException>(() => TimelineEdits.ThreePointEdit(ctx, Request("s", 0, 5, null, null, playhead: 0), ThreePointAction.Superimpose));
    }

    [Fact]
    public void Cambiar_de_angulo_corta_y_muestra_el_mismo_instante_del_otro_angulo()
    {
        (_, EditContext ctx) = Fixture();

        // «n» empezó a grabar 3 s después que «m».
        MulticamAngle[] angles = [new("m", S(0)), new("n", S(3))];
        string right = MulticamEdits.SwitchAngle(ctx, angles, "v1", S(4), 1);

        Clip clip = Get(ctx, right);
        Assert.Equal(("n", S(4), S(10), S(1), S(7)), (clip.MediaId, clip.Start, clip.End, clip.SourceIn, clip.SourceOut));
        Assert.Equal(("m", S(4)), (Get(ctx, "a").MediaId, Get(ctx, "a").End));
        Assert.Equal("m", ctx.Sequence.AudioTracks[0].ClipAt(S(5))!.MediaId);

        // Volver al ángulo 1 en el inicio del tramo no corta: cambia el clip entero.
        string back = MulticamEdits.SwitchAngle(ctx, angles, "v1", S(4), 0);
        Assert.Equal(right, back);
        Assert.Equal(("m", S(4)), (Get(ctx, back).MediaId, Get(ctx, back).SourceIn));
    }

    [Fact]
    public void Un_angulo_sin_material_en_ese_tramo_se_rechaza()
    {
        (_, EditContext ctx) = Fixture();
        MulticamAngle[] angles = [new("m", S(0)), new("n", S(8))];

        Assert.Contains("no tiene material", Assert.Throws<EditException>(() => MulticamEdits.SwitchAngle(ctx, angles, "v1", S(4), 1)).Message, StringComparison.Ordinal);
        Assert.Contains("ya muestra", Assert.Throws<EditException>(() => MulticamEdits.SwitchAngle(ctx, angles, "v1", S(4), 0)).Message, StringComparison.Ordinal);
        Assert.Throws<EditException>(() => MulticamEdits.SwitchAngle(ctx, angles, "v1", S(4), 5));
    }
}
