using Rodaje.App.Services.SafeZones;
using Rodaje.App.Views.Player;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Tests.Player;

/// <summary>Las zonas seguras del visor: las de fábrica, las de paquetes y propias, y el editor de rectángulos.</summary>
public class SafeZonesTests
{
    [Fact]
    public void LasDeFabricaSonDatosValidosConLosClasicosPrimero()
    {
        IReadOnlyList<SafeZoneChoice> builtIn = SafeZoneCatalog.BuiltIn;
        Assert.Equal(SafeZoneCatalog.Classic, builtIn[0].Reference);
        Assert.True(builtIn[0].Preset.Center);
        Assert.All(builtIn[0].Preset.Zones, z => Assert.Equal(SafeZoneKind.Safe, z.Kind));
        Assert.Equal(["Vertical corto con barra lateral (9:16)", "Vertical corto con pie amplio (9:16)", "Vertical corto con botones abajo (9:16)", "Vertical corto con pie y lateral (9:16)", "Historia vertical (9:16)", "Horizontal con controles (16:9)"],
            builtIn.Skip(1).Select(c => c.Name));
        foreach (SafeZoneChoice network in builtIn.Skip(1))
        {
            Assert.Equal(SafeZoneOrigin.BuiltIn, network.Origin);
            Assert.NotEqual("", network.Preset.AspectText);
            Assert.All(network.Preset.Zones, z =>
            {
                Assert.Equal(SafeZoneKind.Interface, z.Kind);
                Assert.True(z.Left + z.Width <= 1 + 1e-9 && z.Top + z.Height <= 1 + 1e-9);
            });
        }

        Assert.True(SafeZoneCatalog.Find(builtIn, "fabrica/tiktok").Preset.Fits(1080, 1920));
        Assert.True(SafeZoneCatalog.Find(builtIn, "fabrica/youtube").Preset.Fits(3840, 2160));
    }

    [Fact]
    public void UnaReferenciaQueYaNoExisteVuelveALosClasicos()
    {
        Assert.Equal(SafeZoneCatalog.Classic, SafeZoneCatalog.Find(SafeZoneCatalog.BuiltIn, "otro@1.0.0/nada").Reference);
        Assert.Equal(SafeZoneCatalog.Classic, SafeZoneCatalog.Find(SafeZoneCatalog.BuiltIn, null).Reference);
    }

    [Fact]
    public void GuardarUnoPropioLoPoneEnLaListaYSePuedeReescribir()
    {
        string root = Path.Combine(Path.GetTempPath(), "rodaje-zonas-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            PackageLocations locations = PackageLocations.Under(root);
            var preset = new SafeZonePreset("", "Mi canal", 9, 16, [new SafeZone("Logo", SafeZoneKind.Interface, 0.8, 0.02, 0.18, 0.08)], false);

            string reference = SafeZoneCatalog.SaveOwn(locations.Own, preset);
            string again = SafeZoneCatalog.SaveOwn(locations.Own, preset);
            Assert.Equal("propios@0.0.0/mi-canal", reference);
            Assert.Equal("propios@0.0.0/mi-canal-2", again);

            PackageScan scan = PackageLibrary.Scan(locations, "rodaje");
            var problems = new List<string>();
            IReadOnlyList<SafeZoneChoice> all = SafeZoneCatalog.All(scan, problems);
            Assert.Empty(problems);
            SafeZoneChoice mine = SafeZoneCatalog.Find(all, reference);
            Assert.Equal((SafeZoneOrigin.Own, "Mi canal", "Logo"), (mine.Origin, mine.Name, mine.Preset.Zones[0].Label));

            string file = SafeZoneCatalog.OwnFile(scan, reference)!;
            SafeZoneCatalog.SaveOwn(locations.Own, preset with { Zones = [.. preset.Zones, SafeZoneEditing.NewZone(1)] }, file);
            all = SafeZoneCatalog.All(PackageLibrary.Scan(locations, "rodaje"));
            Assert.Equal(2, SafeZoneCatalog.Find(all, reference).Preset.Zones.Count);
            Assert.Null(SafeZoneCatalog.OwnFile(scan, SafeZoneCatalog.Classic));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ElPunteroEligeEsquinaAntesQueCuerpoYLaDeEncima()
    {
        SafeZone[] zones =
        [
            new("A", SafeZoneKind.Interface, 0.1, 0.1, 0.5, 0.5),
            new("B", SafeZoneKind.Interface, 0.4, 0.4, 0.3, 0.3),
        ];
        Assert.Equal((1, SafeZoneHandle.Body), SafeZoneEditing.Hit(zones, 0.5, 0.5, 0.01, 0.01));
        Assert.Equal((0, SafeZoneHandle.Body), SafeZoneEditing.Hit(zones, 0.2, 0.2, 0.01, 0.01));
        Assert.Equal((0, SafeZoneHandle.TopLeft), SafeZoneEditing.Hit(zones, 0.105, 0.095, 0.01, 0.01));
        Assert.Equal((1, SafeZoneHandle.BottomRight), SafeZoneEditing.Hit(zones, 0.7, 0.7, 0.01, 0.01));
        Assert.Equal((0, SafeZoneHandle.BottomRight), SafeZoneEditing.Hit(zones, 0.6, 0.6, 0.01, 0.01));
        Assert.Equal((-1, SafeZoneHandle.None), SafeZoneEditing.Hit(zones, 0.9, 0.05, 0.01, 0.01));
    }

    [Fact]
    public void ArrastrarNoSacaLaZonaDeLaImagenNiLaDejaSinTamano()
    {
        var zone = new SafeZone("A", SafeZoneKind.Interface, 0.2, 0.2, 0.3, 0.1);

        SafeZone moved = SafeZoneEditing.Drag(zone, SafeZoneHandle.Body, 0.9, -0.5);
        Assert.Equal((0.7, 0.0, 0.3, 0.1), (moved.Left, moved.Top, moved.Width, moved.Height), Tolerance);

        SafeZone grown = SafeZoneEditing.Drag(zone, SafeZoneHandle.BottomRight, 0.1, 2);
        Assert.Equal((0.2, 0.2, 0.4, 0.8), (grown.Left, grown.Top, grown.Width, grown.Height), Tolerance);

        SafeZone crushed = SafeZoneEditing.Drag(zone, SafeZoneHandle.TopLeft, 0.5, 0.5);
        Assert.Equal((0.5 - SafeZoneEditing.MinSize, 0.3 - SafeZoneEditing.MinSize), (crushed.Left, crushed.Top), PairTolerance);
        Assert.Equal(SafeZoneEditing.MinSize, crushed.Width, 9);
        Assert.Equal(0.5, crushed.Left + crushed.Width, 9);

        SafeZone left = SafeZoneEditing.Drag(zone, SafeZoneHandle.BottomLeft, -1, 0);
        Assert.Equal((0.0, 0.5), (left.Left, left.Width), PairTolerance);
        Assert.Equal("A", left.Label);
    }

    [Theory]
    [InlineData(1080, 1920, 9, 16)]
    [InlineData(1920, 1080, 16, 9)]
    [InlineData(1080, 1350, 4, 5)]
    [InlineData(1000, 1001, 0, 0)]
    public void LaFormaDelProyectoSeReduce(int width, int height, int w, int h) =>
        Assert.Equal((w, h), SafeZoneEditing.Aspect(width, height));

    private static readonly TupleComparer Tolerance = new();
    private static readonly PairComparer PairTolerance = new();

    private sealed class TupleComparer : IEqualityComparer<(double, double, double, double)>
    {
        public bool Equals((double, double, double, double) a, (double, double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9 && Math.Abs(a.Item3 - b.Item3) < 1e-9 && Math.Abs(a.Item4 - b.Item4) < 1e-9;

        public int GetHashCode((double, double, double, double) obj) => 0;
    }

    private sealed class PairComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9;

        public int GetHashCode((double, double) obj) => 0;
    }
}
