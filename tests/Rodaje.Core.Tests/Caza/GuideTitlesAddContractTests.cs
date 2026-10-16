using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Añadir un título» de <c>docs/guia/titulos-y-subtitulos.md</c>, comprobado en las ediciones de Core.
/// Cada prueba cita la frase.
/// </summary>
public sealed class GuideTitlesAddContractTests
{
    private static ClipGenerator NewTitle() => new() { Text = "Hola", Style = ModelSamples.FullStyle() };

    private static string Insert(TrimFixture f, Time at, out IReadOnlyList<string> created)
    {
        created = TitleEdits.InsertTitle(f.Ctx, at, TitleEdits.DefaultDuration, NewTitle(), "Texto");
        return created[0];
    }

    // «pone un título de 5 s en el cursor»
    [Fact]
    public void Titulo_nuevo_dura_5_s_y_empieza_en_el_cursor()
    {
        Assert.Equal(Time.FromSeconds(5), TitleEdits.DefaultDuration);
        TrimFixture f = new();
        string id = Insert(f, F(37), out _);

        Clip clip = f.Clip(id);
        Assert.Equal(F(37), clip.Start);
        Assert.Equal(Time.FromSeconds(5), clip.Duration);
        Assert.NotNull(clip.Generator);
        f.AssertValid();
    }

    // «en la pista de video libre más alta»
    [Fact]
    public void Va_a_la_pista_libre_mas_alta()
    {
        TrimFixture f = new();
        string v3 = TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, 2, "V3");
        string v4 = TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, 3, "V4");
        f.AddTitle(v4, "ocupa", 0, 50, 100);

        string id = Insert(f, F(110), out IReadOnlyList<string> created);
        Assert.Single(created);
        Assert.Same(f.Track(v3), f.Sequence.GetClip(id).Track);

        // Fuera del tramo ocupado, la más alta vuelve a ser V4.
        string later = Insert(f, F(500), out _);
        Assert.Same(f.Track(v4), f.Sequence.GetClip(later).Track);
        f.AssertValid();
    }

    // «(sin contar la principal…)»
    [Fact]
    public void Nunca_va_a_la_principal_aunque_este_libre()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "ocupa", 0, 200, 0);
        string id = Insert(f, F(10), out IReadOnlyList<string> created);

        Assert.Equal(2, created.Count);
        Assert.Empty(f.Track("v1").Clips);
        Assert.NotSame(f.Track("v1"), f.Sequence.GetClip(id).Track);
    }

    // «(sin contar … las bloqueadas ni las de subtítulos)»
    [Fact]
    public void Salta_las_bloqueadas_y_las_de_subtitulos()
    {
        TrimFixture f = new TrimFixture().Lock("v2");
        string subtitles = TitleEdits.AddSubtitleTrack(f.Ctx, "Subtítulos");
        string id = Insert(f, F(0), out IReadOnlyList<string> created);

        Assert.Equal(2, created.Count);
        Assert.Empty(f.Track("v2").Clips);
        Assert.Empty(f.Track(subtitles).Clips);
        Assert.Same(f.Track(created[1]), f.Sequence.GetClip(id).Track);
    }

    // «o en una nueva «Texto»»
    [Fact]
    public void Sin_sitio_crea_una_pista_Texto_encima_de_todas()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "ocupa", 0, 200, 0);
        string id = Insert(f, F(10), out IReadOnlyList<string> created);

        Assert.Equal(2, created.Count);
        Track track = f.Track(created[1]);
        Assert.Equal("Texto", track.Name);
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Null(track.Role);
        Assert.Same(track, f.Sequence.VideoTracks[^1]);
        Assert.Same(track, f.Sequence.GetClip(id).Track);
        f.AssertValid();
    }

    // «arrastrarlo a la línea de tiempo lo pone donde caiga»
    [Fact]
    public void Soltar_un_estilo_lo_pone_en_la_pista_y_el_instante_donde_cae()
    {
        TrimFixture f = new();
        string id = TitleEdits.InsertGenerator(f.Ctx, "v1", F(42), TitleEdits.DefaultDuration, NewTitle());

        Assert.Same(f.Track("v1"), f.Sequence.GetClip(id).Track);
        Assert.Equal(F(42), f.Clip(id).Start);
        f.AssertValid();
    }

    // «doble clic en un estilo cambia el estilo del título seleccionado»
    [Fact]
    public void Aplicar_un_estilo_cambia_el_del_titulo_y_guarda_una_copia()
    {
        TrimFixture f = new();
        string id = Insert(f, F(0), out _);
        TextStyle style = ModelSamples.FullStyle();
        style.Name = "Otro";

        TitleEdits.SetStyle(f.Ctx, id, style);
        Assert.Equal("Otro", f.Clip(id).Generator!.Style!.Name);
        Assert.NotSame(style, f.Clip(id).Generator!.Style);
    }

    // «El clip del título se mueve, corta y recorta como cualquier otro»
    [Fact]
    public void El_titulo_se_mueve_corta_y_recorta()
    {
        TrimFixture f = new();
        string id = Insert(f, F(0), out _);

        TimelineEdits.Move(f.Ctx, id, F(50), "v1", PlacementMode.Strict);
        Assert.Same(f.Track("v1"), f.Sequence.GetClip(id).Track);
        Assert.Equal(F(50), f.Clip(id).Start);

        TimelineEdits.Trim(f.Ctx, id, TrimEdge.In, F(10));
        Assert.Equal(F(60), f.Clip(id).Start);
        Assert.Equal(F(115), f.Clip(id).Duration);

        IReadOnlyList<string> right = TimelineEdits.Split(f.Ctx, id, F(100));
        Assert.Equal(F(40), f.Clip(id).Duration);
        Assert.Equal(F(100), f.Clip(right[0]).Start);
        Assert.Equal(F(75), f.Clip(right[0]).Duration);
        Assert.Equal("Hola", f.Clip(right[0]).Generator!.Text);
        f.AssertValid();
    }

    // «no tiene tope de duración»
    [Fact]
    public void El_titulo_no_tiene_tope_de_duracion_por_ningun_borde()
    {
        TrimFixture f = new();
        string id = TitleEdits.InsertGenerator(f.Ctx, "v2", F(1000), TitleEdits.DefaultDuration, NewTitle());

        TimelineEdits.Trim(f.Ctx, id, TrimEdge.Out, Time.FromSeconds(3600));
        TimelineEdits.Trim(f.Ctx, id, TrimEdge.In, F(-1000));
        Assert.Equal(Time.Zero, f.Clip(id).Start);
        Assert.Equal(F(1000) + Time.FromSeconds(3605), f.Clip(id).Duration);
        f.AssertValid();
    }
}
