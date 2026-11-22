using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Settings;

/// <summary>
/// La carpeta de datos en una USB que se quitó o de solo lectura (RODAJE_DATOS): guardar los ajustes no puede lanzar
/// desde un clic (los visores, las transiciones, exportar los atajos), sino avisar una vez y seguir con ellos en memoria.
/// </summary>
public class SettingsUnwritableTests
{
    [Fact]
    public void SinPoderEscribir_NoLanzaYAvisaUnaSolaVez()
    {
        using var dir = new TempDirectory();
        string blocker = dir.File("usb");
        File.WriteAllText(blocker, "un archivo donde debería haber una carpeta");
        var store = new SettingsStore(Path.Combine(blocker, SettingsStore.FileName));
        var warnings = new List<string>();
        store.SaveFailed += warnings.Add;

        store.Current.LibraryWidth = 400;
        Assert.False(store.SaveIfChanged());
        store.Current.LibraryWidth = 420;
        store.Save();

        string warning = Assert.Single(warnings);
        Assert.Contains("ajustes", warning, StringComparison.Ordinal);
        Assert.Equal(420, store.Current.LibraryWidth);
    }

    [Fact]
    public void AlVolverLaCarpeta_GuardaYAvisaOtraVezSiSeVuelveAPerder()
    {
        using var dir = new TempDirectory();
        string folder = dir.File("usb");
        File.WriteAllText(folder, "");
        var store = new SettingsStore(Path.Combine(folder, SettingsStore.FileName));
        var warnings = new List<string>();
        store.SaveFailed += warnings.Add;
        store.Save();

        File.Delete(folder);
        Directory.CreateDirectory(folder);
        store.Current.LibraryWidth = 400;
        Assert.True(store.SaveIfChanged());

        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "");
        store.Current.LibraryWidth = 410;
        store.Save();
        Assert.Equal(2, warnings.Count);
    }
}
