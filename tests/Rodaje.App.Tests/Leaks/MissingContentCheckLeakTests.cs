using System.Runtime.CompilerServices;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// La comprobación de paquetes que faltan al abrir un proyecto (<see cref="MissingContent"/>) espera a la primera lectura
/// de lo instalado. Mientras esa lectura no terminaba, cada proyecto abierto quedaba retenido por su comprobación
/// pendiente y encolaba otra lectura completa del disco detrás de las demás. Con el disco o el pool lentos es la causa
/// más probable de los 90 proyectos vivos de 200 que vio <see cref="SessionOpenCloseLeakTests"/> en la CI de Windows
/// (tarea 250). Aquí la lectura no puede terminar mientras se mide: espera detrás de otra operación con los paquetes que
/// la prueba retiene. No depende del reloj.
/// </summary>
[Collection(AppLeakGroup.Name)]
public sealed class MissingContentCheckLeakTests : IDisposable
{
    private const int Openings = 50;

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Projects_opened_before_the_first_package_scan_are_not_kept_alive() => UiThread.Run(async () =>
    {
        PackageService packages = NewPackages();
        var playback = new FakePlayback();
        var session = new EditorSession(playback, new FakeProber(), new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator());
        MissingContent.Attach(session, packages);

        // Una desinstalación que no termina hasta que la prueba la suelta ocupa los paquetes: la primera lectura queda
        // esperando su turno. Antes la prueba confiaba en que la continuación de esa lectura volviera al hilo de la
        // interfaz, pero si el pool la terminaba antes de que el await mirara la tarea (el hilo de la interfaz desalojado
        // en un runner cargado), seguía en el acto y HasScanned ya era verdadero (CI de Windows de #246).
        var occupied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        packages.CloseModule = _ =>
        {
            entered.TrySetResult();
            return occupied.Task;
        };
        Task uninstalling = packages.UninstallAsync("paquete-que-no-esta");
        await entered.Task;

        // Todo en el mismo tramo del hilo de la interfaz: ninguna comprobación puede terminar entretanto.
        var opened = new List<WeakReference<Project>>();
        for (int i = 0; i < Openings; i++)
        {
            Task<bool> opening = session.NewProjectAsync();
            Assert.True(opening.IsCompletedSuccessfully);
            Assert.True(await opening);
            Remember(opened, session.Project);
        }

        Assert.False(packages.HasScanned);
        playback.Loads.Clear();
        playback.Reloads.Clear();
        CollectEverything();
        int alive = opened.Count(w => w.TryGetTarget(out _));

        // El proyecto abierto y, como mucho, el anterior. Antes, los cincuenta.
        Assert.True(alive <= 2, $"Siguen vivos {alive} de {Openings} proyectos abiertos antes de la primera lectura de paquetes.");

        // La lectura que esperan las comprobaciones, y no la que hace la desinstalación al terminar.
        Task<PackageScan> firstScan = packages.ScannedAsync();
        occupied.SetResult();
        // No hay tal paquete: la desinstalación falla después de soltar los paquetes, sin tocar el disco.
        await Assert.ThrowsAsync<PackageInstallException>(() => uninstalling);
        await firstScan;
        Assert.True(packages.HasScanned);
    });

    [Fact]
    public void Checks_waiting_for_the_first_scan_share_one_read_of_the_disk() => UiThread.Run(async () =>
    {
        PackageService packages = NewPackages();
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs(), new SequentialIdGenerator());
        int scans = 0;
        packages.Changed += (_, _) => scans++;

        Task<IReadOnlyList<PackageReference>>[] checks = [.. Enumerable.Range(0, 20).Select(_ => MissingContent.CheckAsync(session, packages))];
        await Task.WhenAll(checks);

        // Antes, una lectura completa por comprobación, una detrás de otra.
        Assert.Equal(1, scans);
        Assert.All(checks, c => Assert.Empty(c.Result));

        // Ya leído, no se vuelve a leer; RefreshAsync sí relee.
        await MissingContent.CheckAsync(session, packages);
        Assert.Equal(1, scans);
        await packages.RefreshAsync();
        Assert.Equal(2, scans);
    });

    private PackageService NewPackages() =>
        new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos")), PackageService.LoadBundledCatalog());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Remember(List<WeakReference<Project>> opened, Project project) => opened.Add(new WeakReference<Project>(project));

    private static void CollectEverything()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
