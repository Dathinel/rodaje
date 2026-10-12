using Rodaje.App.Services.Templates;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Templates;

/// <summary>Nombres de las plantillas propias: sin marcas bidi y nunca el de una de fábrica.</summary>
public class TemplateNameTests
{
    private static ProjectTemplate Named(string name) => new()
    {
        Id = ProjectTemplate.NewUserId(),
        Name = name,
        VideoTracks = [new() { Name = "Fondo‮4pm.exe" }],
        AudioTracks = [new() { Name = "⁦Voz⁩" }],
    };

    [Fact]
    public void Normalizar_QuitaLasMarcasBidi()
    {
        ProjectTemplate template = Named("‏Mi‮canal‎؜").Normalized()!;

        Assert.Equal("Micanal", template.Name);
        Assert.Equal("Fondo4pm.exe", template.VideoTracks[0].Name);
        Assert.Equal("Voz", template.AudioTracks[0].Name);
    }

    [Fact]
    public void Normalizar_SoloMarcasBidi_NoTieneArreglo()
    {
        Assert.Null(Named("‮‏").Normalized());
    }

    [Fact]
    public void Guardar_ConElNombreDeUnaDeFabrica_SeRechazaYNoEscribe()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ProjectTemplateStore.FileName);
        var store = new ProjectTemplateStore(path);
        string builtIn = BuiltInTemplates.All[0].Name;

        var error = Assert.Throws<ArgumentException>(() => store.Save(Named(builtIn.ToUpperInvariant())));
        Assert.Contains("fábrica", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => store.Save(Named("‮" + builtIn)));

        Assert.Empty(store.UserTemplates);
        Assert.False(File.Exists(path));
        Assert.Equal(builtIn + " (copia)", store.Save(Named(builtIn + " (copia)")).Name);
    }
}
