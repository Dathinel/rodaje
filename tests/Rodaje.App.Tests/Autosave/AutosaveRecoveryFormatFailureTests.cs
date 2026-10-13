using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Una copia de autoguardado que se abre pero que el guardado rechaza: «Recuperar» avisa «No se pudo recuperar» en vez
/// de dejar escapar la <see cref="ProjectFormatException"/>, no deja archivos a medias y la copia se vuelve a ofrecer
/// (tarea 224).
/// </summary>
/// <remarks>
/// Pasa con un número que en el archivo desborda a infinito (<c>1e400</c>) en un parámetro que el validador no acota
/// (uno que el efecto no conoce): al abrir queda como <see cref="double.PositiveInfinity"/> y al guardar se rechaza.
/// </remarks>
public class AutosaveRecoveryFormatFailureTests
{
    private const string Marker = "7.25";

    [Fact]
    public void Recuperar_CopiaQueElGuardadoRechaza_AvisaYSeVuelveAOfrecer() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Session.Project.Sequences[0].MasterEffects.Add(new EffectInstance
        {
            Id = "fx-extra",
            Type = "rodaje.desconocido",
            Parameters = { ["extra"] = new Parameter { Value = 7.25 } },
        });
        string original = h.Temp.File("Corto.rodaje");
        string directory = h.CrashedSession(original, "Corto");
        string copy = AutosaveRecovery.ListSnapshots(directory).Single().Path;
        string text = File.ReadAllText(copy);
        Assert.Contains(Marker, text, StringComparison.Ordinal);
        File.WriteAllText(copy, text.Replace(Marker, "1e400", StringComparison.Ordinal));

        // La copia se abre (el desbordamiento queda como infinito), pero no se puede volver a guardar.
        Project loaded = ProjectSerializer.Load(copy);
        Assert.Equal(double.PositiveInfinity, loaded.Sequences[0].MasterEffects[0].Parameters["extra"].Value);
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Serialize(loaded));

        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.False(await h.Service.RecoverAsync(found));

        EditorNotification error = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Equal("No se pudo recuperar", error.Title);
        Assert.Contains("Corto (recuperado).rodaje", error.Message, StringComparison.Ordinal);
        Assert.Equal(EditorSession.UntitledName, h.Session.ProjectName);
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*.rodaje"));
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*" + ProjectSerializer.TempSuffix));

        // No se descartó: al soltarla, el próximo arranque la vuelve a ofrecer.
        found.Dispose();
        Assert.Single(h.FindRecoverable());
    });
}
