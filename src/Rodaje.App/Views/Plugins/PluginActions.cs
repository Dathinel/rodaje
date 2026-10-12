using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.Packages;

namespace Rodaje.App.Views.Plugins;

/// <summary>Instalar un <c>.paquete</c> con su aviso, desde la página de complementos o soltándolo en la biblioteca.</summary>
internal static class PluginActions
{
    public static async Task<bool> InstallAsync(EditorServices services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            InstallResult result = await services.Packages.InstallFromFileAsync(path);
            PackageManifest manifest = result.Package.Manifest;
            string what = result.Outcome switch
            {
                InstallOutcome.Updated => $"Se actualizó de la {result.PreviousVersion} a la {manifest.Version}.",
                InstallOutcome.Reinstalled => $"Se reinstaló la {manifest.Version}.",
                InstallOutcome.Downgraded => $"Se volvió a la {manifest.Version}.",
                _ => $"Versión {manifest.Version}, {Describe(manifest)}.",
            };
            string warnings = result.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", result.Warnings);
            services.Session.Notify(NotificationSeverity.Success, $"«{manifest.Name}» instalado", what + warnings);
            return true;
        }
        catch (Exception ex)
        {
            services.Session.Notify(NotificationSeverity.Error, $"No se instaló «{Path.GetFileName(path)}»", ex.Message);
            return false;
        }
    }

    public static string Describe(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Kind == PackageKind.Module)
        {
            return "módulo de IA";
        }

        int count = manifest.Items.Count;
        return count == 1 ? "1 elemento" : $"{count} elementos";
    }
}
