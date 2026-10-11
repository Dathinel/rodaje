using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Inspector;

public class SpeedCurveTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static readonly SpeedPoint[] Three = [new(0, 100), new(5000, 300), new(10_000, 100)];

    private sealed class Harness
    {
        private Harness(EditorSession session)
        {
            Session = session;
            Editor = new SpeedEditor(session, SpeedCommands.Default);
        }

        public EditorSession Session { get; }

        public SpeedEditor Editor { get; }

        public Clip Video => Session.MainTrack!.Clips[0];

        public Clip Second => Session.MainTrack!.Clips[1];

        public Clip Audio => Session.Sequence.AudioTracks[0].Clips[0];

        public static async Task<Harness> TwoClips()
        {
            var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
            await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
            var harness = new Harness(session);
            session.Select([harness.Video.Id]);
            return harness;
        }

        public void Choose(SpeedPreset preset) =>
            Assert.True(Editor.ChooseCurve(SpeedEditor.CurveOptions.ToList().FindIndex(o => o.Preset == preset)));
    }

    [Theory]
    [InlineData(0.1, 0)]
    [InlineData(1, 0.5)]
    [InlineData(10, 1)]
    [InlineData(0.01, 0)]
    [InlineData(50, 1)]
    public void Altura_EsLogaritmica(double rate, double level) => Assert.Equal(level, SpeedCurveMath.Level(rate), 9);

    [Fact]
    public void Altura_IdaYVuelta_DaLaMismaCentesima()
    {
        for (int h = 10; h <= 1000; h++)
        {
            Assert.Equal(h, SpeedCurveMath.HundredthsAt(SpeedCurveMath.Level(h / 100.0)));
        }

        Assert.Equal(10, SpeedCurveMath.HundredthsAt(-1));
        Assert.Equal(1000, SpeedCurveMath.HundredthsAt(2));
    }

    [Fact]
    public void Velocidad_SeInterpolaEntrePuntos_YSigueFuera()
    {
        Assert.Equal(2, SpeedCurveMath.RateAt(Three, 2500), 9);
        Assert.Equal(3, SpeedCurveMath.RateAt(Three, 5000), 9);
        Assert.Equal(1, SpeedCurveMath.RateAt(Three, -10), 9);
        Assert.Equal(1, SpeedCurveMath.RateAt(Three, 20_000), 9);
        Assert.Equal(1, SpeedCurveMath.RateAt([], 0), 9);
    }

    [Fact]
    public void Forma_PasaPorLosPuntos_YVaDe0A1()
    {
        List<(double X, double Level)> shape = SpeedCurveMath.Shape(Three, 0, 10_000, 4);
        Assert.Equal(9, shape.Count);
        Assert.Equal((0.0, 0.5), shape[0]);
        Assert.Contains(shape, s => s.X == 0.5 && Math.Abs(s.Level - SpeedCurveMath.Level(3)) < 1e-9);
        Assert.Equal(1.0, shape[^1].X);

        // Un tramo (el trozo de un clip dividido) se reparte entero de 0 a 1.
        List<(double X, double Level)> part = SpeedCurveMath.Shape(Three, 2500, 5000, 2);
        Assert.Equal(0, part[0].X);
        Assert.Equal(SpeedCurveMath.Level(2), part[0].Level, 9);
        Assert.Equal(SpeedCurveMath.Level(3), part[^1].Level, 9);
    }

    [Fact]
    public void Tocar_EligeElPuntoMasCercanoDentroDelRadio()
    {
        // Área de 100 × 100: el punto del medio (3×) está en x 50 y y ≈ 26.
        double y = (1 - SpeedCurveMath.Level(3)) * 100;
        Assert.Equal(1, SpeedCurveMath.Hit(Three, 52, y + 3, 100, 100, 8));
        Assert.Equal(0, SpeedCurveMath.Hit(Three, 1, 50, 100, 100, 8));
        Assert.Null(SpeedCurveMath.Hit(Three, 30, 90, 100, 100, 8));
    }

    [Fact]
    public void Mover_RespetaElOrdenLosExtremosYLosLimites()
    {
        Assert.Equal(new SpeedPoint(9999, 1000), SpeedCurveMath.Move(Three, 1, 20_000, 5000)[1]);
        Assert.Equal(new SpeedPoint(1, 10), SpeedCurveMath.Move(Three, 1, -5, 1)[1]);
        Assert.Equal(new SpeedPoint(0, 250), SpeedCurveMath.Move(Three, 0, 4000, 250)[0]);
        Assert.Equal(new SpeedPoint(10_000, 50), SpeedCurveMath.Move(Three, 2, 4000, 50)[2]);

        // No toca la lista de partida.
        Assert.Equal(new SpeedPoint(5000, 300), Three[1]);
    }

    [Fact]
    public void Anadir_SobreLaCurva_YQuitar_SinTocarLosExtremos()
    {
        List<SpeedPoint> added = SpeedCurveMath.Add(Three, 2500)!;
        Assert.Equal([new(0, 100), new(2500, 200), new(5000, 300), new(10_000, 100)], added);
        Assert.Null(SpeedCurveMath.Add(Three, 5000));
        Assert.Null(SpeedCurveMath.Add(Three, 0));
        Assert.Null(SpeedCurveMath.Add(Three, 10_000));

        List<SpeedPoint> full = [.. Enumerable.Range(0, SpeedCurve.MaxPoints).Select(i => new SpeedPoint(i * 100, 100))];
        Assert.Null(SpeedCurveMath.Add(full, 50));

        Assert.Equal([new(0, 100), new(10_000, 100)], SpeedCurveMath.Remove(Three, 1));
        Assert.Null(SpeedCurveMath.Remove(Three, 0));
        Assert.Null(SpeedCurveMath.Remove(Three, 2));
        Assert.Null(SpeedCurveMath.Remove([new(0, 100), new(10_000, 100)], 1));
    }

    [Fact]
    public void Plana_SeAcotaA10x() =>
        Assert.Equal([new(0, 1000), new(10_000, 1000)], SpeedCurveMath.Flat(40));

    [Fact]
    public void Selector_MuestraElPreajusteQueCoincide()
    {
        Assert.Equal(CurveState.NoneOption, SpeedEditor.OptionOf(null));
        foreach (SpeedPreset preset in SpeedPresets.All)
        {
            int option = SpeedEditor.OptionOf(SpeedPresets.Curve(preset));
            Assert.Equal(preset, SpeedEditor.CurveOptions[option].Preset);
            Assert.Equal(SpeedPresets.Name(preset), SpeedEditor.CurveOptions[option].Name);
        }

        Assert.Equal(CurveState.CustomOption, SpeedEditor.OptionOf(new SpeedCurve { Span = S(1), Points = [.. Three] }));
        Assert.Equal(["Ninguna", "Personalizada", "Montaje", "Héroe", "Bala", "Entrada rápida", "Salida rápida"],
            SpeedEditor.CurveOptions.Select(o => o.Name));
    }

    [Fact]
    public void Preajuste_EsUnPaso_ConLosEnlazados_YLaDuracionDeSuMedia() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        int steps = h.Session.History.Count;
        h.Choose(SpeedPreset.Bullet);

        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal(SpeedPresets.Points(SpeedPreset.Bullet), h.Video.Speed!.Curve!.Points);
        Assert.Equal(SpeedPresets.Points(SpeedPreset.Bullet), h.Audio.Speed!.Curve!.Points);
        double expected = 10 / SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.Bullet));
        Assert.Equal(expected, h.Video.Duration.ToSeconds(), 1);
        Assert.Equal(h.Video.End, h.Second.Start);
        Assert.Equal(SpeedEditor.OptionOf(h.Video.Speed.Curve), h.Editor.ReadCurve()!.Option);

        // El mismo preajuste otra vez no hace nada; otro reemplaza en un paso, y «Ninguna» vuelve a 1×.
        Assert.False(h.Editor.ChooseCurve(h.Editor.ReadCurve()!.Option));
        h.Choose(SpeedPreset.Hero);
        Assert.Equal(steps + 2, h.Session.History.Count);
        Assert.True(h.Editor.ChooseCurve(CurveState.NoneOption));
        Assert.Null(h.Video.Speed);
        Assert.Equal(h.Video.SourceDuration, h.Video.Duration);
        Assert.Equal(steps + 3, h.Session.History.Count);

        h.Session.Undo();
        Assert.Equal(SpeedPresets.Points(SpeedPreset.Hero), h.Video.Speed!.Curve!.Points);
    });

    [Fact]
    public void Personalizada_EmpiezaPlanaALaVelocidadDeAhora() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.SetRate(new Rational(2, 1)));
        Assert.True(h.Editor.ChooseCurve(CurveState.CustomOption));
        Assert.Equal([new(0, 200), new(10_000, 200)], h.Video.Speed!.Curve!.Points);
        Assert.Equal(S(5).ToSeconds(), h.Video.Duration.ToSeconds(), 2);
        Assert.Equal(CurveState.CustomOption, h.Editor.ReadCurve()!.Option);

        // Con una curva ya puesta, «Personalizada» no cambia nada: se personaliza moviendo sus puntos.
        h.Choose(SpeedPreset.Montage);
        int steps = h.Session.History.Count;
        Assert.False(h.Editor.ChooseCurve(CurveState.CustomOption));
        Assert.Equal(steps, h.Session.History.Count);
    });

    [Fact]
    public void ArrastrarUnPunto_EsUnPaso_YSaleDelInicioDelGesto() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        h.Choose(SpeedPreset.Hero);
        SpeedCurve start = h.Video.Speed!.Curve!.Clone();
        Time startDuration = h.Video.Duration;
        int steps = h.Session.History.Count;

        Assert.True(h.Editor.BeginPointDrag(2));
        Assert.True(h.Editor.IsDraggingPoint);
        foreach ((int at, int hundredths) in new[] { (4000, 30), (4500, 60), (3000, 15), (4200, 40) })
        {
            Assert.True(h.Editor.DragPoint(at, hundredths));
        }

        h.Editor.EndPointDrag();
        Assert.False(h.Editor.IsDraggingPoint);
        Assert.Equal(steps + 1, h.Session.History.Count);
        Assert.Equal("Cambiar curva de velocidad", h.Session.UndoDescription);
        Assert.Equal(SpeedCurveMath.Move(start.Points, 2, 4200, 40), h.Video.Speed!.Curve!.Points);
        Assert.Equal(h.Video.Speed.Curve.Points, h.Audio.Speed!.Curve!.Points);
        Assert.Equal(CurveState.CustomOption, h.Editor.ReadCurve()!.Option);
        Time dragged = h.Video.Duration;
        Assert.NotEqual(startDuration, dragged);

        // De un solo salto al mismo destino queda igual: el camino del ratón no cuenta.
        h.Session.Undo();
        Assert.True(h.Video.Speed!.Curve!.ContentEquals(start));
        Assert.Equal(startDuration, h.Video.Duration);
        Assert.True(h.Editor.BeginPointDrag(2));
        Assert.True(h.Editor.DragPoint(4200, 40));
        h.Editor.EndPointDrag();
        Assert.Equal(dragged, h.Video.Duration);
        Assert.Equal(h.Video.End, h.Second.Start);
    });

    [Fact]
    public void ArrastrarYVolverAlInicio_DejaElClipComoEstaba() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        h.Choose(SpeedPreset.FastIn);
        SpeedCurve start = h.Video.Speed!.Curve!.Clone();
        Time duration = h.Video.Duration;

        Assert.True(h.Editor.BeginPointDrag(1));
        Assert.True(h.Editor.DragPoint(2000, 700));
        Assert.True(h.Editor.DragPoint(start.Points[1].At, start.Points[1].Hundredths));
        h.Editor.EndPointDrag();
        Assert.True(h.Video.Speed!.Curve!.ContentEquals(start));
        Assert.Equal(duration, h.Video.Duration);
    });

    [Fact]
    public void EscDuranteElArrastre_LoRevierte() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        h.Choose(SpeedPreset.Bullet);
        SpeedCurve start = h.Video.Speed!.Curve!.Clone();
        int steps = h.Session.History.Count;

        Assert.True(h.Editor.BeginPointDrag(1));
        Assert.True(h.Editor.DragPoint(2000, 500));
        h.Session.CancelGesture();
        Assert.False(h.Editor.IsDraggingPoint);
        Assert.False(h.Editor.DragPoint(1000, 500));
        h.Editor.EndPointDrag();
        Assert.Equal(steps, h.Session.History.Count);
        Assert.True(h.Video.Speed!.Curve!.ContentEquals(start));
    });

    [Fact]
    public void DobleClic_AnadeYQuita_UnPasoCadaUno() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.False(h.Editor.AddOrRemovePoint(5000, null));
        Assert.False(h.Editor.BeginPointDrag(0));
        h.Choose(SpeedPreset.FastOut);
        int count = h.Video.Speed!.Curve!.Points.Count;
        int steps = h.Session.History.Count;

        Assert.True(h.Editor.AddOrRemovePoint(3000, null));
        Assert.Equal(count + 1, h.Video.Speed!.Curve!.Points.Count);
        Assert.True(h.Editor.AddOrRemovePoint(3000, null) is false);
        Assert.True(h.Editor.AddOrRemovePoint(0, 1));
        Assert.Equal(count, h.Video.Speed!.Curve!.Points.Count);
        Assert.Equal(steps + 2, h.Session.History.Count);
        Assert.False(h.Editor.AddOrRemovePoint(0, 0));
        Assert.False(h.Editor.AddOrRemovePoint(0, count - 1));
    });

    [Fact]
    public void Congelado_NoTieneCurva() => UiThread.Run(async () =>
    {
        Harness h = await Harness.TwoClips();
        Assert.True(h.Editor.Freeze());
        CurveState state = h.Editor.ReadCurve()!;
        Assert.False(state.CanEdit);
        Assert.False(h.Editor.ChooseCurve(2));
    });

    [Fact]
    public void Ventana_YCursor_SiguenAlTrozoDelClip()
    {
        var clip = new Clip
        {
            Id = "c",
            Start = S(10),
            SourceIn = S(0),
            SourceOut = S(4),
            Duration = S(2),
            Speed = new ClipSpeed { Offset = S(1), Curve = new SpeedCurve { Span = S(4), Points = [new(0, 100), new(10_000, 100)] } },
        };
        Assert.Equal((2500.0, 7500.0), SpeedCurveMath.Window(clip));
        Assert.Equal(5000, SpeedCurveMath.AtOf(clip, S(11))!.Value, 6);
        Assert.Null(SpeedCurveMath.AtOf(clip, S(12)));
        Assert.Null(SpeedCurveMath.AtOf(clip, S(9)));
        Assert.Equal(S(1), SpeedCurveMath.ClipTimeOf(clip, 5000));
        Assert.Equal((0.0, 10_000.0), SpeedCurveMath.Window(new Clip { Id = "x" }));
    }
}
