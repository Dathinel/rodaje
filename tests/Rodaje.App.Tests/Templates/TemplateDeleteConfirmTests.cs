using Rodaje.App.Services;
using Rodaje.App.Services.Templates;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Templates;

/// <summary>«Borrar plantilla propia» pregunta antes y solo borra si se confirma.</summary>
public class TemplateDeleteConfirmTests
{
    private static ProjectTemplate Square(string name) => new()
    {
        Id = ProjectTemplate.NewUserId(),
        Name = name,
        Width = 1080,
        Height = 1080,
        FrameRate = Rational.Fps30,
        VideoTracks = [new() { Name = "Video" }],
        AudioTracks = [new() { Name = "Audio" }],
    };

    private static EditorSession Session(FakeDialogs dialogs) =>
        new(new FakePlayback(), new FakeProber(), dialogs, new SequentialIdGenerator());

    [Fact]
    public void SiSeConfirma_LaBorraYLaQuitaDelArchivo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        var store = new ProjectTemplateStore(path);
        ProjectTemplate saved = store.Save(Square("Mi canal"));
        var dialogs = new FakeDialogs { ConfirmAnswer = true };

        Assert.True(await Session(dialogs).DeleteUserTemplateAsync(store, saved.Id));

        Assert.Empty(store.UserTemplates);
        var reloaded = new ProjectTemplateStore(path);
        Assert.Null(reloaded.Load());
        Assert.Empty(reloaded.UserTemplates);
        (string title, string message, string confirm) = Assert.Single(dialogs.Confirmations);
        Assert.Equal("¿Borrar la plantilla?", title);
        Assert.Contains("«Mi canal»", message, StringComparison.Ordinal);
        Assert.Contains("No se puede deshacer", message, StringComparison.Ordinal);
        Assert.Equal("Borrar", confirm);
    });

    [Fact]
    public void SiSeCancela_NoBorraNadaNiTocaElArchivo() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        var store = new ProjectTemplateStore(path);
        ProjectTemplate saved = store.Save(Square("Mi canal"));
        string before = File.ReadAllText(path);
        var dialogs = new FakeDialogs { ConfirmAnswer = false };

        Assert.False(await Session(dialogs).DeleteUserTemplateAsync(store, saved.Id));

        Assert.Equal(saved.Id, Assert.Single(store.UserTemplates).Id);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(dialogs.Confirmations);
    });

    [Fact]
    public void PorDefectoElDobleNoConfirma() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        ProjectTemplate saved = store.Save(Square("Mi canal"));

        Assert.False(await Session(new FakeDialogs()).DeleteUserTemplateAsync(store, saved.Id));

        Assert.Single(store.UserTemplates);
    });

    [Fact]
    public void UnaDeFabricaOUnIdQueNoEsta_NoPregunta() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        store.Save(Square("Mi canal"));
        var dialogs = new FakeDialogs { ConfirmAnswer = true };
        EditorSession session = Session(dialogs);

        Assert.False(await session.DeleteUserTemplateAsync(store, BuiltInTemplates.Vertical1080Id));
        Assert.False(await session.DeleteUserTemplateAsync(store, "no-existe"));

        Assert.Empty(dialogs.Confirmations);
        Assert.Single(store.UserTemplates);
        Assert.NotNull(store.Find(BuiltInTemplates.Vertical1080Id));
    });

    [Fact]
    public void SoloBorraLaElegida() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        ProjectTemplate first = store.Save(Square("Primera"));
        ProjectTemplate second = store.Save(Square("Segunda"));
        var dialogs = new FakeDialogs { ConfirmAnswer = true };

        Assert.True(await Session(dialogs).DeleteUserTemplateAsync(store, first.Id));

        Assert.Equal(second.Id, Assert.Single(store.UserTemplates).Id);
        Assert.Contains("«Primera»", Assert.Single(dialogs.Confirmations).Message, StringComparison.Ordinal);
    });
}
