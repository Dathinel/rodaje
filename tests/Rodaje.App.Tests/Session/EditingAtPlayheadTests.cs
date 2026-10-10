using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

public class EditingAtPlayheadTests
{
    private static (EditorSession Session, FakePlayback Playback) NewSession()
    {
        var playback = new FakePlayback();
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Audio("m.mp3", 30);
        return (new EditorSession(playback, prober, new FakeDialogs(), new SequentialIdGenerator()), playback);
    }

    private static async Task<EditorSession> TwoClips()
    {
        (EditorSession session, _) = NewSession();
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        return session;
    }

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(seconds) });

    private static List<(Time Start, Time End)> MainClips(EditorSession session) =>
        [.. session.MainTrack!.Clips.Select(c => (c.Start, c.End))];

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    [Fact]
    public void Cortar_SinSeleccion_CortaElClipPrincipalYSuAudio() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        At(session, 4);

        Assert.True(session.SplitAtPlayhead());

        Assert.Equal([(S(0), S(4)), (S(4), S(10)), (S(10), S(20))], MainClips(session));
        Assert.Equal(3, session.Sequence.AudioTracks[0].Clips.Count);
        Assert.Equal("Cortar clip", session.UndoDescription);
        session.Undo();
        Assert.Equal(2, session.MainTrack!.Clips.Count);
    });

    [Fact]
    public void Cortar_EnUnBordeOFuera_NoHaceNada() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        int steps = session.History.Count;
        At(session, 10);
        Assert.False(session.SplitAtPlayhead());
        At(session, 25);
        Assert.False(session.SplitAtPlayhead());
        Assert.Equal(steps, session.History.Count);
    });

    [Fact]
    public void Cortar_ConSeleccion_SoloCortaLoSeleccionado() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        string second = session.MainTrack!.Clips[1].Id;
        session.Select([second]);
        At(session, 4);
        Assert.False(session.SplitAtPlayhead());

        At(session, 15);
        Assert.True(session.SplitAtPlayhead());
        Assert.Equal([(S(0), S(10)), (S(10), S(15)), (S(15), S(20))], MainClips(session));
    });

    [Fact]
    public void RecortarInicio_EnLaPrincipalMagnetica_CierraElHuecoYMueveElCursor() => UiThread.Run(async () =>
    {
        (EditorSession session, FakePlayback playback) = NewSession();
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        At(session, 13);

        Assert.True(session.TrimStartToPlayhead());

        Assert.Equal([(S(0), S(10)), (S(10), S(17))], MainClips(session));
        Assert.Equal(S(10), playback.Seeks[^1]);
    });

    [Fact]
    public void RecortarFinal_SinMagnetica_DejaElHueco() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        session.Timeline.MagneticMainTrack = false;
        At(session, 6);

        Assert.True(session.TrimEndToPlayhead());

        Assert.Equal([(S(0), S(6)), (S(10), S(20))], MainClips(session));
    });

    [Fact]
    public void RecortarFinal_EnLaPrincipalMagnetica_CorreLoQueSigue() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        At(session, 6);

        Assert.True(session.TrimEndToPlayhead());

        Assert.Equal([(S(0), S(6)), (S(6), S(16))], MainClips(session));
    });

    [Fact]
    public void Borrar_EnLaPrincipal_CierraElHuecoYLimpiaLaSeleccion() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        session.Select([session.MainTrack!.Clips[0].Id]);

        Assert.True(session.DeleteSelection());

        Assert.Equal([(S(0), S(10))], MainClips(session));
        Assert.Empty(session.SelectedClipIds);
        Assert.Single(session.Sequence.AudioTracks[0].Clips);
        Assert.False(session.DeleteSelection());
    });

    [Fact]
    public void Borrar_SinMagnetica_DejaElHueco() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        session.Timeline.MagneticMainTrack = false;
        session.Select([session.MainTrack!.Clips[0].Id]);

        Assert.True(session.DeleteSelection());

        Assert.Equal([(S(10), S(20))], MainClips(session));
    });

    [Fact]
    public void SeleccionarTodo_TomaTodosLosClips() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        session.SelectAll();
        Assert.Equal(4, session.SelectedClipIds.Count);
    });

    [Fact]
    public void LosAtajosDelArmazon_LleganALaSesion() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        var router = new ShortcutRouter(registry);
        using IDisposable bindings = EditorShortcuts.Bind(router, session);
        At(session, 5);

        Assert.True(router.Dispatch(KeyChord.Parse("S"), false, false));
        Assert.Equal(3, session.MainTrack!.Clips.Count);
        Assert.True(router.Dispatch(KeyChord.Parse("Ctrl+Z"), false, false));
        Assert.Equal(2, session.MainTrack!.Clips.Count);
        Assert.True(router.Dispatch(KeyChord.Parse("Ctrl+Y"), false, false));
        Assert.Equal(3, session.MainTrack!.Clips.Count);
        Assert.False(router.Dispatch(KeyChord.Parse("Supr"), false, false));
        double zoom = session.Timeline.PixelsPerSecond;
        Assert.True(router.Dispatch(KeyChord.Parse("Ctrl++"), false, false));
        Assert.True(session.Timeline.PixelsPerSecond > zoom);
        Assert.True(router.Dispatch(KeyChord.Parse("N"), false, false));
        Assert.False(session.Timeline.Snapping);
        Assert.True(router.Dispatch(KeyChord.Parse("Z"), false, false));
        Assert.Equal(TimelineTool.Zoom, session.Timeline.Tool);
    });

    [Fact]
    public void Zoom_SeQuedaEnSusLimites()
    {
        var timeline = new TimelineState();
        for (int i = 0; i < 100; i++)
        {
            timeline.ZoomIn();
        }

        Assert.Equal(TimelineState.MaxPixelsPerSecond, timeline.PixelsPerSecond);
        timeline.PixelsPerSecond = double.NaN;
        Assert.Equal(TimelineState.DefaultPixelsPerSecond, timeline.PixelsPerSecond);
        timeline.PixelsPerSecond = 0;
        Assert.Equal(TimelineState.MinPixelsPerSecond, timeline.PixelsPerSecond);
    }
}
