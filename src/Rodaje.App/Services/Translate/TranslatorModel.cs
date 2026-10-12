using Rodaje.App.Services.Modules;
using Rodaje.Packages;
using Rodaje.Packages.Download;

namespace Rodaje.App.Services.Translate;

/// <summary>
/// Si el modelo del traductor está antes de abrir el módulo: los de <c>module.models</c> del <c>paquete.json</c> instalado
/// tienen que estar en la carpeta del módulo con su tamaño exacto, donde los deja «Bajar modelos» en Complementos.
/// </summary>
public static class TranslatorModel
{
    public const string Missing = "Falta el modelo del traductor. Bájalo desde Complementos › Módulos de IA («Bajar modelos»).";

    /// <summary>
    /// <c>true</c> si a un módulo instalado desde Complementos le falta algún modelo. Uno de la distribución completa o
    /// del repositorio busca los suyos en otras carpetas (<c>modelos\</c>, <c>.entorno\modelos</c>): ahí decide el módulo.
    /// </summary>
    public static bool IsMissing(ModuleDescriptor module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return module.Origin == ModuleOrigin.Installed
            && ModuleModels.Read(Path.Combine(module.Directory, PackageManifest.FileName)).Any(m => !m.IsPresentIn(module.Directory));
    }
}
