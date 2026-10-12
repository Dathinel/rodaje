using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// «Recuperar» cuando la apertura no sale porque la persona cancela el aviso de cambios sin guardar: no deja un
/// «(recuperado N)» huérfano por cada intento.
/// </summary>
public class AutosaveRecoveryCancelTests
{
    [Fact]
    public void Recuperar_CancelandoElAvisoDeCambios_NoDejaArchivosHuerfanos() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        RecoverableSession found = Assert.Single(h.FindRecoverable());
        Assert.True(h.Session.Execute(new RenameProjectCommand("Abierto")));

        // La persona cancela dos veces el aviso de cambios sin guardar.
        h.Dialogs.Choice = UnsavedChangesChoice.Cancel;
        Assert.False(await h.Service.RecoverAsync(found));
        Assert.False(await h.Service.RecoverAsync(found));

        Assert.Equal(2, h.Dialogs.Asked);
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*(recuperado*).rodaje"));
        Assert.Equal("Abierto", h.Session.ProjectName);

        // A la tercera descarta los cambios: se recupera con el primer nombre libre.
        h.Dialogs.Choice = UnsavedChangesChoice.Discard;
        Assert.True(await h.Service.RecoverAsync(found));

        Assert.Equal(h.Temp.File("Corto (recuperado).rodaje"), h.Session.FilePath);
        Assert.Single(Directory.GetFiles(h.Temp.Path, "*(recuperado*).rodaje"));
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void Recuperar_ProyectoNuncaGuardadoYAvisoCancelado_BorraLoEscrito() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(null, "Sin guardar");
        RecoverableSession found = Assert.Single(h.FindRecoverable());
        Assert.True(h.Session.Execute(new RenameProjectCommand("Abierto")));

        h.Dialogs.SavePath = h.Temp.File("Elegido");
        h.Dialogs.Choice = UnsavedChangesChoice.Cancel;
        Assert.False(await h.Service.RecoverAsync(found));

        Assert.False(File.Exists(h.Temp.File("Elegido.rodaje")));

        // La copia sigue en el autoguardado: el reintento vuelve a preguntar dónde y recupera.
        h.Dialogs.Choice = UnsavedChangesChoice.Discard;
        Assert.True(await h.Service.RecoverAsync(found));
        Assert.Equal(2, h.Dialogs.SavePicks);
        Assert.Equal(h.Temp.File("Elegido.rodaje"), h.Session.FilePath);
    });

    [Fact]
    public void Recuperar_SobreUnArchivoElegidoQueYaExistia_NoLoBorraSiSeCancela() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(null, "Sin guardar");
        RecoverableSession found = Assert.Single(h.FindRecoverable());
        Assert.True(h.Session.Execute(new RenameProjectCommand("Abierto")));

        // La persona eligió pisar un archivo que ya existía: es suyo, no se borra aunque la apertura no salga.
        string chosen = h.Temp.File("Existente.rodaje");
        File.WriteAllText(chosen, "");
        h.Dialogs.SavePath = chosen;
        h.Dialogs.Choice = UnsavedChangesChoice.Cancel;
        Assert.False(await h.Service.RecoverAsync(found));

        Assert.True(File.Exists(chosen));
    });
}
