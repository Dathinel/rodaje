using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// <see cref="AutosaveService.RecoveredPathNextTo"/>: el destino propuesto para «Recuperar» tiene que poder guardarse,
/// aunque en la carpeta haya una carpeta con ese nombre o el nombre salga del proyecto y no de un archivo.
/// </summary>
public class AutosaveRecoveredPathTests
{
    [Fact]
    public void UnaCarpetaConElNombreDelDestino_SeSaltaComoUnArchivo()
    {
        using var temp = new TempDirectory();
        string original = temp.File("Corto.rodaje");
        Directory.CreateDirectory(temp.File("Corto (recuperado).rodaje"));

        // Antes devolvía la carpeta: el guardado de la recuperación fallaba siempre y la copia se volvía a ofrecer.
        Assert.Equal(temp.File("Corto (recuperado 2).rodaje"), AutosaveService.RecoveredPathNextTo(original, "Corto"));
    }

    [Fact]
    public void SinNombreDeArchivo_ElNombreDelProyectoSeLimpiaYNoSaleDeLaCarpeta()
    {
        using var temp = new TempDirectory();
        string original = temp.File(".rodaje");

        string? target = AutosaveService.RecoveredPathNextTo(original, "Mi/corto: final?. ");

        Assert.Equal(temp.File("Mi_corto_ final_ (recuperado).rodaje"), target);
    }

    [Fact]
    public void SinNombreDeArchivo_UnNombreReservadoDeWindowsNoSeUsaTalCual()
    {
        using var temp = new TempDirectory();
        string original = temp.File(".rodaje");

        Assert.Equal(temp.File("_CON.v2 (recuperado).rodaje"), AutosaveService.RecoveredPathNextTo(original, "CON.v2"));
        Assert.Equal(temp.File("Proyecto (recuperado).rodaje"), AutosaveService.RecoveredPathNextTo(original, " .. "));
    }
}
