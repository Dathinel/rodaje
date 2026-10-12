using Rodaje.App.Services.Modules;
using Rodaje.Packages;
using Rodaje.Packages.Download;

namespace Rodaje.App.Services.Translate;

/// <summary>
/// Si el modelo del traductor está antes de abrir el módulo: los modelos de <c>module.models</c> del <c>paquete.json</c>
/// instalado se buscan con <see cref="ModelStore.Find"/> por su SHA-256 (en <c>complementos\modelos</c> y en
/// <c>modelos\</c> de la distribución completa) y por su ruta dentro de la carpeta del módulo.
/// </summary>
public static class TranslatorModel
{
    public const string Missing = "Falta el modelo del traductor: instálalo desde Complementos (IA → Traducir subtítulos).";

    /// <summary>
    /// <c>true</c> si falta algún modelo del módulo. Un módulo que corre desde el repositorio busca el suyo en
    /// <c>.entorno\modelos</c> y uno con <c>RODAJE_MODELOS</c> en esa carpeta: en esos casos (y si el manifiesto no se
    /// puede leer) no se adelanta nada y decide el módulo.
    /// </summary>
    public static bool IsMissing(ModuleDescriptor module, string storeFolder, IReadOnlyList<string> bundledFolders)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.Origin == ModuleOrigin.Development || Rodaje.Media.EnvironmentVariables.Read("RODAJE_MODELOS") is not null)
        {
            return false;
        }

        if (ManifestReader.ReadModels(Path.Combine(module.Directory, PackageManifest.FileName)) is not { } models)
        {
            return false;
        }

        using var http = new HttpClient();
        var store = new ModelStore(storeFolder, new FileDownloader(http), bundledFolders: bundledFolders);
        return models.Any(m => m.Find(store, module.Directory) is null);
    }

    /// <summary>Con las carpetas de siempre: <c>complementos\modelos</c> de los datos de la app y <c>modelos\</c> junto al exe.</summary>
    public static bool IsMissing(ModuleDescriptor module) =>
        IsMissing(module, Path.Combine(AppDataPaths.Root, "complementos", "modelos"), [Path.Combine(AppContext.BaseDirectory, "modelos")]);
}
