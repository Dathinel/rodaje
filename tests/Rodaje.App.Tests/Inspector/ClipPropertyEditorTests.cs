using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
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

        public static async Task<Harness> WithClip(double seconds = 10, int width = 1920, int height = 1080, Rational? rate = null)
        {
            var playback = new FakePlayback();
            var session = new EditorSession(playback, new FakeProber().Video("a.mp4", seconds, width, height, rate), new FakeDialogs(), new SequentialIdGenerator());
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
    public void Hdr_reference_is_editable_animatable_saved_and_undoable() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        ParameterSpec spec = Assert.Single(ParameterSpec.Transform(1920, 1080), p => p.Name == P.HdrReferenceNits);
        Assert.Equal("nits", spec.Unit);
        Assert.True(spec.Animatable);
        Assert.Equal(0, spec.DefaultValue);
        h.Drag(P.HdrReferenceNits, 100, 400);
        Assert.Equal(400, h.Read(P.HdrReferenceNits).Value);
        Assert.True(h.Editor.ToggleKeyframe(h.Video, Transform, P.HdrReferenceNits, 0));
        Assert.Equal([new Keyframe(S(2), 400)], h.Parameter(P.HdrReferenceNits)!.Keyframes);
        string json = Dump(h);
        Project restored = Rodaje.Core.Serialization.ProjectSerializer.Deserialize(json);
        Clip clip = restored.GetSequence(h.Session.SequenceId).VideoTracks[0].Clips[0];
        Assert.Equal(h.Parameter(P.HdrReferenceNits)!.Keyframes, clip.Effects[0].Parameters[P.HdrReferenceNits].Keyframes);
        h.Session.Undo();
        h.Session.Undo();
        Assert.Equal(0, h.Read(P.HdrReferenceNits).Value);
    });

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
    public void Esc_EnElPrimerArrastre_RevierteSinRomperAlSoltar() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        int steps = h.Session.History.Count;
        h.Editor.BeginChange(h.Video, Transform, P.Scale, "Cambiar escala");
        Assert.True(h.Editor.Change(2));

        h.Session.CancelGesture();
        Assert.False(h.Editor.Change(3));
        h.Editor.EndChange();

        Assert.Null(ClipPropertyEditor.FindEffect(h.Video, Transform));
        Assert.Equal(steps, h.Session.History.Count);
        Assert.False(h.Session.IsInGesture);

        h.Drag(P.Scale, 1.5);
        Assert.Equal(1.5, h.Parameter(P.Scale)!.Value);
        Assert.Equal(steps + 1, h.Session.History.Count);
    });

    [Fact]
    public void Escala_ElSegundoKeyframeEscritoEnElCuadroSeMantieneHastaElFinal() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip(6, 640, 360, new Rational(25, 1));
        h.At(0);
        Assert.True(h.Editor.ToggleKeyframe(h.Video, Transform, P.Scale, 1));
        h.At(3.056);
        Assert.True(h.Editor.SetValue([h.Video], Transform, P.Scale, 0.5, "Cambiar escala"));

        Assert.Equal(2, h.Parameter(P.Scale)!.Keyframes.Count);
        Assert.Equal(0.5, h.Parameter(P.Scale)!.Keyframes[1].Value);
        foreach (double seconds in new[] { 3.056, 4.278, 5.5 })
        {
            h.At(seconds);
            Assert.Equal(0.5, h.Read(P.Scale).Value, 6);
        }

        h.At(2);
        Assert.Equal(1 - (0.5 * 2 / 3.056), h.Read(P.Scale).Value, 2);
        Assert.Equal(1, h.Read(P.ScaleY).Value);
        Assert.Null(h.Parameter(P.ScaleY));
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

    private static (string, double)[] All() =>
        [(P.X, 0), (P.Y, 0), (P.Scale, 1), (P.Rotation, 0), (P.Opacity, 1)];

    private static Func<int, Task<bool>> Answer(bool accept, List<int> asked) => n =>
    {
        asked.Add(n);
        return Task.FromResult(accept);
    };

    private static string Dump(Harness h) => Rodaje.Core.Serialization.ProjectSerializer.Serialize(h.Session.Project);

    [Fact]
    public void Restablecer_SinKeyframes_NoPreguntaYEsUnPaso() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        var asked = new List<int>();
        Assert.False(ClipPropertyEditor.CanReset(h.Video, Transform, [(P.Scale, 1)]));
        Assert.False(await h.Editor.ResetAsync(h.Video, Transform, [(P.Scale, 1)], "Restablecer escala", Answer(true, asked)));

        h.Drag(P.Scale, 1.5);
        h.Drag(P.X, 40);
        Assert.False(h.Read(P.Scale).IsDefault(1));
        int steps = h.Session.History.Count;

        Assert.True(await h.Editor.ResetAsync(h.Video, Transform, [(P.Scale, 1)], "Restablecer escala", Answer(true, asked)));
        Assert.Empty(asked);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal("Restablecer escala", h.Session.UndoDescription);
        Assert.True(h.Read(P.Scale).IsDefault(1));
        Assert.Equal(40, h.Parameter(P.X)!.Value);
        Assert.False(ClipPropertyEditor.CanReset(h.Video, Transform, [(P.Scale, 1)]));

        h.Session.Undo();
        Assert.Equal(1.5, h.Parameter(P.Scale)!.Value);
    });

    [Fact]
    public void Restablecer_ConKeyframes_PreguntaYCancelarNoCambiaNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1);
        h.At(4);
        h.Drag(P.Opacity, 0.2);
        string before = Dump(h);
        int steps = h.Session.History.Count;
        var asked = new List<int>();

        Assert.False(await h.Editor.ResetAsync(h.Video, Transform, [(P.Opacity, 1)], "Restablecer opacidad", Answer(false, asked)));
        Assert.Equal([2], asked);
        Assert.Equal(before, Dump(h));
        Assert.Equal(steps, h.Session.History.Count);

        Assert.True(await h.Editor.ResetAsync(h.Video, Transform, [(P.Opacity, 1)], "Restablecer opacidad", Answer(true, asked)));
        Assert.Empty(h.Parameter(P.Opacity)!.Keyframes);
        Assert.True(h.Read(P.Opacity).IsDefault(1));
        Assert.Equal(steps + 1, h.Session.History.Count);

        h.Session.Undo();
        Assert.Equal(before, Dump(h));
    });

    [Fact]
    public void RestablecerTodo_AvisaConElTotalYSeDeshaceDeUnaVez() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Drag(P.X, 10);
        h.Drag(P.Rotation, 45);
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Scale, 1);
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1);
        h.At(3);
        h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1);
        string before = Dump(h);
        int steps = h.Session.History.Count;
        var asked = new List<int>();

        Assert.True(await h.Editor.ResetAsync(h.Video, Transform, All(), "Restablecer transformación", Answer(true, asked)));
        Assert.Equal([3], asked);
        Assert.Equal(steps + 1, h.Session.History.Count);
        foreach ((string name, double value) in All())
        {
            Assert.True(h.Read(name).IsDefault(value));
        }

        Assert.False(ClipPropertyEditor.CanReset(h.Video, Transform, All()));
        h.Session.Undo();
        Assert.Equal(before, Dump(h));
    });

    [Fact]
    public void QuitarKeyframes_DejaElValorDelCursor_YCancelarNoCambiaNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        var asked = new List<int>();
        Assert.False(await h.Editor.ClearKeyframesAsync(h.Video, Transform, P.X, "Quitar keyframes de posición X", Answer(true, asked)));
        Assert.Empty(asked);

        h.Editor.ToggleKeyframe(h.Video, Transform, P.X, 0);
        h.At(4);
        h.Drag(P.X, 100);
        h.At(3);
        string before = Dump(h);

        Assert.False(await h.Editor.ClearKeyframesAsync(h.Video, Transform, P.X, "Quitar keyframes de posición X", Answer(false, asked)));
        Assert.Equal(before, Dump(h));

        Assert.True(await h.Editor.ClearKeyframesAsync(h.Video, Transform, P.X, "Quitar keyframes de posición X", Answer(true, asked)));
        Assert.Equal([2, 2], asked);
        Assert.Empty(h.Parameter(P.X)!.Keyframes);
        Assert.Equal(50, h.Parameter(P.X)!.Value, 6);
        Assert.Equal("Quitar keyframes de posición X", h.Session.UndoDescription);
        h.Session.Undo();
        Assert.Equal(before, Dump(h));
    });

    [Fact]
    public void Restablecer_PistaBloqueada_AvisaYNoCambiaNada() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        h.Drag(P.X, 10);
        h.Session.Execute(Rodaje.Core.Commands.Edits.SetTrackState(h.Session.SequenceId, h.Session.MainTrack!.Id, locked: true));
        int steps = h.Session.History.Count;
        var warnings = new List<EditorNotification>();
        h.Session.Notified += (_, n) => warnings.Add(n);

        Assert.False(await h.Editor.ResetAsync(h.Video, Transform, All(), "Restablecer transformación", Answer(true, [])));
        Assert.Single(warnings);
        Assert.Equal(steps, h.Session.History.Count);
        Assert.Equal(10, h.Parameter(P.X)!.Value);
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

    [Fact]
    public void ConId_EditaEseEfectoDeLaPila_YNoCreaOtro() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        string sequence = h.Session.SequenceId;
        const string Color = BuiltInEffects.Color;
        const string Exposure = BuiltInEffects.ColorParameters.Exposure;
        h.Session.Execute(Rodaje.Core.Commands.Edits.AddEffect(sequence, h.Video.Id, Color));
        h.Session.Execute(Rodaje.Core.Commands.Edits.AddEffect(sequence, h.Video.Id, Color));
        string second = h.Video.Effects[1].Id;

        Assert.True(h.Editor.SetValue(h.Video, Color, Exposure, 1.5, "Cambiar exposición", second));
        Assert.False(h.Video.Effects[0].Parameters.ContainsKey(Exposure));
        Assert.Equal(1.5, h.Editor.Read(h.Video, Color, Exposure, 0, second).Value);
        Assert.Equal(0, h.Editor.Read(h.Video, Color, Exposure, 0).Value);

        Assert.True(h.Editor.ToggleKeyframe(h.Video, Color, Exposure, 0, second));
        Assert.Equal(1, ClipPropertyEditor.KeyframeCount(h.Video, Color, [Exposure], second));
        Assert.True(await h.Editor.ResetAsync(h.Video, Color, [(Exposure, 0)], "Restablecer exposición", Answer(true, []), second));
        Assert.Equal(0, h.Video.Effects[1].Parameters[Exposure].Value);

        Assert.False(h.Editor.SetValue(h.Video, Color, Exposure, 1, "Cambiar exposición", "no-existe"));
        Assert.Equal(2, h.Video.Effects.Count);
    });

    [Fact]
    public void ConVelocidad_LosKeyframesVanEnTiempoDelOrigen_YLaNavegacionEnElDeLaLinea() => UiThread.Run(async () =>
    {
        Harness h = await Harness.WithClip();
        Assert.True(h.Session.Execute(Edits.SetSpeed(h.Session.SequenceId, h.Video.Id, new Rational(2, 1))));

        h.At(1);
        Assert.True(h.Editor.ToggleKeyframe(h.Video, Transform, P.Opacity, 1));
        h.At(3);
        h.Drag(P.Opacity, 0.5);
        Assert.Equal([Time.FromSeconds(2), Time.FromSeconds(6)], h.Parameter(P.Opacity)!.Keyframes.Select(k => k.Time));

        h.At(1);
        Assert.NotNull(h.Read(P.Opacity).KeyframeHere);
        Assert.Equal(Time.FromSeconds(3), h.Read(P.Opacity).Next);
        h.At(2);
        Assert.Null(h.Read(P.Opacity).KeyframeHere);
        Assert.Equal(0.75, h.Read(P.Opacity).Value, 6);
        Assert.Equal(Time.FromSeconds(1), h.Read(P.Opacity).Previous);
    });
}
