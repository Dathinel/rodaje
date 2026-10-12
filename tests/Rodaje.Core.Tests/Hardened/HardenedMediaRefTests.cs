using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Versión endurecida de <c>TrackClipMediaTests.ResolvePath_RootedWithoutDrive_UsesProjectDrive</c>: allí, fuera de
/// Windows, la prueba hacía <c>return</c> y salía en verde sin comprobar nada. Aquí se informa como omitida, con la causa,
/// y en Windows comprueba además que el resultado sea absoluto y que una ruta UNC se respete.
/// </summary>
public class HardenedMediaRefTests
{
    [WindowsOnlyFact("las rutas «\\carpeta» y «Q:archivo» solo son relativas a una unidad en Windows.")]
    public void A_path_rooted_without_drive_takes_the_drive_of_the_project()
    {
        var m = new MediaRef { Path = @"\Videos\a b.mp4" };
        string resolved = m.ResolvePath(@"Q:\Proyectos\Uno");
        Assert.Equal(@"Q:\Videos\a b.mp4", resolved);
        Assert.True(Path.IsPathFullyQualified(resolved));

        var driveRelative = new MediaRef { Path = @"Q:clip.mp4" };
        string driveResolved = driveRelative.ResolvePath(@"Q:\Proyectos\Uno");
        Assert.Equal(@"Q:\Proyectos\Uno\clip.mp4", driveResolved);
        Assert.True(Path.IsPathFullyQualified(driveResolved));

        // Una ruta UNC ya es completa: no se le pega la unidad del proyecto.
        var unc = new MediaRef { Path = @"\\servidor\medios\c.mp4" };
        Assert.Equal(@"\\servidor\medios\c.mp4", unc.ResolvePath(@"Q:\Proyectos\Uno"));
    }
}
