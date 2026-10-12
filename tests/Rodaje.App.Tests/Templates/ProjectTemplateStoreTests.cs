using Rodaje.App.Services.Templates;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Templates;

public class ProjectTemplateStoreTests
{
    private static ProjectTemplate Vertical(string name) => new()
    {
        Id = ProjectTemplate.NewUserId(),
        Name = name,
        Width = 1080,
        Height = 1920,
        FrameRate = Rational.Fps60,
        VideoTracks = [new() { Name = "Fondo" }, new() { Name = "Texto", Role = TrackRoles.Subtitles }],
        AudioTracks = [new() { Name = "Voz" }],
    };

    [Fact]
    public void SinArchivo_SoloLasDeFabrica()
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        Assert.Null(store.Load());
        Assert.Empty(store.UserTemplates);
        Assert.Equal(BuiltInTemplates.All, store.All);
    }

    [Fact]
    public void GuardarYLeer_ConservaFormatoPistasYNombres()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        var store = new ProjectTemplateStore(path);
        ProjectTemplate saved = store.Save(Vertical("Reels con voz"));

        var loaded = new ProjectTemplateStore(path);
        Assert.Null(loaded.Load());
        ProjectTemplate template = Assert.Single(loaded.UserTemplates);
        Assert.Equal(saved.Id, template.Id);
        Assert.Equal("Reels con voz", template.Name);
        Assert.Equal((1080, 1920, Rational.Fps60), (template.Width, template.Height, template.FrameRate));
        Assert.Equal(["Fondo", "Texto"], template.VideoTracks.Select(t => t.Name));
        Assert.Equal(TrackRoles.Subtitles, template.VideoTracks[1].Role);
        Assert.Null(template.VideoTracks[0].Role);
        Assert.Equal(["Voz"], template.AudioTracks.Select(t => t.Name));
        Assert.Same(template, loaded.Find(saved.Id));
        Assert.Equal(BuiltInTemplates.All.Count + 1, loaded.All.Count());
        Assert.DoesNotContain("isBuiltIn", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void GuardarConElMismoNombre_Reemplaza()
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        ProjectTemplate first = store.Save(Vertical("Mi canal"));
        ProjectTemplate second = Vertical("MI CANAL");
        second.Width = 720;

        ProjectTemplate replaced = store.Save(second);

        Assert.Equal(first.Id, replaced.Id);
        ProjectTemplate only = Assert.Single(store.UserTemplates);
        Assert.Equal(720, only.Width);
        Assert.Equal("MI CANAL", only.Name);
    }

    [Fact]
    public void Borrar_QuitaSoloLaPropia()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        var store = new ProjectTemplateStore(path);
        ProjectTemplate a = store.Save(Vertical("A"));
        store.Save(Vertical("B"));

        Assert.True(store.Remove(a.Id));
        Assert.False(store.Remove(a.Id));
        Assert.False(store.Remove(BuiltInTemplates.Landscape1080Id));

        var loaded = new ProjectTemplateStore(path);
        Assert.Null(loaded.Load());
        Assert.Equal(["B"], loaded.UserTemplates.Select(t => t.Name));
        Assert.NotNull(loaded.Find(BuiltInTemplates.Landscape1080Id));
    }

    [Fact]
    public void NoSePisaUnaDeFabricaNiSeGuardaUnaSinArreglo()
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        ProjectTemplate fake = Vertical("Falsa");
        fake.Id = BuiltInTemplates.Vertical1080Id;
        Assert.Throws<ArgumentException>(() => store.Save(fake));
        Assert.Throws<ArgumentException>(() => store.Save(new ProjectTemplate { Id = "propia.x", Name = "" }));
        Assert.Empty(store.UserTemplates);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public void ConElMáximo_NoGuardaOtraPeroSíReemplaza()
    {
        using var dir = new TempDirectory();
        var store = new ProjectTemplateStore(dir.File(ProjectTemplateStore.FileName));
        for (int i = 0; i < ProjectTemplateStore.MaxUserTemplates; i++)
        {
            store.Save(Vertical($"P{i}"));
        }

        Assert.Throws<InvalidOperationException>(() => store.Save(Vertical("Una más")));
        store.Save(Vertical("P3"));
        Assert.Equal(ProjectTemplateStore.MaxUserTemplates, store.UserTemplates.Count);
    }

    [Fact]
    public void ArchivoDañado_SeApartaYQuedanLasDeFabrica()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        File.WriteAllText(path, "{ esto no es json");

        var store = new ProjectTemplateStore(path);
        string? warning = store.Load();

        Assert.NotNull(warning);
        Assert.Contains("dañado", warning, StringComparison.Ordinal);
        Assert.Empty(store.UserTemplates);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(dir.File("plantillas.dañado.json")));
    }

    [Fact]
    public void ArchivoEditadoAMano_CorrigeLoQueSePuedeYDescartaElResto()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        File.WriteAllText(path, """
            {
              "version": 1,
              "templates": [
                null,
                { "id": "propia.ok", "name": "Buena", "width": 1081, "height": 1920, "frameRate": { "num": 25, "den": 1 }, "sampleRate": 48000,
                  "videoTracks": [ { "name": "" } ], "audioTracks": null },
                { "id": "propia.ok", "name": "Repetida", "width": 1920, "height": 1080, "frameRate": { "num": 25, "den": 1 }, "sampleRate": 48000 },
                { "id": "fabrica.16x9-1080p", "name": "Pisada", "width": 640, "height": 480, "frameRate": { "num": 25, "den": 1 }, "sampleRate": 48000 },
                { "id": "propia.mala", "name": "Rápida", "width": 1920, "height": 1080, "frameRate": { "num": 100000, "den": 1 }, "sampleRate": 48000 },
                { "id": "", "name": "Sin id", "width": 1920, "height": 1080, "frameRate": { "num": 30, "den": 1 }, "sampleRate": 48000 }
              ]
            }
            """);

        var store = new ProjectTemplateStore(path);
        Assert.Null(store.Load());

        ProjectTemplate only = Assert.Single(store.UserTemplates);
        Assert.Equal("Buena", only.Name);
        Assert.Equal(1080, only.Width);
        Assert.Equal(["Video 1"], only.VideoTracks.Select(t => t.Name));
        Assert.Equal(["Audio 1"], only.AudioTracks.Select(t => t.Name));
        Assert.Equal(1920, store.Find(BuiltInTemplates.Landscape1080Id)!.Width);
        Assert.Empty(ModelValidator.Validate(only.CreateProject("p", new SequentialIdGenerator())));
    }

    [Fact]
    public void ArchivoEnorme_NoSeLee()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        File.WriteAllText(path, "{\"templates\":[]," + new string(' ', 2 * 1024 * 1024) + "\"version\":1}");

        var store = new ProjectTemplateStore(path);
        Assert.NotNull(store.Load());
        Assert.Empty(store.UserTemplates);
    }
}
