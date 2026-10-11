using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>Velocidad desde el teclado: Ctrl+R, Mayús+R, Ctrl+Alt+. y Ctrl+Alt+, e «Invertir».</summary>
public class SpeedShortcutTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10) y b [10,20), con su audio enlazado.</summary>
    private static async Task<EditorSession> TwoClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        return session;
    }

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds) });

    private static string Layout(Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    [Theory]
    [InlineData(ShortcutPreset.Rodaje)]
    [InlineData(ShortcutPreset.Agile)]
    [InlineData(ShortcutPreset.Classic)]
    public void LasTeclasSonLasMismasEnLosTresEstilosYRSigueSiendoRegion(ShortcutPreset preset)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = preset;

        Assert.Equal(ActionIds.ClipSpeed, registry.FindAction(KeyChord.Parse("Ctrl+R")));
        Assert.Equal(ActionIds.FreezeFrame, registry.FindAction(KeyChord.Parse("Mayús+R")));
        Assert.Equal(ActionIds.SpeedUp, registry.FindAction(KeyChord.Parse("Ctrl+Alt+.")));
        Assert.Equal(ActionIds.SlowDown, registry.FindAction(KeyChord.Parse("Ctrl+Alt+,")));
        Assert.Empty(registry.GetKeys(ActionIds.Reverse));
        Assert.NotEqual(ActionIds.FreezeFrame, registry.FindAction(KeyChord.Parse("R")));
        Assert.Empty(registry.FindConflicts());
        Assert.Contains(CheatSheet.Select(registry), e => e.ActionId == ActionIds.ClipSpeed);
        Assert.Contains(CheatSheet.Select(registry), e => e.ActionId == ActionIds.FreezeFrame);
    }

    [Fact]
    public void CtrlR_SeleccionaElClipDelCursorYPideLaPestana() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        int requests = 0;
        session.SpeedPanelRequested += (_, _) => requests++;
        At(session, 12);

        Assert.True(session.ShowClipSpeed());
        Assert.Equal(1, requests);
        Assert.Equal([session.MainTrack!.Clips[1].Id], session.SelectedClipIds);
    });

    [Fact]
    public void MasRapidoYMasLento_SubenYBajanEscalonesConRipple() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        At(session, 3);

        Assert.True(session.StepSpeed(faster: true));
        Assert.Equal("0-8 8-18", Layout(session.MainTrack!));
        Assert.Equal("0-8 8-18", Layout(session.Sequence.AudioTracks[0]));
        Assert.Equal("Más rápido", session.UndoDescription);

        Assert.True(session.StepSpeed(faster: false));
        Assert.True(session.StepSpeed(faster: false));
        Assert.Equal(new Rational(3, 4), session.MainTrack!.Clips[0].Speed!.Rate);
        Assert.Equal("0-13.33 13.33-23.33", Layout(session.MainTrack!));

        Assert.True(session.Undo());
        Assert.Null(session.MainTrack!.Clips[0].Speed);
        Assert.Equal("0-10 10-20", Layout(session.MainTrack!));
    });

    [Fact]
    public void LosEscalonesTerminanEnLosExtremos()
    {
        Assert.Equal(new Rational(5, 4), EditorSession.NextStep(new Rational(1, 1), faster: true));
        Assert.Equal(new Rational(3, 4), EditorSession.NextStep(new Rational(1, 1), faster: false));
        Assert.Equal(new Rational(2, 1), EditorSession.NextStep(new Rational(17, 10), faster: true));
        Assert.Null(EditorSession.NextStep(new Rational(100, 1), faster: true));
        Assert.Null(EditorSession.NextStep(new Rational(1, 10), faster: false));
    }

    [Fact]
    public void CongelarCuadro_MeteTresSegundosEnElCursorYLoSelecciona() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        int steps = session.History.Count;
        At(session, 12);

        Assert.True(session.FreezeFrameAtPlayhead());
        Assert.Equal("0-10 10-12 12-15 15-23", Layout(session.MainTrack!));
        Assert.Equal("0-10 10-12 15-23", Layout(session.Sequence.AudioTracks[0]));
        Clip frozen = session.MainTrack!.Clips[2];
        Assert.Equal([frozen.Id], session.SelectedClipIds);
        Assert.True(frozen.Speed!.Freeze);
        Assert.Equal(S(2), frozen.SourceIn);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal("Congelar cuadro", session.UndoDescription);

        Assert.False(session.FreezeFrameAtPlayhead());
        Assert.False(session.StepSpeed(faster: true));
    });

    [Fact]
    public void Invertir_VaYVuelveConElEnlazado() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        At(session, 3);

        Assert.True(session.ToggleReverse());
        Assert.True(session.MainTrack!.Clips[0].Speed!.Reverse);
        Assert.True(session.Sequence.AudioTracks[0].Clips[0].Speed!.Reverse);
        Assert.Equal("0-10 10-20", Layout(session.MainTrack!));
        Assert.Equal("Invertir", session.UndoDescription);

        Assert.True(session.ToggleReverse());
        Assert.Null(session.MainTrack!.Clips[0].Speed);
    });
}
