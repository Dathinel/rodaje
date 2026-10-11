using Rodaje.App.Services;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Un título en la línea de tiempo: nombre, recortes sin tope de material y gestos que no le aplican.</summary>
public class TitleClipTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal con a [0,10) y su audio; un título «Hola» en V2 [2,7).</summary>
    private static async Task<(EditorSession Session, Clip Title)> WithTitle()
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        var generator = new ClipGenerator { Text = "Hola\nmundo", Style = new TextStyle() };
        SequenceEditCommand insert = Edits.InsertTitle(session.SequenceId, S(2), S(5), generator);
        Assert.True(session.Execute(insert));
        return (session, session.Sequence.GetClip(insert.CreatedIds[0]).Clip);
    }

    private static DragOptions Options(EditorSession session) => new()
    {
        Snapping = false,
        MagneticTrackId = session.MainTrack!.Id,
        RippleScope = session.Timeline.RippleScope,
        PixelsPerSecond = 100,
    };

    private static ClipDragSession Start(EditorSession session, Clip clip, DragOperation operation, double grab)
    {
        var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, clip.Id, operation, S(grab), Options(session), session.Position));
        drag.Begin();
        return drag;
    }

    [Fact]
    public void ElNombreEsLaPrimeraLineaDelTexto()
    {
        var project = new Project();
        Assert.Equal("Hola", ClipLabels.Name(project, new Clip { Generator = new ClipGenerator { Text = "\n  Hola \nmundo" } }));
        Assert.Equal(ClipLabels.EmptyTitle, ClipLabels.Name(project, new Clip { Generator = new ClipGenerator { Text = " \n\t" } }));
        Assert.Equal("solid", ClipLabels.Name(project, new Clip { Generator = new ClipGenerator { Kind = "solid" } }));
        Assert.Equal("falta", ClipLabels.Name(project, new Clip { MediaId = "x" }, "falta"));
        string longName = ClipLabels.Name(project, new Clip { Generator = new ClipGenerator { Text = new string('a', 200) } });
        Assert.Equal(ClipLabels.MaxTitleName, longName.Length);
        Assert.EndsWith("…", longName, StringComparison.Ordinal);
    }

    [Fact]
    public void ElMargenDeUnTituloNoTieneTopeDeMaterial()
    {
        var title = new Clip { Generator = new ClipGenerator(), SourceIn = S(0), SourceOut = S(5) };
        Assert.Equal(ClipDrag.GeneratorRoom, ClipDrag.Room(title, TrimEdge.In, Time.Zero));
        Assert.Equal(ClipDrag.GeneratorRoom, ClipDrag.Room(title, TrimEdge.Out, Time.Zero));
    }

    [Fact]
    public void AlargarElInicioDeUnTituloPasaDeSuOrigenVirtual() => UiThread.Run(async () =>
    {
        (EditorSession session, Clip title) = await WithTitle();
        ClipDragSession drag = Start(session, title, DragOperation.TrimStart, 2);
        drag.Move(S(0.5), null);
        drag.Complete();

        Clip trimmed = session.Sequence.GetClip(title.Id).Clip;
        Assert.Equal(S(0.5), trimmed.Start);
        Assert.Equal(S(-1.5), trimmed.SourceIn);
        Assert.Equal(S(6.5), trimmed.Duration);

        drag = Start(session, trimmed, DragOperation.TrimEnd, 7);
        drag.Move(S(60), null);
        drag.Complete();
        Assert.Equal(S(60), session.Sequence.GetClip(title.Id).Clip.End);
    });

    [Theory]
    [InlineData(DragOperation.Slip, 4.0)]
    [InlineData(DragOperation.StretchStart, 2.0)]
    [InlineData(DragOperation.StretchEnd, 7.0)]
    public void SlipYAltBordeNoCambianUnTituloYLoExplican(DragOperation operation, double grab) => UiThread.Run(async () =>
    {
        (EditorSession session, Clip title) = await WithTitle();
        int steps = session.History.Count;
        ClipDragSession drag = Start(session, title, operation, grab);
        DragPlan plan = drag.Move(S(grab + 1), null);
        drag.Complete();

        Assert.Null(plan.Build);
        Assert.Equal(ClipLabels.NoMaterial, plan.Balloon!.Value.Text);
        Clip after = session.Sequence.GetClip(title.Id).Clip;
        Assert.Equal((S(2), S(0), S(5)), (after.Start, after.SourceIn, after.SourceOut));
        Assert.Null(after.Speed);
        Assert.Equal(steps, session.History.Count);
    });

    [Fact]
    public void LosAtajosDeVelocidadIgnoranLosTitulos() => UiThread.Run(async () =>
    {
        (EditorSession session, Clip title) = await WithTitle();
        session.Select([title.Id]);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(3) });
        int steps = session.History.Count;

        Assert.False(session.StepSpeed(faster: true));
        Assert.False(session.SetSpeed([title.Id], new Rational(2, 1)));
        Assert.False(session.FreezeFrameAtPlayhead());
        Assert.False(session.ToggleReverse());
        Assert.Equal(steps, session.History.Count);
        Assert.Null(session.Sequence.GetClip(title.Id).Clip.Speed);
    });

    /// <remarks>
    /// Punto de conexión: <c>PlayerEngine.EnsureCompositor</c> (Views/Player) arma el <c>Compositor</c> con
    /// <c>Resources = _content</c>; ahí se le pasa el rasterizador de texto de <c>Rodaje.Render/Text</c> creado sobre el
    /// <c>GpuDevice</c> del hilo de render, y la capa generada que emita <c>TimelinePlan</c> se dibuja sin más cambios en la
    /// App. La prueba de humo se escribe con la autoprueba (captura «texto» con píxeles del título en el visor).
    /// </remarks>
    [Fact(Skip = "Falta el carril generado del motor y el rasterizador de Rodaje.Render/Text (agentes de la 5b en paralelo).")]
    public void ElTituloSeVeEnElVisor()
    {
    }

    [Fact]
    public void MoverYCortarUnTituloFuncionaComoCualquierClip() => UiThread.Run(async () =>
    {
        (EditorSession session, Clip title) = await WithTitle();
        ClipDragSession drag = Start(session, title, DragOperation.Move, 3);
        drag.Move(S(6), null);
        drag.Complete();
        Assert.Equal(S(5), session.Sequence.GetClip(title.Id).Clip.Start);

        session.Select([title.Id]);
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(7) });
        Assert.True(session.SplitAtPlayhead());
        Track track = session.Sequence.GetClip(title.Id).Track;
        Assert.Equal(2, track.Clips.Count);
        Assert.All(track.Clips, c => Assert.Equal("Hola\nmundo", c.Generator!.Text));
        Assert.False(track.Clips[0].Generator!.Edges.Out);
        Assert.False(track.Clips[1].Generator!.Edges.In);
    });
}
