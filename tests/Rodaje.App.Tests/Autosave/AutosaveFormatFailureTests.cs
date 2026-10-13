using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Un proyecto que el serializador no puede escribir (un NaN en la ganancia maestra) no hace caer el autoguardado: se
/// avisa una sola vez aunque cada ciclo vuelva a fallar, no queda ningún <c>.tmp</c> y, al corregir el valor, las
/// copias siguen (tarea 218).
/// </summary>
public class AutosaveFormatFailureTests
{
    private const string FailureTitle = "No se pudo autoguardar";
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(20);

    [Fact]
    public void ProyectoConNaN_AvisaUnaVezYNoDejaTemporales() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        h.Session.Project.Sequences[0].MasterGainDb = double.NaN;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Con NaN")));

        for (int cycle = 0; cycle < 3; cycle++)
        {
            h.Clock.Advance(Gap);
            h.Service.Tick();
            await h.Drain();
        }

        EditorNotification warning = Assert.Single(h.Notifications);
        Assert.Equal(FailureTitle, warning.Title);
        Assert.Equal(NotificationSeverity.Warning, warning.Severity);
        Assert.Empty(h.Snapshots);
        Assert.Empty(Directory.GetFiles(h.Service.SessionDirectory!, "*" + ProjectSerializer.TempSuffix, SearchOption.AllDirectories));
    });

    [Fact]
    public void ProyectoConNaN_AlCorregirlo_VuelveACopiarYElAvisoSeRearma() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        h.Session.Project.Sequences[0].MasterGainDb = double.NaN;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Con NaN")));
        h.Clock.Advance(Gap);
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Notifications);

        h.Session.Project.Sequences[0].MasterGainDb = 0;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corregido")));
        h.Clock.Advance(Gap);
        h.Service.Tick();
        await h.Drain();

        AutosaveSnapshot copy = Assert.Single(h.Snapshots);
        Assert.Equal("Corregido", ProjectSerializer.Load(copy.Path).Name);

        h.Session.Project.Sequences[0].MasterGainDb = double.PositiveInfinity;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Con infinito")));
        h.Clock.Advance(Gap);
        h.Service.Tick();
        await h.Drain();

        Assert.Equal(2, h.Notifications.Count(n => n.Title == FailureTitle));
        Assert.Single(h.Snapshots);
    });
}
