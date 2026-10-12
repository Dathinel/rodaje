using Rodaje.App.Services;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Serialization;

namespace Rodaje.App.Tests.Autosave;

/// <summary>
/// Recuperación desde <see cref="AutosaveService"/>: la oferta al arrancar, «Recuperar» con copias dañadas o con
/// <c>sesion.json</c> corrupto, un bloqueo que no se deja borrar y el doble clic en el botón.
/// </summary>
public class AutosaveRecoveryServiceTests
{
    [Fact]
    public void Oferta_ConElBotonRecuperar_EscribeUnaCopiaJuntoAlOriginalYLaAbre() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();
        string original = h.Temp.File("Mi corto.rodaje");
        h.CrashedSession(original, "Mi corto");

        await h.Service.OfferRecoveryAsync();

        EditorNotification offer = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Warning, offer.Severity);
        Assert.Contains("Mi corto", offer.Message, StringComparison.Ordinal);
        Assert.Equal("Recuperar", offer.Action!.Text);

        offer.Action.Run();
        await WaitFor(() => h.Notifications.Any(n => n.Severity == NotificationSeverity.Success));

        string recovered = h.Temp.File("Mi corto (recuperado).rodaje");
        Assert.Equal(recovered, h.Session.FilePath);
        Assert.Equal("Mi corto", h.Session.ProjectName);
        Assert.False(File.Exists(original));
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void Oferta_SinSesionesCaidas_NoAvisa() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.Service.Start();

        await h.Service.OfferRecoveryAsync();

        Assert.Empty(h.Notifications);
    });

    [Fact]
    public void Recuperar_ConLaUltimaCopiaDanada_UsaLaAnterior() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Primera", "Segunda");
        File.WriteAllText(CopyPath(directory, 2), "{ dañada");
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.True(await h.Service.RecoverAsync(found));

        Assert.Equal("Primera", h.Session.ProjectName);
        EditorNotification done = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Success, done.Severity);
        Assert.Contains("copia n.º 1", done.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Recuperar_ConTodasLasCopiasDanadas_AvisaYSeVuelveAOfrecer() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Primera", "Segunda");
        File.WriteAllText(CopyPath(directory, 1), "no es json");
        File.WriteAllText(CopyPath(directory, 2), "{ dañada");
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Assert.False(await h.Service.RecoverAsync(found));

        EditorNotification error = Assert.Single(h.Notifications);
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Equal("No se pudo recuperar", error.Title);
        Assert.Equal(EditorSession.UntitledName, h.Session.ProjectName);
        Assert.Empty(Directory.GetFiles(h.Temp.Path, "*.rodaje"));

        // No se descartó: al soltarla, el próximo arranque la vuelve a ofrecer.
        found.Dispose();
        Assert.Single(h.FindRecoverable());
    });

    [Fact]
    public void Oferta_ConSesionJsonCorrupto_NoOfreceNiFalla() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        File.WriteAllText(Path.Combine(directory, AutosaveSession.InfoFileName), "{ \"unsavedChanges\": tr");

        await h.Service.OfferRecoveryAsync();

        Assert.Empty(h.Notifications);
        Assert.True(Directory.Exists(directory));
    });

    [Fact]
    public void Recuperar_ConSesionJsonCorruptoTrasLaOferta_RecuperaIgual() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        RecoverableSession found = Assert.Single(h.FindRecoverable());
        File.WriteAllText(Path.Combine(directory, AutosaveSession.InfoFileName), "\0\0\0");

        Assert.True(await h.Service.RecoverAsync(found));

        Assert.Equal(h.Temp.File("Corto (recuperado).rodaje"), h.Session.FilePath);
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void Recuperar_SiNoSePuedeBorrarElBloqueo_NoSeVuelveAOfrecer() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        string directory = h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        RecoverableSession found = Assert.Single(h.FindRecoverable());
        string lockPath = Path.Combine(directory, AutosaveSession.LockFileName);

        // Discard no logra borrar el bloqueo (un antivirus o un indexador lo tiene, por ejemplo).
        using (UndeletableLock.Block(lockPath))
        {
            Assert.True(await h.Service.RecoverAsync(found));
            Assert.True(Path.Exists(lockPath));
        }

        // Con el bloqueo otra vez en su sitio y libre, el próximo arranque no la ofrece: ya se recuperó.
        Assert.True(File.Exists(lockPath));
        Assert.Empty(h.FindRecoverable());
        Assert.False(AutosaveHarness.ReadUnsaved(directory));
    });

    [Fact]
    public void Recuperar_DobleClic_RecuperaUnaSolaVez() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        Task<bool> first = h.Service.RecoverAsync(found);
        Task<bool> second = h.Service.RecoverAsync(found);
        bool[] results = await Task.WhenAll(first, second);

        Assert.Equal([true, false], results);
        Assert.Equal(h.Temp.File("Corto (recuperado).rodaje"), h.Session.FilePath);
        Assert.False(File.Exists(h.Temp.File("Corto (recuperado 2).rodaje")));
        Assert.Single(h.Notifications, n => n.Severity == NotificationSeverity.Success);

        // Un clic más tarde, con el aviso ya resuelto, tampoco hace nada.
        Assert.False(await h.Service.RecoverAsync(found));
        Assert.Single(h.Notifications, n => n.Severity == NotificationSeverity.Success);
    });

    [Fact]
    public void Recuperar_DobleClicEnElBotonDelAviso_RecuperaUnaSolaVez() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(h.Temp.File("Corto.rodaje"), "Corto");
        await h.Service.OfferRecoveryAsync();
        NotificationAction recover = Assert.Single(h.Notifications).Action!;

        Task<EditorNotification> success = h.NotificationAsync(n => n.Severity == NotificationSeverity.Success);
        recover.Run();
        recover.Run();

        // El aviso de éxito es lo último de la primera recuperación, y el segundo clic se descarta antes de volver de
        // Run, así que al llegar el aviso ya no queda nada en curso.
        await success;

        Assert.Single(h.Notifications, n => n.Severity == NotificationSeverity.Success);
        Assert.Single(Directory.GetFiles(h.Temp.Path, "*(recuperado*).rodaje"));
    });

    [Fact]
    public void Recuperar_ProyectoNuncaGuardado_PreguntaDondeYSePuedeReintentar() => UiThread.Run(async () =>
    {
        using var h = new AutosaveHarness();
        h.CrashedSession(null, "Sin guardar");
        RecoverableSession found = Assert.Single(h.FindRecoverable());

        // La persona cancela el diálogo: no se recupera ni se descarta.
        h.Dialogs.SavePath = null;
        Assert.False(await h.Service.RecoverAsync(found));
        Assert.Equal(1, h.Dialogs.SavePicks);
        Assert.Empty(h.Notifications);

        h.Dialogs.SavePath = h.Temp.File("Elegido");
        Assert.True(await h.Service.RecoverAsync(found));

        Assert.Equal(2, h.Dialogs.SavePicks);
        Assert.Equal(h.Temp.File("Elegido.rodaje"), h.Session.FilePath);
        Assert.Empty(h.FindRecoverable());
    });

    [Fact]
    public void RecoveredPathNextTo_NoPisaLoQueExiste() => UiThread.Run(() =>
    {
        using var temp = new TempDirectory();
        string original = temp.File("Corto.rodaje");

        Assert.Equal(temp.File("Corto (recuperado).rodaje"), AutosaveService.RecoveredPathNextTo(original, "Corto"));
        File.WriteAllText(temp.File("Corto (recuperado).rodaje"), "");
        Assert.Equal(temp.File("Corto (recuperado 2).rodaje"), AutosaveService.RecoveredPathNextTo(original, "Corto"));
        Assert.Null(AutosaveService.RecoveredPathNextTo(null, "Corto"));
        Assert.Null(AutosaveService.RecoveredPathNextTo(temp.File(Path.Combine("no existe", "Corto.rodaje")), "Corto"));
    });

    private static string CopyPath(string sessionDirectory, int number) =>
        AutosaveRecovery.ListSnapshots(sessionDirectory).Single(s => s.Number == number).Path;

    private static async Task WaitFor(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "No pasó en 10 s.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Hace que <see cref="File.Delete"/> no pueda borrar el bloqueo mientras dura, sin abrirlo (la sesión recuperable
    /// lo tiene abierto sin compartir). En Windows lo marca de solo lectura; en Linux, donde la prueba corre como
    /// administrador y los permisos no frenan, pone una carpeta con algo dentro en su lugar. Al terminar, el bloqueo
    /// vuelve a ser un archivo normal que se puede abrir y borrar.
    /// </summary>
    private sealed class UndeletableLock : IDisposable
    {
        private readonly string _path;

        private UndeletableLock(string path) => _path = path;

        public static UndeletableLock Block(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
            else
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "ocupado"), "");
            }

            return new UndeletableLock(path);
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(_path, FileAttributes.Normal);
            }
            else
            {
                Directory.Delete(_path, recursive: true);
                File.WriteAllText(_path, "0");
            }
        }
    }
}
