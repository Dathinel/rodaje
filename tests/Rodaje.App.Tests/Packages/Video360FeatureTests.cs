using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Xunit;

namespace Rodaje.App.Tests.Packages;

/// <summary>El reencuadre 360 viene en el motor, pero la interfaz solo lo ofrece con el complemento <c>rodaje-360</c> instalado.</summary>
[Collection(nameof(EffectFeaturesGroup))]
public sealed class Video360FeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-360-app", Guid.NewGuid().ToString("N")[..8]);
    private readonly IReadOnlySet<string> _before = EffectCatalog.EnabledFeatures;

    [Fact]
    public void Installing_rodaje_360_offers_the_reframe_and_uninstalling_hides_it()
    {
        PackageScan without = Scan(install: false);
        EffectCatalog.EnabledFeatures = EffectCatalog.FeaturesFrom(without);
        Assert.DoesNotContain(EffectCatalog.For(TrackKind.Video), e => e.Type == BuiltInEffects.Reframe360);
        Assert.DoesNotContain(LibraryElements.BuiltIn(LibraryCategory.Effects), e => e.Reference == BuiltInEffects.Reframe360);
        Assert.False(EffectCatalog.IsOffered(BuiltInEffects.Reframe360));
        Assert.True(EffectCatalog.IsOffered(BuiltInEffects.Mirror));

        PackageScan with = Scan(install: true);
        EffectCatalog.EnabledFeatures = EffectCatalog.FeaturesFrom(with);
        Assert.Equal([EffectFeatures.Video360], EffectCatalog.EnabledFeatures);
        Assert.Contains(EffectCatalog.For(TrackKind.Video), e => e.Type == BuiltInEffects.Reframe360);
        LibraryElement element = Assert.Single(LibraryElements.BuiltIn(LibraryCategory.Effects), e => e.Reference == BuiltInEffects.Reframe360);
        Assert.Equal("Reencuadre 360", element.Name);
        Assert.Equal("Proyección", EffectUi.OptionLabel(BuiltInEffects.Reframe360Options.Projection));
        Assert.Equal("Pequeño planeta", EffectUi.ChoiceName(BuiltInEffects.Reframe360Options.Projection, "stereographic"));
    }

    public void Dispose()
    {
        EffectCatalog.EnabledFeatures = _before;
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private PackageScan Scan(bool install)
    {
        string folder = Path.Combine(_root, install ? "con" : "sin");
        string package = Path.Combine(folder, "p", EffectCatalog.Video360Package);
        if (install)
        {
            Directory.CreateDirectory(Path.Combine(package, "efectos"));
            File.WriteAllText(Path.Combine(package, "efectos", "frente.json"), """
                { "format": "rodaje.effect", "version": 1, "name": "Mirar al frente", "anchor": "whole", "durationSeconds": 1,
                  "effects": [ { "type": "rodaje.reframe360", "parameters": { "fov": 90 } } ] }
                """);
            File.WriteAllText(Path.Combine(package, "paquete.json"), """
                { "formatVersion": 1, "id": "rodaje-360", "version": "1.0.0", "name": "Video 360", "author": "Dathinel", "license": "CC0-1.0",
                  "apps": ["rodaje"], "items": [ { "id": "frente", "kind": "effect", "name": "Mirar al frente", "file": "efectos/frente.json" } ] }
                """);
        }

        return PackageLibrary.Scan(new PackageLocations(Path.Combine(folder, "p"), Path.Combine(folder, "m"), Path.Combine(folder, "propios"), []), "rodaje");
    }
}

[CollectionDefinition(nameof(EffectFeaturesGroup), DisableParallelization = true)]
public sealed class EffectFeaturesGroup;
