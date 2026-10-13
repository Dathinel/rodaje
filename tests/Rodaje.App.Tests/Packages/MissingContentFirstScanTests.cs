using System.Collections;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Packages;

namespace Rodaje.App.Tests.Packages;

/// <summary>
/// La comprobación de paquetes que faltan al abrir (<see cref="MissingContent.CheckAsync"/>) cuando todavía no terminó la
/// primera lectura de lo instalado (<see cref="PackageService.ScannedAsync"/>), tarea 261. La lectura no puede terminar
/// mientras la prueba no suelta el hilo de la interfaz, porque su continuación vuelve a él: no depende del reloj.
/// </summary>
public sealed class MissingContentFirstScanTests : IDisposable
{
    private const string MissingLut = "rodaje-filtros@1.0.0/lut-calido";

    private readonly TempDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Un_proyecto_reemplazado_durante_la_primera_lectura_no_avisa() => UiThread.Run(async () =>
    {
        PackageService packages = NewPackages();
        (EditorSession session, List<EditorNotification> warnings) = NewSession();
        Assert.True(await session.OpenProjectAsync(SaveProjectWithMissingLut()));
        Project replaced = session.Project;

        Task<IReadOnlyList<PackageReference>> check = MissingContent.CheckAsync(session, packages);
        Assert.False(check.IsCompleted);
        Task<bool> opening = session.NewProjectAsync();
        Assert.True(opening.IsCompletedSuccessfully);
        Assert.True(await opening);
        Assert.NotSame(replaced, session.Project);

        // El proyecto reemplazado sigue vivo (lo retiene la prueba): solo cuenta que ya no es el abierto.
        Assert.Empty(await check);
        Assert.Empty(warnings);
        Assert.True(packages.HasScanned);
        GC.KeepAlive(replaced);
    });

    [Fact]
    public void Sin_reemplazo_el_aviso_sale_al_terminar_la_primera_lectura() => UiThread.Run(async () =>
    {
        PackageService packages = NewPackages();
        (EditorSession session, List<EditorNotification> warnings) = NewSession();
        Assert.True(await session.OpenProjectAsync(SaveProjectWithMissingLut()));

        Task<IReadOnlyList<PackageReference>> check = MissingContent.CheckAsync(session, packages);
        Assert.False(check.IsCompleted);

        PackageReference missing = Assert.Single(await check);
        Assert.Equal(MissingLut, missing.ToString());
        EditorNotification notice = Assert.Single(warnings);
        Assert.Equal("Faltan paquetes", notice.Title);
        Assert.Contains("«rodaje-filtros»", notice.Message, StringComparison.Ordinal);
    });

    [Fact]
    public void Una_primera_lectura_fallida_se_reintenta_en_la_siguiente_llamada() => UiThread.Run(async () =>
    {
        var bundled = new FailingFolders();
        PackageService packages = NewPackages(bundled);
        (EditorSession session, List<EditorNotification> warnings) = NewSession();
        Assert.True(await session.OpenProjectAsync(SaveProjectWithMissingLut()));

        bundled.Failing = true;
        Task<PackageScan> first = packages.ScannedAsync();
        Assert.Same(first, packages.ScannedAsync());
        await Assert.ThrowsAsync<IOException>(() => first);
        await Assert.ThrowsAsync<IOException>(() => MissingContent.CheckAsync(session, packages));
        Assert.False(packages.HasScanned);
        Assert.Empty(warnings);

        // Antes, la lectura fallida quedaba guardada y cada llamada devolvía el mismo error para siempre.
        bundled.Failing = false;
        Task<PackageScan> second = packages.ScannedAsync();
        Assert.NotSame(first, second);
        await second;
        Assert.True(packages.HasScanned);

        PackageReference missing = Assert.Single(await MissingContent.CheckAsync(session, packages));
        Assert.Equal(MissingLut, missing.ToString());
        Assert.Single(warnings);
    });

    private PackageService NewPackages(IReadOnlyList<string>? bundled = null) =>
        new(PackageLocations.Under(Path.Combine(_folder.Path, "complementos"), bundled), PackageService.LoadBundledCatalog());

    private static (EditorSession Session, List<EditorNotification> Warnings) NewSession()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber(), new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator());
        var warnings = new List<EditorNotification>();
        session.Notified += (_, n) =>
        {
            if (n.Severity is NotificationSeverity.Warning or NotificationSeverity.Error)
            {
                warnings.Add(n);
            }
        };
        return (session, warnings);
    }

    private string SaveProjectWithMissingLut()
    {
        var track = new Track { Id = "v", Kind = TrackKind.Video };
        track.Effects.Add(new EffectInstance
        {
            Id = "e",
            Type = BuiltInEffects.Color,
            Options = new() { [BuiltInEffects.ColorOptions.Lut] = MissingLut },
        });
        string path = Path.Combine(_folder.Path, "p.rodaje");
        ProjectSerializer.Save(new Project { Id = "p", Name = "p", Sequences = [new Sequence { Id = "s", Name = "s", VideoTracks = [track] }] }, path);
        return path;
    }

    /// <summary>Carpetas de la distribución completa que no se pueden leer mientras <see cref="Failing"/> (un disco que se cae).</summary>
    private sealed class FailingFolders : IReadOnlyList<string>
    {
        public bool Failing { get; set; }

        public int Count => 0;

        public string this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator() =>
            Failing ? throw new IOException("Disco no disponible (prueba).") : Enumerable.Empty<string>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
