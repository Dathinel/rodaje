using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Packages;
using Rodaje.App.Shell;
using Rodaje.Packages;

namespace Rodaje.App.Views.Plugins;

/// <summary>Instalar un <c>.paquete</c> con su aviso, desde la página de complementos o soltándolo en la biblioteca.</summary>
internal static class PluginActions
{
    public static async Task<bool> InstallAsync(EditorServices services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Comprobar y copiar un paquete grande tarda: la barra de avisos lo dice (no mientras se espera la respuesta del diálogo).
        LongActionScope? scope = Begin();
        LongActionScope? Begin() => services.LongActions.TryBegin(
            "paquete.instalar", "Instalando el paquete", "Comprobando y copiando «" + Path.GetFileName(path) + "»", showAfter: LongActionService.QuickOperationDelay);
        try
        {
            LocalPackageInstall installation = await services.Packages.InstallLocalFileAsync(path, async () =>
            {
                scope?.Dispose();
                scope = null;
                var dialog = new ContentDialog
                {
                    XamlRoot = services.Shell.XamlRoot,
                    Title = "¿Instalar este programa?",
                    Content = "El paquete contiene un programa que se ejecutará. No está en el catálogo firmado. Instálalo solo si confías en el origen.",
                    PrimaryButtonText = "Instalar",
                    SecondaryButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Secondary,
                };
                bool approved = await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
                scope = approved ? Begin() : null;
                return approved;
            });
            if (installation.Outcome != LocalInstallOutcome.Installed) return false;
            InstallResult result = installation.Result!;
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
        finally
        {
            scope?.Dispose();
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
