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

    /// <summary>Registra lo que pide la pestaña y lo ejecuta con las ediciones del núcleo (congelar no hace nada).</summary>
    private sealed class RecordingCommands : ISpeedCommands
    {
        private readonly ISpeedCommands _inner = SpeedCommands.Default;

        public List<string> Calls { get; } = [];

        public ICommand SetSpeed(string sequenceId, string clipId, Rational rate, RippleScope scope)
        {
            Calls.Add($"speed {clipId} {rate}");
            return _inner.SetSpeed(sequenceId, clipId, rate, scope);
        }

        public ICommand SetSpeed(string sequenceId, IReadOnlyList<string> clipIds, Rational rate, RippleScope scope)
        {
            Calls.Add($"speed-many {string.Join(',', clipIds)} {rate}");
            return _inner.SetSpeed(sequenceId, clipIds, rate, scope);
        }

        public ICommand RateStretch(string sequenceId, string clipId, Time duration, RippleScope scope)
        {
            Calls.Add($"stretch {clipId} {duration.ToSeconds()}");
            return _inner.RateStretch(sequenceId, clipId, duration, scope);
        }

        public ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch)
        {
            Calls.Add($"pitch {clipId} {preservePitch}");
            return _inner.SetPreservePitch(sequenceId, clipId, preservePitch);
        }

        public ICommand SetReverse(string sequenceId, string clipId, bool reverse)
        {
            Calls.Add($"reverse {clipId} {reverse}");
            return _inner.SetReverse(sequenceId, clipId, reverse);
        }

        public ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration, RippleScope scope)
        {
            Calls.Add($"freeze {clipId} {at.ToSeconds()} {duration.ToSeconds()}");
            return new SequenceEditCommand("Congelar fotograma", sequenceId, (EditContext _) => { });
        }

        public ICommand SetCurve(string sequenceId, string clipId, SpeedCurve? curve, RippleScope scope)
        {
            Calls.Add($"curve {clipId} {(curve is null ? "null" : curve.Points.Count)}");
            return _inner.SetCurve(sequenceId, clipId, curve, scope);
        }

        public ICommand ApplySpeedPreset(string sequenceId, string clipId, SpeedPreset preset, RippleScope scope)
        {
            Calls.Add($"preset {clipId} {preset}");
            return _inner.ApplySpeedPreset(sequenceId, clipId, preset, scope);
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

        public static async Task<Harness> TwoClips(double first = 10)
        {
            var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", first).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
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
    public void MantenerTono_VaConLosEnlazados_YA1xNoSeToca() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.False(h.Editor.Read()!.CanChangePitch);
        Assert.False(h.Editor.SetPreservePitch(false));
        Assert.Null(h.Video.Speed);

        Assert.True(h.Editor.SetRate(new Rational(2, 1)));
        Assert.True(h.Editor.Read()!.CanChangePitch);
        Assert.True(h.Editor.SetPreservePitch(false));
        Assert.False(h.Video.Speed!.PreservePitch);
        Assert.False(h.Audio.Speed!.PreservePitch);
        Assert.Equal(S(5), h.Video.Duration);
        Assert.False(h.Editor.SetPreservePitch(false));

        Assert.True(h.Editor.SetPreservePitch(true));
        Assert.True(h.Video.Speed!.PreservePitch);
        Assert.True(h.Audio.Speed!.PreservePitch);
    });

    [Fact]
    public void Invertir_EsUnPasoDelHistorial_ConLosEnlazados_YSinCambiarLaDuracion() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.Read()!.CanReverse);
        Assert.False(h.Editor.SetReverse(false));
        Assert.Empty(h.Commands.Calls);

        Assert.True(h.Editor.SetReverse(true));
        Assert.Equal($"reverse {h.Video.Id} True", Assert.Single(h.Commands.Calls));
        Assert.True(h.Editor.Read()!.IsReversed);
        Assert.True(h.Audio.Speed!.Reverse);
        Assert.Equal((S(10), S(10)), (h.Video.Duration, h.Second.Start));
        Assert.False(h.Editor.SetReverse(true));

        h.Session.Undo();
        Assert.False(h.Editor.Read()!.IsReversed);
        Assert.Null(h.Audio.Speed);

        h.Session.Redo();
        Assert.True(h.Editor.Read()!.IsReversed);
        Assert.True(h.Editor.SetReverse(false));
        Assert.Null(h.Video.Speed);
    });

    [Fact]
    public void Invertir_Con_MantenerTono_AlUnoPorNoCambiaElTono() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.SetReverse(true));
        Assert.False(h.Editor.Read()!.CanChangePitch);
        Assert.False(h.Editor.SetPreservePitch(false));

        Assert.True(h.Editor.SetRate(new Rational(2, 1)));
        SpeedState state = h.Editor.Read()!;
        Assert.True(state.IsReversed);
        Assert.True(state.CanChangePitch);
        Assert.True(h.Editor.SetPreservePitch(false));
        Assert.True(h.Video.Speed is { Reverse: true, PreservePitch: false });
    });

    [Fact]
    public void Invertir_NoSePuedeEnUnCongelado() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Session.FreezeFrameAtPlayhead());
        SpeedState state = h.Editor.Read()!;
        Assert.True(state.IsFrozen);
        Assert.False(state.CanReverse);
        Assert.False(state.IsReversed);
        Assert.False(h.Editor.SetReverse(true));
        Assert.DoesNotContain(h.Commands.Calls, c => c.StartsWith("reverse", StringComparison.Ordinal));
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
    public void Nucleo_RechazaUnClipQueNoEsta() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        ISpeedCommands commands = SpeedCommands.Default;
        Assert.False(h.Session.Execute(commands.SetSpeed(h.Session.SequenceId, "no-existe", new Rational(2, 1), RippleScope.Track)));
        Assert.False(h.Session.Execute(commands.RateStretch(h.Session.SequenceId, "no-existe", S(3), RippleScope.Track)));
        Assert.Null(h.Video.Speed);
    });

    [Fact]
    public void Restablecer_VuelveA1xEnUnPaso_SinPreguntarSiNoHayCurva() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.False(h.Editor.Read()!.CanReset);
        Assert.False(await h.Editor.ResetAsync(() => throw new InvalidOperationException("No debe preguntar.")));

        Assert.True(h.Editor.SetRate(new Rational(2, 1)));
        Assert.True(h.Editor.SetPreservePitch(false));
        Assert.True(h.Editor.Read()!.CanReset);
        int steps = h.Session.History.Count;
        Assert.True(await h.Editor.ResetAsync(() => throw new InvalidOperationException("No debe preguntar.")));

        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal(SpeedState.Normal, h.Editor.Read()!.Rate);
        // A 1× el clip queda sin velocidad, así que «Mantener tono» vuelve a su valor por omisión.
        Assert.True(h.Editor.Read()!.PreservePitch);
        Assert.Equal((S(10), S(10)), (h.Video.Duration, h.Second.Start));
        Assert.False(h.Editor.Read()!.CanReset);

        h.Session.Undo();
        Assert.Equal(new Rational(2, 1), h.Editor.Read()!.Rate);
    });

    [Fact]
    public void Restablecer_ConCurva_PreguntaAntes_YCancelarNoCambiaNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        string clipId = h.Video.Id;
        Assert.True(h.Session.Execute(new SequenceEditCommand("Curva", h.Session.SequenceId, (EditContext ctx) =>
        {
            Clip clip = ctx.Sequence.GetClip(clipId).Clip;
            clip.Speed = new ClipSpeed { Curve = new SpeedCurve { Span = S(5), Points = [new(0, 200), new(10_000, 200)] } };
            clip.Duration = S(5);
        })));
        Assert.True(h.Editor.Read()!.HasCurve);
        Assert.True(h.Editor.Read()!.CanReset);
        int steps = h.Session.History.Count;

        int asked = 0;
        Assert.False(await h.Editor.ResetAsync(() => { asked++; return Task.FromResult(false); }));
        Assert.Equal(1, asked);
        Assert.Equal(steps, h.Session.History.Count);
        Assert.NotNull(h.Video.Speed!.Curve);

        Assert.True(await h.Editor.ResetAsync(() => { asked++; return Task.FromResult(true); }));
        Assert.Equal(2, asked);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Null(h.Video.Speed);
        Assert.Equal(S(10), h.Video.Duration);
    });

    [Fact]
    public void Restablecer_SiElClipDesaparecioDuranteElAviso_NoHaceNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        string clipId = h.Video.Id;
        Assert.True(h.Session.Execute(new SequenceEditCommand("Curva", h.Session.SequenceId, (EditContext ctx) =>
        {
            Clip clip = ctx.Sequence.GetClip(clipId).Clip;
            clip.Speed = new ClipSpeed { Curve = new SpeedCurve { Span = S(5), Points = [new(0, 200), new(10_000, 200)] } };
            clip.Duration = S(5);
        })));
        int steps = h.Session.History.Count;
        Assert.False(await h.Editor.ResetAsync(() =>
        {
            h.Session.Undo();
            return Task.FromResult(true);
        }));
        Assert.Equal(steps - 1, h.Session.History.Count);
    });

    private static string Layout(Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    [Fact]
    public void Nucleo_CongelarConElCursorAMitadDelClip_CortaMeteTresSegundosYSeDeshaceDeUnaVez() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        var editor = new SpeedEditor(h.Session, SpeedCommands.Default);
        h.At(4);
        int steps = h.Session.History.Count;

        Assert.True(editor.Freeze());
        Track main = h.Session.MainTrack!;
        Track audio = h.Session.Sequence.AudioTracks[0];
        Assert.Equal("0-4 4-7 7-13 13-23", Layout(main));
        Assert.Equal("0-4 7-13 13-23", Layout(audio));
        Clip frozen = main.Clips[1];
        Assert.True(frozen.Speed!.Freeze);
        Assert.Equal(S(4), frozen.SourceIn);
        Assert.Equal([frozen.Id], h.Session.SelectedClipIds);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.False(editor.Read()!.CanChangeRate);

        Assert.True(h.Session.Undo());
        Assert.Equal("0-10 10-20", Layout(main));
        Assert.Equal("0-10 10-20", Layout(audio));
    });

    [Fact]
    public void Nucleo_DuracionEscrita33s_DejaElClipJustoEnEsoYSeDeshaceDeUnaVez() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        var editor = new SpeedEditor(h.Session, SpeedCommands.Default);
        int steps = h.Session.History.Count;

        Assert.True(editor.SetDuration(SpeedMath.ParseDuration("3,3 s")!.Value));
        Assert.Equal(S(3.3), h.Video.Duration);
        Assert.Equal(S(3.3), h.Audio.Duration);
        Assert.Equal((S(0), S(10)), (h.Video.SourceIn, h.Video.SourceOut));
        Assert.Equal((S(3.3), S(3.3)), (h.Second.Start, h.SecondAudio.Start));
        Assert.Equal("3,3 s", SpeedMath.FormatDuration(editor.Read()!.Duration));
        Assert.Equal(steps + 1, h.Session.History.Count);

        Assert.True(h.Session.Undo());
        Assert.Null(h.Video.Speed);
        Assert.Equal((S(10), S(10)), (h.Video.Duration, h.Second.Start));
    });

    [Fact]
    public void Arrastre_CadaPasoParteDelInicio_SoltarDaElMismoMaterialQueUnSoloCambio() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips(first: 10.37);
        Rational final = SpeedMath.FromSlider(530);
        Assert.True(h.Editor.SetRate(final));
        var expected = (h.Video.SourceIn, h.Video.SourceOut, h.Video.Duration, h.Audio.SourceOut, h.Audio.Duration, h.Second.Start);
        Assert.True(h.Session.Undo());
        int steps = h.Session.History.Count;

        h.Editor.BeginChange();
        for (int i = 0; i <= 42; i++)
        {
            Assert.True(h.Editor.Change(SpeedMath.FromSlider(i * SpeedMath.SliderMax / 42)));
        }

        Assert.Equal(new Rational(100, 1), h.Video.Speed!.Rate);
        Assert.True(h.Editor.Change(final));
        h.Editor.EndChange();

        Assert.Equal(expected, (h.Video.SourceIn, h.Video.SourceOut, h.Video.Duration, h.Audio.SourceOut, h.Audio.Duration, h.Second.Start));
        Assert.Equal(steps + 1, h.Session.History.Count);

        // Ir y volver a la velocidad de partida deja el clip exactamente como estaba.
        h.Editor.BeginChange();
        Assert.True(h.Editor.Change(new Rational(7, 1)));
        Assert.True(h.Editor.Change(final));
        h.Editor.EndChange();
        Assert.Equal(expected, (h.Video.SourceIn, h.Video.SourceOut, h.Video.Duration, h.Audio.SourceOut, h.Audio.Duration, h.Second.Start));

        Assert.True(h.Session.Undo());
        Assert.True(h.Session.Undo());
        Assert.Null(h.Video.Speed);
        Assert.Equal((S(0), S(10.37)), (h.Video.SourceIn, h.Video.SourceOut));
    });
}
