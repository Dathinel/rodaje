using System.ComponentModel;
using System.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Packages.Download;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Updates;

namespace Rodaje.App.Shell;

/// <summary>
/// Aviso de versión nueva: al arrancar (como mucho una vez al día) y en Ayuda › Buscar actualizaciones…, con la barra
/// de avisos de la ventana. «Ver novedades» abre la página de la release y «Más tarde» calla esa versión.
/// </summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private bool _checkingUpdates;

    private void AttachUpdates() =>
        _bindings.Add(_router.Bind(ShellShortcuts.CheckUpdates, () => UiTask.Run(CheckForUpdatesAsync(manual: true))));

    /// <summary>Cuando la ventana ya se ve (no en las mediciones ni en la autoprueba, que no tocan la red).</summary>
    private void StartUpdateCheck()
    {
        if (!_options.IsAutomated)
        {
            UiTask.Run(CheckForUpdatesAsync(manual: false));
        }
    }

    /// <summary>
    /// Consulta fuera del hilo de la interfaz y avisa aquí. Al arrancar, un fallo o una versión descartada no dicen nada;
    /// a mano se responde siempre, también «tienes la última» y «no se pudo comprobar».
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingUpdates)
        {
            return;
        }

        _checkingUpdates = true;
        try
        {
            AppSettings settings = _settings.Current;
            var checker = new UpdateChecker(new GitHubReleaseSource(CatalogSource.AppVersion), CatalogSource.AppVersion);
            UpdateCheckResult result = await checker.CheckAsync(settings, manual).ConfigureAwait(true);
            if (_closed)
            {
                return;
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.Available when result.Release is { } release:
                    Session.Notify(NotificationSeverity.Informational, $"Rodaje {release.Version} disponible",
                        $"Tienes la {checker.Current}. La versión nueva se descarga desde la página de la release en GitHub.",
                        new NotificationAction("Ver novedades", () => OpenReleasePage(release)),
                        new NotificationAction("Más tarde", () => UpdateChecker.Dismiss(settings, release)));
                    break;
                case UpdateCheckStatus.UpToDate when manual:
                    Session.Notify(NotificationSeverity.Success, $"Tienes la última versión, {checker.Current}", "No hay ninguna versión más nueva publicada.");
                    break;
                case UpdateCheckStatus.Unavailable when manual:
                    Session.Notify(NotificationSeverity.Warning, "No se pudo comprobar", "No hay conexión o GitHub no respondió a tiempo. Inténtalo más tarde.");
                    break;
                default:
                    break;
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    private void OpenReleasePage(PublishedRelease release)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(release.Url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Session.Notify(NotificationSeverity.Warning, "No se pudo abrir el navegador", $"La página es {release.Url.AbsoluteUri} ({ex.Message}).");
        }
    }
}
