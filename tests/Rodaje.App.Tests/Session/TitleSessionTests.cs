using Rodaje.App.Services;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Effects;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Paquetes;
using Rodaje.Paquetes.Content;

namespace Rodaje.App.Tests.Session;

/// <summary>Crear títulos (T, menú, biblioteca, soltar) y pistas de subtítulos desde la sesión.</summary>
public class TitleSessionTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal con a [0,10) y su audio enlazado; V1 es la única de video.</summary>
    private static async Task<EditorSession> OneClip()
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        return session;
    }

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds) });

    private static LibraryElement Style(string reference) =>
        new("Estilo", "", "", reference, true, reference == TitleStyles.BuiltInReference ? null : PackageOrigin.Installed, ItemKind.TextStyle);

    private static TextStylePreset Preset() => new(
        "neon", "Neón", new TextStyleFont("Inter", 800, false, "rodaje-fuentes@1.0.0/inter", ["Arial"]), 120, 1920, 1080, 1.1, 2,
        TextStyleAlign.Left, "#FF00FFCC", new TextStyleStroke("#000000", 6, TextStyleJoin.Miter), null,
        new TextStyleBackground("#00000080", 24, 12, 8),
        new TextStyleAnimation(0.5, [new PresetCurve("rodaje.transform", "opacity", [new PresetKey(0, 0, PresetInterpolation.Linear), new PresetKey(0.5, 1, PresetInterpolation.EaseOut)])]),
        null);

    [Fact]
    public void T_CreaUnTituloDe5sEnElCursorEnLaPistaLibreDeArribaYLoSelecciona() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        int requests = 0;
        session.TextPanelRequested += (_, _) => requests++;
        At(session, 2);
        int steps = session.History.Count;
        int tracks = session.Sequence.VideoTracks.Count;
        Assert.Equal(2, tracks);

        // El proyecto nuevo trae «Video 2» vacía encima de la principal: el título va ahí.
        Assert.True(session.AddTitleAtPlayhead());

        Assert.Equal(tracks, session.Sequence.VideoTracks.Count);
        Track track = session.Sequence.VideoTracks[1];
        Clip title = Assert.Single(track.Clips);
        Assert.Equal((S(2), S(7)), (title.Start, title.End));
        Assert.Equal(TitleStyles.DefaultText, title.Generator!.Text);
        Assert.Equal(700, title.Generator.Style!.Font.Weight);
        Assert.Equal([title.Id], session.SelectedClipIds);
        Assert.Same(title, session.SelectedTitle);
        Assert.Equal(1, requests);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Añadir texto", session.UndoDescription);

        // Otro en el mismo instante no cabe: va a una pista nueva «Texto» encima de todas; uno más, a «Texto 2».
        Assert.True(session.AddTitleAtPlayhead());
        Assert.Equal("Texto", session.Sequence.VideoTracks[2].Name);
        Assert.True(session.AddTitleAtPlayhead());
        Assert.Equal("Texto 2", session.Sequence.VideoTracks[3].Name);

        // Uno más tarde cabe en la de arriba, que está libre ahí.
        At(session, 8);
        Assert.True(session.AddTitleAtPlayhead());
        Assert.Equal(4, session.Sequence.VideoTracks.Count);
        Assert.Equal(2, session.Sequence.VideoTracks[3].Clips.Count);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(session.Undo());
        }

        Assert.Equal(tracks, session.Sequence.VideoTracks.Count);
        Assert.Empty(session.Sequence.VideoTracks[1].Clips);
    });

    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void LaTeclaTEsAnadirTextoEnLosTresEstilos(ShortcutPreset preset)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = preset;
        Assert.Equal(ActionIds.AddText, registry.FindAction(KeyChord.Parse("T")));
        Assert.Empty(registry.GetKeys(ActionIds.AddSubtitleTrack));
        Assert.Empty(registry.FindConflicts());
    }

    [Fact]
    public void LosAtajosLlamanALaSesion() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        var router = new ShortcutRouter(registry);
        using IDisposable bindings = EditorShortcuts.Bind(router, session);

        Assert.True(router.Invoke(ActionIds.AddText));
        Assert.True(router.Invoke(ActionIds.AddSubtitleTrack));
        Assert.Equal(TrackRoles.Subtitles, session.Sequence.VideoTracks[^1].Role);
        Assert.Equal("Subtítulos", session.Sequence.VideoTracks[^1].Name);
        Assert.NotNull(session.SelectedTitle);
    });

    [Fact]
    public void LaBibliotecaCreaUnTituloOCambiaElEstiloDelSeleccionado() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        var loader = new FakeContentLoader();
        loader.TextStyles["rodaje-titulos@1.0.0/neon"] = Preset();
        var applier = new ElementApplier(session, loader);
        At(session, 1);

        Assert.True(await applier.ApplyAsync(Style(TitleStyles.BuiltInReference)));
        Clip title = session.SelectedTitle!;
        Assert.Equal("Segoe UI", title.Generator!.Style!.Font.Family);

        // Con un título seleccionado, otro estilo lo cambia (una copia con su origen) sin crear otro.
        Assert.True(await applier.ApplyAsync(Style("rodaje-titulos@1.0.0/neon")));
        Assert.Single(session.Sequence.VideoTracks[1].Clips);
        TextStyle style = session.Sequence.GetClip(title.Id).Clip.Generator!.Style!;
        Assert.Equal("rodaje-titulos@1.0.0/neon", style.Source);
        Assert.Equal(("Inter", 800, 120.0), (style.Font.Family, style.Font.Weight, style.Size));
        Assert.Equal(TextAlign.Left, style.Align);
        Assert.Equal(StrokeJoin.Miter, style.Stroke!.Join);
        Assert.Equal([24.0, 12.0], style.Background!.Padding);
        Assert.Equal(Interpolation.EaseOut, style.In!.Curves["opacity"][1].Interp);
        Assert.Null(style.Out);

        // «Añadir texto en el cursor» siempre crea uno nuevo.
        Assert.True(await applier.AddTitleAsync(Style("rodaje-titulos@1.0.0/neon")));
        Assert.Equal(2, session.Sequence.VideoTracks.Sum(t => t.Clips.Count(c => c.Generator is not null)));

        // Un estilo que falta o está dañado avisa y no cambia nada.
        int steps = session.History.Count;
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        loader.Broken.Add("rodaje-titulos@1.0.0/roto");
        Assert.False(await applier.ApplyAsync(Style("rodaje-titulos@1.0.0/falta")));
        Assert.False(await applier.ApplyAsync(Style("rodaje-titulos@1.0.0/roto")));
        Assert.Equal(steps, session.History.Count);
        Assert.Equal(2, notices.Count);
    });

    [Fact]
    public void SoltarUnEstilo_VaALaPistaBajoElPunteroSiEstaLibre()
    {
        var sequence = new Sequence
        {
            VideoTracks =
            [
                new Track { Id = "v1", Kind = TrackKind.Video },
                new Track { Id = "v2", Kind = TrackKind.Video, Clips = [new Clip { Id = "c", Generator = new ClipGenerator(), SourceOut = S(4), Start = S(10) }] },
                new Track { Id = "sub", Kind = TrackKind.Video, Role = TrackRoles.Subtitles },
                new Track { Id = "bloq", Kind = TrackKind.Video, Locked = true },
            ],
            AudioTracks = [new Track { Id = "a1", Kind = TrackKind.Audio }],
        };

        Assert.Same(sequence.VideoTracks[1], TitleDrag.TargetTrack(sequence, sequence.VideoTracks[1], S(0)));
        Assert.Same(sequence.VideoTracks[1], TitleDrag.TargetTrack(sequence, sequence.VideoTracks[1], S(14)));
        Assert.Null(TitleDrag.TargetTrack(sequence, sequence.VideoTracks[1], S(6)));
        Assert.Null(TitleDrag.TargetTrack(sequence, sequence.VideoTracks[0], S(0)));
        Assert.Null(TitleDrag.TargetTrack(sequence, sequence.VideoTracks[2], S(0)));
        Assert.Null(TitleDrag.TargetTrack(sequence, sequence.VideoTracks[3], S(0)));
        Assert.Null(TitleDrag.TargetTrack(sequence, sequence.AudioTracks[0], S(0)));
        Assert.Null(TitleDrag.TargetTrack(sequence, null, S(0)));
    }

    [Fact]
    public void SoltarEnUnaPistaConcretaYPistaDeSubtitulos() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        Assert.True(session.AddSubtitleTrack());
        Track subtitles = session.Sequence.VideoTracks[^1];
        Assert.Equal(TrackRoles.Subtitles, subtitles.Role);

        // Un título suelto no va a la de subtítulos: crea la suya.
        Assert.True(session.AddTitleAt(null, S(3)));
        Assert.Empty(subtitles.Clips);

        // Soltarlo sobre la de subtítulos sí lo pone ahí (un subtítulo es un clip de texto).
        Assert.True(session.AddTitleAt(subtitles.Id, S(0)));
        Assert.Single(subtitles.Clips);

        Assert.True(session.SetSubtitleRole(subtitles.Id, false));
        Assert.Null(session.Sequence.GetTrack(subtitles.Id).Role);
        Assert.True(session.Undo());
        Assert.Equal(TrackRoles.Subtitles, session.Sequence.GetTrack(subtitles.Id).Role);
    });
}
