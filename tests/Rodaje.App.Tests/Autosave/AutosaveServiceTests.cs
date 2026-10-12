using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Escritura de <see cref="Rodaje.App.Services.Autosave.AutosaveService"/>: cuándo copia (<c>Tick</c> con un reloj
/// falso), el orden de la fila de escrituras y lo que anota al guardar, al deshacer hasta lo guardado y al abrir otro
/// proyecto.
/// </summary>
public class AutosaveServiceTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(20);

    [Fact]
    public void Start_CreaLaCarpetaDeLaSesionConSuBloqueo() => UiThread.Run(() =>
    {
        using var h = new AutosaveHarness();

        Assert.True(h.Service.Start());

        Assert.True(h.Service.IsActive);
        Assert.True(File.Exists(Path.Combine(h.Service.SessionDirectory!, AutosaveSession.LockFileName)));
        Assert.True(h.Service.Start());
    });

    [Fact]
    public void Tick_SinCambios_NoEscribe() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();

        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Service.Tick();
        await h.Drain();

        Assert.Empty(h.Snapshots);
    });

    [Fact]
    public void Tick_EsperaLaCalmaYEscribeUnaCopia() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Service.Tick();
        await h.Drain();
        Assert.Empty(h.Snapshots);

        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();

        AutosaveSnapshot copy = Assert.Single(h.Snapshots);
        Assert.Equal("Corto", ProjectSerializer.Load(copy.Path).Name);
        Assert.True(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));
        Assert.Empty(h.Notifications);
    });

    [Fact]
    public void Tick_DuranteUnGesto_EsperaAQueTermine() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Session.BeginGesture("Arrastrar");

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Service.Tick();
        await h.Drain();
        Assert.Empty(h.Snapshots);

        h.Session.CancelGesture();
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Snapshots);
    });

    [Fact]
    public void Tick_RespetaElTiempoMinimoEntreCopias() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Primera")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();

        Assert.True(h.Session.Execute(new RenameProjectCommand("Segunda")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Snapshots);

        h.Clock.Advance(Gap);
        h.Service.Tick();
        await h.Drain();

        Assert.Equal(["Segunda", "Primera"], h.Snapshots.Select(s => ProjectSerializer.Load(s.Path).Name));
    });

    [Fact]
    public void Tick_SinStart_NoEscribeNada() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        h.Service.Tick();
        await h.Drain();

        Assert.False(h.Service.IsActive);
        Assert.False(Directory.Exists(h.Root));
    });

    [Fact]
    public void Guardar_TrasUnaCopiaEnLaFila_QuedaSinNadaQueOfrecer() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        string directory = h.Service.SessionDirectory!;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Clock.Advance(Quiet);

        // La copia va a la fila y, sin esperarla, se guarda: MarkSaved tiene que ir detrás de la copia.
        h.Service.Tick();
        Assert.True(await h.Session.SaveAsAsync(h.Temp.File("Corto.rodaje")));
        await h.Drain();

        Assert.Single(h.Snapshots);
        Assert.False(AutosaveHarness.ReadUnsaved(directory));

        // Un cierre inesperado ahora no ofrece nada.
        h.Service.Dispose();
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void Guardar_AnotaQueNoHayCambiosYDejaLasCopiasComoHistorial() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();
        Assert.True(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));

        Assert.True(await h.Session.SaveAsAsync(h.Temp.File("Corto.rodaje")));
        await h.Drain();

        Assert.False(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));
        Assert.Single(h.Snapshots);

        // Ya no hay nada pendiente: el reloj puede seguir sin que se escriban copias.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Snapshots);
    });

    [Fact]
    public void DeshacerHastaLoGuardado_AnotaQueNoHayCambios() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(await h.Session.SaveAsAsync(h.Temp.File("Corto.rodaje")));
        await h.Drain();

        Assert.True(h.Session.Execute(new RenameProjectCommand("Otro nombre")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();
        Assert.True(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));

        Assert.True(h.Session.Undo());
        Assert.False(h.Session.IsDirty);
        await h.Drain();

        Assert.False(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));
        h.Service.Dispose();
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void AbrirOtroProyecto_BorraLasCopiasDelAnterior() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Anterior")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();
        Assert.Single(h.Snapshots);

        h.Dialogs.Choice = UnsavedChangesChoice.Discard;
        Assert.True(await h.Session.NewProjectAsync());
        await h.Drain();

        Assert.Empty(h.Snapshots);
        Assert.False(AutosaveHarness.ReadUnsaved(h.Service.SessionDirectory!));

        // El proyecto nuevo, sin cambios, no se copia.
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        h.Service.Tick();
        await h.Drain();
        Assert.Empty(h.Snapshots);
    });

    [Fact]
    public void Complete_BorraLaCarpetaDeLaSesion() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        string directory = h.Service.SessionDirectory!;
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();

        h.Service.Complete();

        Assert.False(h.Service.IsActive);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void Dispose_ConCambiosSinGuardar_SeOfreceAlSiguienteArranque() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        Assert.True(h.Session.Execute(new RenameProjectCommand("Corto")));
        h.Clock.Advance(Quiet);
        h.Service.Tick();
        await h.Drain();

        h.Service.Dispose();

        RecoverableSession found = Assert.Single(h.FindRecoverable());
        Assert.Equal("Corto", found.ProjectName);
    });
}
