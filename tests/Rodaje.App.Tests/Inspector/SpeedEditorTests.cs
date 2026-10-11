using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public class SpeedEditorTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Registra lo que pide la pestaña y lo ejecuta con la versión provisional (congelar no hace nada).</summary>
    private sealed class RecordingCommands : ISpeedCommands
    {
        private readonly ISpeedCommands _inner = SpeedCommands.Default;

        public List<string> Calls { get; } = [];

        public ICommand SetSpeed(string sequenceId, string clipId, Rational rate)
        {
            Calls.Add($"speed {clipId} {rate}");
            return _inner.SetSpeed(sequenceId, clipId, rate);
        }

        public ICommand RateStretch(string sequenceId, string clipId, Time duration)
        {
            Calls.Add($"stretch {clipId} {duration.ToSeconds()}");
            return _inner.RateStretch(sequenceId, clipId, duration);
        }

        public ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch)
        {
            Calls.Add($"pitch {clipId} {preservePitch}");
            return _inner.SetPreservePitch(sequenceId, clipId, preservePitch);
        }

        public ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration)
        {
            Calls.Add($"freeze {clipId} {at.ToSeconds()} {duration.ToSeconds()}");
            return new SequenceEditCommand("Congelar cuadro", sequenceId, (EditContext _) => { });
        }
    }

    private sealed class Harness
    {
        private Harness(EditorSession session)
        {
            Session = session;
            Editor = new SpeedEditor(session, Commands);
        }

        public EditorSession Session { get; }

        public RecordingCommands Commands { get; } = new();

        public SpeedEditor Editor { get; }

        public Clip Video => Session.MainTrack!.Clips[0];

        public Clip Second => Session.MainTrack!.Clips[1];

        public Clip Audio => Session.Sequence.AudioTracks[0].Clips[0];

        public Clip SecondAudio => Session.Sequence.AudioTracks[0].Clips[1];

        public static async Task<Harness> TwoClips()
        {
            var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
            await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
            var harness = new Harness(session);
            session.Select([harness.Video.Id]);
            harness.At(2);
            return harness;
        }

        public void At(double seconds) =>
            Session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds), Duration = Session.Sequence.Duration });
    }

    [Fact]
    public void Lee_ElClipALaVelocidadNormal() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        SpeedState state = h.Editor.Read()!;
        Assert.Equal(h.Video.Id, state.ClipId);
        Assert.Equal(SpeedState.Normal, state.Rate);
        Assert.Equal((S(10), S(10)), (state.SourceDuration, state.Duration));
        Assert.True(state.PreservePitch);
        Assert.True(state.HasAudio);
        Assert.True(state.IsVideo);
        Assert.False(state.IsFrozen || state.IsReversed || state.HasCurve);

        h.Session.Select([h.Audio.Id]);
        Assert.Equal(h.Audio.Id, h.Editor.Read()!.ClipId);
        Assert.False(h.Editor.Read()!.IsVideo);
        h.Session.Select([h.Audio.Id, h.Second.Id]);
        Assert.Equal(h.Second.Id, h.Editor.Read()!.ClipId);
        h.Session.ClearSelection();
        Assert.Null(h.Editor.Read());
    });

    [Fact]
    public void Velocidad_CambiaLaDuracionDelGrupoYCorreLoDeDetras_EnUnPaso() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        int steps = h.Session.History.Count;
        Assert.True(h.Editor.SetRate(new Rational(2, 1)));

        Assert.Equal(S(5), h.Video.Duration);
        Assert.Equal(S(5), h.Audio.Duration);
        Assert.Equal((S(5), S(5)), (h.Second.Start, h.SecondAudio.Start));
        Assert.Equal(new Rational(2, 1), h.Editor.Read()!.Rate);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.False(h.Editor.SetRate(new Rational(2, 1)));

        Assert.True(h.Editor.SetRate(new Rational(1, 2)));
        Assert.Equal((S(20), S(20)), (h.Second.Start, h.SecondAudio.Start));
        Assert.Equal(steps + 2, h.Session.History.Count);

        h.Session.Undo();
        h.Session.Undo();
        Assert.Null(h.Video.Speed);
        Assert.Equal(S(10), h.Second.Start);
    });

    [Fact]
    public void Arrastre_EsUnGestoDeUnPaso_YEscLoRevierte() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        int steps = h.Session.History.Count;
        h.Editor.BeginChange();
        Assert.True(h.Editor.IsChanging);
        foreach (double position in new[] { 400.0, 450, 500, 550 })
        {
            Assert.True(h.Editor.Change(SpeedMath.FromSlider(position)));
        }

        h.Editor.EndChange();
        Assert.False(h.Editor.IsChanging);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal(SpeedMath.FromSlider(550), h.Editor.Read()!.Rate);
        Assert.Equal("Cambiar velocidad", h.Session.UndoDescription);

        h.Editor.BeginChange();
        Assert.True(h.Editor.Change(new Rational(4, 1)));
        h.Session.CancelGesture();
        Assert.False(h.Editor.Change(new Rational(8, 1)));
        h.Editor.EndChange();
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal(SpeedMath.FromSlider(550), h.Editor.Read()!.Rate);
    });

    [Fact]
    public void Arrastre_QueFalla_NoInsiste() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Session.Execute(Edits.SetTrackState(h.Session.SequenceId, h.Session.Sequence.AudioTracks[0].Id, locked: true)));
        int steps = h.Session.History.Count;
        h.Editor.BeginChange();
        Assert.False(h.Editor.Change(new Rational(2, 1)));
        Assert.False(h.Editor.Change(new Rational(3, 1)));
        h.Editor.EndChange();
        Assert.Single(h.Commands.Calls);
        Assert.Equal(steps, h.Session.History.Count);
        Assert.Null(h.Video.Speed);
    });

    [Fact]
    public void Duracion_EscritaDaLaVelocidad() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.SetDuration(S(4)));
        Assert.Equal(new Rational(5, 2), h.Editor.Read()!.Rate);
        Assert.Equal(S(4), h.Video.Duration);
        Assert.Equal(S(4), h.Second.Start);
        Assert.False(h.Editor.SetDuration(S(4)));
        Assert.False(h.Editor.SetDuration(Time.Zero));

        Assert.True(h.Editor.SetDuration(S(3.3)));
        Assert.Equal(S(3.3), h.Video.Duration);
        Assert.Equal("3,03×", SpeedMath.FormatRate(h.Editor.Read()!.Rate));
    });

    [Fact]
    public void MantenerTono_VaConLosEnlazados_YA1xSinCambiosNoQuedaVelocidad() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.SetPreservePitch(false));
        Assert.False(h.Video.Speed!.PreservePitch);
        Assert.False(h.Audio.Speed!.PreservePitch);
        Assert.Equal(S(10), h.Video.Duration);
        Assert.False(h.Editor.SetPreservePitch(false));

        Assert.True(h.Editor.SetPreservePitch(true));
        Assert.Null(h.Video.Speed);
        Assert.Null(h.Audio.Speed);
    });

    [Fact]
    public void Congelar_PideElCuadroDelCursorTresSegundos_SoloEnVideoYDentro() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.CanFreeze(h.Video));
        Assert.True(h.Editor.Freeze());
        Assert.Equal($"freeze {h.Video.Id} 2 3", Assert.Single(h.Commands.Calls));

        h.At(12);
        Assert.False(h.Editor.CanFreeze(h.Video));
        Assert.False(h.Editor.Freeze());

        h.At(2);
        h.Session.Select([h.Audio.Id]);
        Assert.False(h.Editor.CanFreeze(h.Audio));
        Assert.False(h.Editor.Freeze());
        Assert.Single(h.Commands.Calls);
    });

    [Fact]
    public void Congelado_YCurva_SeLeenDelModelo() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        var frozen = new Clip { Id = "f", Start = S(0), SourceIn = S(1), SourceOut = S(1) + Time.FromTicks(1), Speed = new ClipSpeed { Freeze = true }, Duration = S(3) };
        SpeedState state = h.Editor.Read(frozen);
        Assert.True(state.IsFrozen);
        Assert.False(state.CanChangeRate);

        var curved = new Clip
        {
            Id = "c",
            SourceIn = S(0),
            SourceOut = S(6),
            Duration = S(3),
            Speed = new ClipSpeed { Curve = new SpeedCurve { Span = S(3), Points = [new(0, 200), new(10_000, 200)] } },
        };
        state = h.Editor.Read(curved);
        Assert.True(state.HasCurve);
        Assert.Equal(new Rational(2, 1), state.Rate);
    });

    [Fact]
    public void Provisional_RechazaLoQueNoCabeYCongelar() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        ISpeedCommands commands = SpeedCommands.Default;
        Assert.False(h.Session.Execute(commands.FreezeFrame(h.Session.SequenceId, h.Video.Id, S(2), S(3))));
        Assert.False(h.Session.Execute(commands.SetSpeed(h.Session.SequenceId, "no-existe", new Rational(2, 1))));
        Assert.Null(h.Video.Speed);
    });
}
