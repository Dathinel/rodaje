using Rodaje.App.Services;
using Rodaje.App.Services.Templates;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Templates;

public class TemplateSessionTests
{
    [Fact]
    public void ProyectoDesdePlantilla_TomaFormatoYPistasYSinHistorial() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        Assert.False(session.KeepsTemplateFormat);

        Assert.True(await session.NewProjectFromTemplateAsync(BuiltInTemplates.Find(BuiltInTemplates.Vertical1080Id)!));

        Assert.Equal((1080, 1920), (session.Sequence.Width, session.Sequence.Height));
        Assert.Equal(2, session.Sequence.VideoTracks.Count);
        Assert.Equal(EditorSession.UntitledName, session.ProjectName);
        Assert.False(session.History.CanUndo);
        Assert.True(session.KeepsTemplateFormat);
    });

    [Fact]
    public void ProyectoDesdePlantilla_ElPrimerVideoNoCambiaElFormato() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10, 3840, 2160, new Rational(25, 1));
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(await session.NewProjectFromTemplateAsync(BuiltInTemplates.Find(BuiltInTemplates.Square1080Id)!));

        ImportResult result = await session.ImportMediaAsync([Path.GetFullPath("a.mp4")]);

        Assert.Single(result.MediaIds);
        Assert.Equal((1080, 1080), (session.Sequence.Width, session.Sequence.Height));
        Assert.Equal(Rational.Fps30, session.Sequence.FrameRate);
    });

    [Fact]
    public void ElegirElFormatoEnUnProyectoVacio_ElPrimerVideoNoLoCambia() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10, 1920, 1080, new Rational(25, 1));
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(session.ChangeFormat(1080, 1920, Rational.Fps30, Rodaje.App.Services.Format.ClipFit.Fit));

        await session.ImportMediaAsync([Path.GetFullPath("a.mp4")]);

        Assert.Equal((1080, 1920), (session.Sequence.Width, session.Sequence.Height));
        Assert.True(session.History.CanUndo);
    });

    [Fact]
    public void ProyectoNuevoCorriente_SigueTomandoElFormatoDelPrimerVideo() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10, 3840, 2160, new Rational(25, 1));
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        Assert.True(await session.NewProjectFromTemplateAsync(BuiltInTemplates.Find(BuiltInTemplates.Square1080Id)!));
        Assert.True(await session.NewProjectAsync());
        Assert.False(session.KeepsTemplateFormat);

        await session.ImportMediaAsync([Path.GetFullPath("a.mp4")]);

        Assert.Equal(3840, session.Sequence.Width);
        Assert.Equal(new Rational(25, 1), session.Sequence.FrameRate);
    });

    [Fact]
    public void GuardarComoPlantilla_DesdeLaSesionYVolverACrear() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator(), new NewProjectTracks(3, 1));
        ProjectTemplate template = session.CreateTemplateFromSequence("Tres pistas");
        Assert.Equal(["Video 1", "Video 2", "Video 3"], template.VideoTracks.Select(t => t.Name));
        Assert.Equal(["Audio 1"], template.AudioTracks.Select(t => t.Name));

        session.NewProjectTracks = NewProjectTracks.Default;
        Assert.True(await session.NewProjectFromTemplateAsync(template));
        Assert.Equal(3, session.Sequence.VideoTracks.Count);
        Assert.Single(session.Sequence.AudioTracks);
    });
}
