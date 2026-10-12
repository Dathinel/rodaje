using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;

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

    /// <summary>
    /// Como lo conecta <c>PlayerEngine</c>: un rasterizador sobre el dispositivo del hilo de render, en la sesión (precarga)
    /// y en el compositor; en pausa, el aviso <c>Changed</c> hace volver a componer. La captura «texto» de la autoprueba
    /// lo comprueba en la app publicada.
    /// </summary>
    [GpuFact]
    public void ElTituloSeVeEnElVisor()
    {
        const int width = 640;
        const int height = 360;
        var style = TitleStyles.Default();
        style.Size = 120;
        style.Shadow = null;
        var track = new Track { Id = "v", Kind = TrackKind.Video, Name = "Texto" };
        track.Clips.Add(new Clip { Id = "t", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = S(5), Generator = new ClipGenerator { Text = "HOLA", Style = style } });
        var sequence = new Sequence { Id = "s", Name = "Títulos", FrameRate = Rational.Fps30, Width = width, Height = height };
        sequence.VideoTracks.Add(track);
        var project = new Project { Id = "p", Name = "Títulos", Sequences = [sequence] };

        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu);
        using var ready = new ManualResetEventSlim();
        titles.Changed += (_, _) => ready.Set();
        using TimelineSession session = TimelineSession.Open(project, "s", gpu,
            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never, Titles = titles });
        using var compositor = new Compositor(gpu, width, height) { Titles = titles };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)), "Los shaders no compilaron en 60 s.");
        Assert.Empty(titles.FindMissingFonts(session.GeneratedContents()));

        session.Seek(S(1));
        var watch = Stopwatch.StartNew();
        CompositeResult result;
        do
        {
            // Tope contra cuelgues, no medida: el costo de rasterizar lo exige TitleRasterizerTests. En la CI el título se
            // rasteriza en otro hilo y se compone con WARP en la CPU, compartida con las demás pruebas, y 10 s no alcanzaban.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "El título no llegó en 60 s.");
            ready.Wait(TimeSpan.FromMilliseconds(20));
            using CompositionFrame frame = session.Tick();
            result = compositor.Render(frame);
        }
        while (session.IsSeekPending || result.TitlesPending > 0);

        Assert.Equal(1, result.LayersDrawn);
        Assert.Equal(0, result.MissingContent);
        byte[] rgba = compositor.ReadbackRgba();
        int white = 0;
        for (int y = height / 3; y < height * 2 / 3; y++)
        {
            for (int x = width / 4; x < width * 3 / 4; x++)
            {
                int i = ((y * width) + x) * 4;
                white += rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] > 200 ? 1 : 0;
            }
        }

        Assert.True(white > 500, $"Solo {white} píxeles blancos en el centro.");
        Assert.Equal(0, rgba[0] + rgba[1] + rgba[2]);

        var unknown = TitleStyles.Default();
        unknown.Font = new StyleFont { Family = "Rodaje Fuente Que No Existe", Source = "rodaje-fuentes@1.0.0/nada" };
        track.Clips[0].Generator!.Style = unknown;
        session.Reload(project);
        MissingFont missing = Assert.Single(titles.FindMissingFonts(session.GeneratedContents()));
        Assert.Equal(("Rodaje Fuente Que No Existe", "t"), (missing.Family, missing.ClipId));
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

/// <summary>Necesita una GPU con Direct3D 11 que muestree texturas.</summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute() => Skip = Rodaje.TestMedia.Skip.GpuTextures;
}
