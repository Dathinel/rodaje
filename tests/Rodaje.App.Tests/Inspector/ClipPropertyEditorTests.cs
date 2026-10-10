using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Inspector;

public class ClipPropertyEditorTests
{
    private const string Transform = BuiltInEffects.Transform;

    private sealed class Harness
    {
        private Harness(EditorSession session, FakePlayback playback)
        {
            Session = session;
            Playback = playback;
            Editor = new ClipPropertyEditor(session);
        }

        public EditorSession Session { get; }

        public FakePlayback Playback { get; }

        public ClipPropertyEditor Editor { get; }

        public Clip Video => Session.MainTrack!.Clips[0];

        public static async Task<Harness> WithClip()
        {
            var playback = new FakePlayback();
            var session = new EditorSession(playback, new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
            await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
            var harness = new Harness(session, playback);
            session.Select([harness.Video.Id]);
            harness.At(2);
            return harness;
        }

        public void At(double seconds) =>
            Session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(seconds), Duration = Session.Sequence.Duration });

        public ParameterState Read(string name) => Editor.Read(Video, Transform, name, P.DefaultValue(name));

        public Parameter? Parameter(string name) =>
            ClipPropertyEditor.FindEffect(Video, Transform) is { } effect && effect.Parameters.TryGetValue(name, out Parameter? p) ? p : null;

        public void Drag(string name, params double[] values)
        {
            Editor.BeginChange(Video, Transform, name, "Cambiar " + name);
            foreach (double value in values)
            {
                Assert.True(Editor.Change(value));
            }

            Editor.EndChange();
        }
    }

    private static Time S(double seconds) => Time.FromSeconds(seconds);

    [Fact]
    public void Objetivos_VideoSeleccionadoYSuAudioEnlazado() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        Assert.Same(h.Video, h.Editor.VideoTarget());
        Clip audio = h.Session.Sequence.AudioTracks[0].Clips[0];
        Assert.Same(audio, h.Editor.AudioTarget());

        h.Session.Select([audio.Id]);
        Assert.Null(h.Editor.VideoTarget());
        Assert.Same(audio, h.Editor.AudioTarget());
        h.Session.ClearSelection();
        Assert.Null(h.Editor.AudioTarget());
    });

    [Fact]
    public void SinEfecto_SeLeenLosValoresPorDefecto() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        ParameterState scale = h.Read(P.Scale);
        Assert.Equal(1, scale.Value);
        Assert.False(scale.IsAnimated);
        Assert.Null(scale.KeyframeHere);
    });

    [Fact]
    public void Arrastre_SinTransformacion_EsUnPasoYRecargaElReproductorEnCadaValor() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        int steps = h.Session.History.Count;
        int reloads = h.Playback.Reloads.Count;

        h.Drag(P.Scale, 1.1, 1.2, 1.3, 1.4);

        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal("Cambiar scale", h.Session.UndoDescription);
        Assert.Equal(reloads + 5, h.Playback.Reloads.Count);
        Assert.Equal(1.4, h.Parameter(P.Scale)!.Value);
        Clip reloaded = h.Playback.Reloads[^1].GetSequence(h.Session.SequenceId).VideoTracks[0].Clips[0];
        Assert.Equal(1.4, reloaded.Effects[0].Parameters[P.Scale].Value);

        h.Session.Undo();
        Assert.Empty(h.Video.Effects);
    });

    [Fact]
    public void Arrastre_ConTransformacion_SeFundeEnUnPaso_YElSiguienteEsOtro() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Drag(P.X, 10);
        int steps = h.Session.History.Count;

        h.Drag(P.X, 20, 30, 40);
        Assert.Equal(steps + 1, h.Session.History.Count);
        h.Drag(P.X, 50);
        Assert.Equal(steps + 2, h.Session.History.Count);

        h.Session.Undo();
        Assert.Equal(40, h.Parameter(P.X)!.Value);
        h.Session.Undo();
        Assert.Equal(10, h.Parameter(P.X)!.Value);
    });

    [Fact]
    public void Rombo_PoneYQuitaElKeyframeEnElCursor() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        Assert.True(h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1));
        Assert.Equal([new Keyframe(S(2), 1)], h.Parameter(P.Opacity)!.Keyframes);
        Assert.Equal("Añadir keyframe", h.Session.UndoDescription);
        Assert.NotNull(h.Read(P.Opacity).KeyframeHere);

        Assert.True(h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1));
        Assert.Empty(h.Parameter(P.Opacity)!.Keyframes);
        Assert.Equal(1, h.Parameter(P.Opacity)!.Value);
    });

    [Fact]
    public void ConKeyframes_CambiarEnOtroInstante_AgregaUnoSolo() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Editor.ToggleKeyframe(h.Video, Transform, P.X, 0);
        h.At(5);
        Assert.Equal(0, h.Read(P.X).Value);
        Assert.Null(h.Read(P.X).KeyframeHere);

        h.Drag(P.X, 100, 200, 300);

        Assert.Equal([new Keyframe(S(2), 0), new Keyframe(S(5), 300)], h.Parameter(P.X)!.Keyframes);
        h.At(3.5);
        Assert.Equal(150, h.Read(P.X).Value, 6);
        Assert.Equal(2, h.Read(P.X).KeyframeCount);
    });

    [Fact]
    public void ConKeyframes_DentroDelMismoCuadro_AjustaElExistente() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Rotation, 0);
        h.Session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(2) + Time.FromTicks(1000), Duration = h.Session.Sequence.Duration });

        Assert.NotNull(h.Read(P.Rotation).KeyframeHere);
        h.Drag(P.Rotation, 45);
        Assert.Equal([new Keyframe(S(2), 45)], h.Parameter(P.Rotation)!.Keyframes);
    });

    [Fact]
    public void ArrastreDuranteLaReproduccion_UsaElInstanteDelPrincipio() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Y, 0);
        h.At(4);
        h.Editor.BeginChange(h.Video, Transform, P.Y, "Mover en Y");
        h.Editor.Change(10);
        h.At(4.5);
        h.Editor.Change(20);
        h.At(5);
        h.Editor.Change(30);
        h.Editor.EndChange();

        Assert.Equal([new Keyframe(S(2), 0), new Keyframe(S(4), 30)], h.Parameter(P.Y)!.Keyframes);
    });

    [Fact]
    public void FueraDelClip_NoSePonenKeyframesNiSeEditaLoAnimado() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Scale, 1);
        h.At(10);
        Assert.False(h.Editor.IsPlayheadInside(h.Video));
        Assert.False(h.Editor.ToggleKeyframe(h.Video, Transform, P.Scale, 1));

        h.Editor.BeginChange(h.Video, Transform, P.Scale, "Cambiar escala");
        Assert.False(h.Editor.Change(2));
        h.Editor.EndChange();
        Assert.Single(h.Parameter(P.Scale)!.Keyframes);

        h.Editor.SetValue(h.Video, Transform, P.X, 25, "Mover en X");
        Assert.Equal(25, h.Parameter(P.X)!.Value);
    });

    [Fact]
    public void Curva_YNavegacionEntreKeyframes() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.At(1);
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1);
        h.At(3);
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1);
        h.At(1);
        Assert.True(h.Editor.SetInterpolation(h.Video, Transform, P.Opacity, Interpolation.EaseInOut));
        Assert.Equal(Interpolation.EaseInOut, h.Parameter(P.Opacity)!.Keyframes[0].Interpolation);
        Assert.False(h.Editor.SetInterpolation(h.Video, Transform, P.Opacity, Interpolation.EaseInOut));

        h.At(2);
        ParameterState state = h.Read(P.Opacity);
        Assert.Equal(S(1), state.Previous);
        Assert.Equal(S(3), state.Next);
        Assert.False(h.Editor.SetInterpolation(h.Video, Transform, P.Opacity, Interpolation.Hold));

        Assert.True(h.Editor.GoToKeyframe(h.Video, Transform, P.Opacity, 1));
        Assert.Equal(S(3), h.Playback.Seeks[^1]);
        h.At(3);
        Assert.False(h.Editor.GoToKeyframe(h.Video, Transform, P.Opacity, 1));
        Assert.True(h.Editor.GoToKeyframe(h.Video, Transform, P.Opacity, -1));
        Assert.Equal(S(1), h.Playback.Seeks[^1]);
    });

    [Fact]
    public void PistaBloqueada_AvisaUnaVezPorArrastre_YNoQuedaNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Session.Execute(Rodaje.Core.Commands.Edits.SetTrackState(h.Session.SequenceId, h.Session.MainTrack!.Id, locked: true));
        int steps = h.Session.History.Count;
        var warnings = new List<EditorNotification>();
        h.Session.Notified += (_, n) => warnings.Add(n);

        h.Editor.BeginChange(h.Video, Transform, P.Scale, "Cambiar escala");
        Assert.False(h.Editor.Change(1.1));
        Assert.False(h.Editor.Change(1.2));
        h.Editor.EndChange();

        Assert.Single(warnings);
        Assert.Equal(steps, h.Session.History.Count);
        Assert.False(h.Session.IsInGesture);
        Assert.Empty(h.Video.Effects);
    });

    [Fact]
    public void EditaLaPrimeraTransformacionActiva() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        string sequence = h.Session.SequenceId;
        h.Session.Execute(Rodaje.Core.Commands.Edits.AddEffect(sequence, h.Video.Id, Transform));
        h.Session.Execute(Rodaje.Core.Commands.Edits.AddEffect(sequence, h.Video.Id, Transform));
        h.Session.Execute(Rodaje.Core.Commands.Edits.SetEffectEnabled(sequence, h.Video.Id, h.Video.Effects[0].Id, false));

        h.Drag(P.X, 77);
        Assert.False(h.Video.Effects[0].Parameters.ContainsKey(P.X));
        Assert.Equal(77, h.Video.Effects[1].Parameters[P.X].Value);
    });
}
