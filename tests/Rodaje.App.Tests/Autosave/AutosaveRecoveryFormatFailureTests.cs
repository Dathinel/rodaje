using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Una copia de autoguardado con un número que en el archivo desborda a infinito (<c>1e400</c>) en un parámetro que el
/// validador no acota (uno que el efecto no conoce).
/// </summary>
/// <remarks>
/// Hasta la tarea 226 esa copia se abría (el número quedaba como <see cref="double.PositiveInfinity"/>) y era el guardado
/// de «Recuperar» el que la rechazaba (tarea 224). Ahora se rechaza al abrir, como cualquier copia dañada: si hay una
/// copia anterior se recupera esa, y si no, «Recuperar» avisa «No se pudo recuperar» y la copia se vuelve a ofrecer.
/// Ya no hay contenido de archivo que se abra y que el guardado rechace (NaN, infinitos y sustitutos sueltos se rechazan
/// al abrir), así que el <c>catch</c> de <see cref="ProjectFormatException"/> al escribir la copia queda como defensa.
/// </remarks>
public class AutosaveRecoveryFormatFailureTests
{
    private const string Marker = "7.25";

    [Fact]
    public void Recuperar_CopiaConUnNumeroQueDesborda_AvisaYSeVuelveAOfrecer() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        AddUnknownParameter(h);
        string original = h.Temp.File("Corto.rodaje");
        string directory = h.CrashedSession(original, "Corto");
        string copy = AutosaveRecovery.ListSnapshots(directory).Single().Path;
        Overflow(copy);

        // La copia ya no se abre como infinito: se rechaza diciendo dónde está el número.
        ProjectFormatException rejected = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Load(copy));
        Assert.Contains("«$.sequences[0].masterEffects[0].parameters.extra.value»", rejected.Message, StringComparison.Ordinal);

        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.False(await h.Service.RecoverAsync(found));

        EditorNotification error = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Equal("No se pudo recuperar", error.Title);
        Assert.Contains("parameters.extra.value", error.Message, StringComparison.Ordinal);
        Assert.Equal(EditorSession.UntitledName, h.Session.ProjectName);
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*.rodaje"));
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*" + ProjectSerializer.TempSuffix));

        // No se descartó: al soltarla, el próximo arranque la vuelve a ofrecer.
        found.Dispose();
        Assert.Single(h.FindRecoverable());
    });

    [Fact]
    public void Recuperar_UltimaCopiaConUnNumeroQueDesborda_UsaLaAnterior() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        AddUnknownParameter(h);
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Primera", "Segunda");
        Overflow(AutosaveRecovery.ListSnapshots(directory).Single(s => s.Number == 2).Path);
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.True(await h.Service.RecoverAsync(found));

        Assert.Equal("Primera", h.Session.ProjectName);
        Assert.Equal(7.25, h.Session.Project.Sequences[0].MasterEffects[0].Parameters["extra"].Value);
        EditorNotification done = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Success, done.Severity);
        Assert.Contains("copia n.º 1", done.Message, StringComparison.Ordinal);
    });

    private static void AddUnknownParameter(AutosaveHarness h) =>
        h.Session.Project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-extra",
            Type = "rodaje.desconocido",
            Parameters = { ["extra"] = new Parameter { Value = 7.25 } },
        });

    private static void Overflow(string copy)
    {
        string text = File.ReadAllText(copy);
        Assert.Contains(Marker, text, StringComparison.Ordinal);
        File.WriteAllText(copy, text.Replace(Marker, "1e400", StringComparison.Ordinal));
    }
}
