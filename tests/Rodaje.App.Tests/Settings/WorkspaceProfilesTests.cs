using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Settings;

public class WorkspaceProfilesTests
{
    [Fact]
    public void TresDeFabrica_ColorEnsanchaElPanelYEscondeTexto()
    {
        var settings = new AppSettings();
        Assert.Equal(["Edición", "Color", "Audio"], WorkspaceProfiles.All(settings).Select(p => p.Name));

        WorkspaceProfiles.Apply(settings, WorkspaceProfiles.Find(settings, "fabrica.color")!);
        Assert.Equal(480, settings.InspectorWidth);
        Assert.Equal(TimelineProportion.MoreViewer, settings.TimelineShare);
        Assert.Contains("Texto", settings.HiddenInspectorTabs);
        Assert.Equal("fabrica.color", settings.Workspace);

        WorkspaceProfiles.Apply(settings, WorkspaceProfiles.Find(settings, WorkspaceProfiles.EditingId)!);
        Assert.Empty(settings.HiddenInspectorTabs);
        Assert.Equal(AppSettings.DefaultInspectorWidth, settings.InspectorWidth);
    }

    [Fact]
    public void Propios_SeGuardanReemplazanPorNombreYSeQuitan()
    {
        var settings = new AppSettings();
        var current = new WorkspaceProfile { LibraryWidth = 300, InspectorWidth = 500, TimelineShare = 0.5, InspectorTab = "Texto", HiddenInspectorTabs = ["Máscaras"] };
        WorkspaceProfile saved = WorkspaceProfiles.SaveCurrent(settings, " Subtítulos ", current);
        Assert.Equal("Subtítulos", saved.Name);
        Assert.Equal(saved.Id, settings.Workspace);
        Assert.Equal(4, WorkspaceProfiles.All(settings).Count());

        WorkspaceProfile again = WorkspaceProfiles.SaveCurrent(settings, "subtítulos", new WorkspaceProfile { InspectorWidth = 600 });
        Assert.Equal(saved.Id, again.Id);
        Assert.Single(settings.Workspaces);
        Assert.Equal(600, settings.Workspaces[0].InspectorWidth);

        Assert.True(WorkspaceProfiles.Remove(settings, saved.Id));
        Assert.Equal(WorkspaceProfiles.EditingId, settings.Workspace);
        Assert.False(WorkspaceProfiles.Remove(settings, "fabrica.color"));
    }

    [Fact]
    public void LoLeidoDeUnArchivoSeCorrige()
    {
        var settings = new AppSettings
        {
            Workspace = "no.existe",
            Workspaces =
            [
                new() { Id = "propio.a", Name = "A", InspectorWidth = 5000, TimelineShare = double.NaN },
                new() { Id = "propio.a", Name = "Repetido" },
                new() { Id = "fabrica.color", Name = "Tapa uno de fábrica" },
                new() { Id = "", Name = "Sin id" },
            ],
        };
        settings.Normalize();
        WorkspaceProfile only = Assert.Single(settings.Workspaces);
        Assert.Equal(900, only.InspectorWidth);
        Assert.Equal(TimelineProportion.Balanced, only.TimelineShare);
        Assert.Null(settings.Workspace);
    }

    [Fact]
    public void UnEspacioPropioViejo_PasaSuAltoAProporcion()
    {
        var settings = new AppSettings { Workspaces = [new() { Id = "propio.v", Name = "Viejo", TimelineHeight = 450 }] };
        settings.Normalize();

        WorkspaceProfile own = Assert.Single(settings.Workspaces);
        Assert.Equal(0.5, own.TimelineShare, 3);
        Assert.Null(own.TimelineHeight);
    }
}
