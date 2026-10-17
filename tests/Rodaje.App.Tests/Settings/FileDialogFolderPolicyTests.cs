using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Settings;

public sealed class FileDialogFolderPolicyTests
{
    [Fact]
    public void Abrir_UsaLaUltimaCarpetaYLaRecuerdaEntreUsos()
    {
        using var dir = new TempDirectory();
        string first = Directory.CreateDirectory(Path.Combine(dir.Path, "Material")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(dir.Path, "Otra carpeta")).FullName;
        var settings = new AppSettings { LastOpenFolder = first };

        Assert.Equal(first, FileDialogFolderPolicy.OpenFolder(settings));

        FileDialogFolderPolicy.RememberOpen(settings, Path.Combine(second, "clip.mov"));

        Assert.Equal(second, settings.LastOpenFolder);
    }

    [Theory]
    [InlineData(SaveDialogKind.Project)]
    [InlineData(SaveDialogKind.Export)]
    [InlineData(SaveDialogKind.Still)]
    [InlineData(SaveDialogKind.ExportPreset)]
    [InlineData(SaveDialogKind.Shortcuts)]
    public void Guardar_ElProyectoTienePrioridadYDespuesSeUsaLaCarpetaDelTipo(SaveDialogKind kind)
    {
        using var dir = new TempDirectory();
        string project = Directory.CreateDirectory(Path.Combine(dir.Path, "Proyecto")).FullName;
        string current = Directory.CreateDirectory(Path.Combine(dir.Path, "Proyecto actual")).FullName;
        string export = Directory.CreateDirectory(Path.Combine(dir.Path, "Exportaciones")).FullName;
        string still = Directory.CreateDirectory(Path.Combine(dir.Path, "Fotogramas")).FullName;
        string preset = Directory.CreateDirectory(Path.Combine(dir.Path, "Preajustes")).FullName;
        string shortcuts = Directory.CreateDirectory(Path.Combine(dir.Path, "Atajos")).FullName;
        var settings = new AppSettings
        {
            LastProjectSaveFolder = project,
            LastStillExportFolder = still,
            LastExportPresetFolder = preset,
            LastShortcutExportFolder = shortcuts,
            Export = new ExportPreferences { Folder = export },
        };
        string? expected = kind switch
        {
            SaveDialogKind.Project => project,
            SaveDialogKind.Export => export,
            SaveDialogKind.Still => still,
            SaveDialogKind.ExportPreset => preset,
            SaveDialogKind.Shortcuts => shortcuts,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(current, FileDialogFolderPolicy.SaveFolder(settings, kind, current));
        Assert.Equal(expected, FileDialogFolderPolicy.SaveFolder(settings, kind));
    }

    [Theory]
    [InlineData(SaveDialogKind.Project)]
    [InlineData(SaveDialogKind.Export)]
    [InlineData(SaveDialogKind.Still)]
    [InlineData(SaveDialogKind.ExportPreset)]
    [InlineData(SaveDialogKind.Shortcuts)]
    public void Guardar_RecuerdaLaCarpetaElegidaPorTipo(SaveDialogKind kind)
    {
        using var dir = new TempDirectory();
        string folder = Directory.CreateDirectory(Path.Combine(dir.Path, "Destino")).FullName;
        var settings = new AppSettings();

        FileDialogFolderPolicy.RememberSave(settings, kind, Path.Combine(folder, "salida.ext"));

        Assert.Equal(folder, FileDialogFolderPolicy.SaveFolder(settings, kind));
    }

    [Fact]
    public void Ajustes_ConservanLasCarpetasEntreSesiones()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.LastOpenFolder = dir.Path;
        FileDialogFolderPolicy.RememberSave(store.Current, SaveDialogKind.Project, Path.Combine(dir.Path, "proyecto.rodaje"));
        FileDialogFolderPolicy.RememberSave(store.Current, SaveDialogKind.Export, Path.Combine(dir.Path, "exportacion.mp4"));
        FileDialogFolderPolicy.RememberSave(store.Current, SaveDialogKind.Still, Path.Combine(dir.Path, "fotograma.png"));
        FileDialogFolderPolicy.RememberSave(store.Current, SaveDialogKind.ExportPreset, Path.Combine(dir.Path, "preajuste.rodaje-export"));
        FileDialogFolderPolicy.RememberSave(store.Current, SaveDialogKind.Shortcuts, Path.Combine(dir.Path, "atajos.json"));
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        Assert.Equal(dir.Path, FileDialogFolderPolicy.OpenFolder(loaded.Current));
        Assert.All(Enum.GetValues<SaveDialogKind>(), kind => Assert.Equal(dir.Path, FileDialogFolderPolicy.SaveFolder(loaded.Current, kind)));
    }
}
