using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Raw;

/// <summary>«Importar CinemaDNG…» en Archivo. Sin atajo por omisión.</summary>
internal static class RawCommands
{
    public const string ImportCinemaDng = "file.importCinemaDng";

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(ImportCinemaDng, () => Run(services));
    }

    private static void Run(EditorServices services) => UiTask.Run(RunAsync(services));

    private static async Task RunAsync(EditorServices services)
    {
        try
        {
            await CinemaDngDialog.ShowAsync(services);
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UiTask.Report(ex, notify: false);
            services.Session.Notify(NotificationSeverity.Error, "Importar CinemaDNG", ex.Message);
        }
    }
}
