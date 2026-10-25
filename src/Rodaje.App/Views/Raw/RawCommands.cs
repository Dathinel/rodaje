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

    // Como acción larga: barra de avance y «Cancelar» en la ventana mientras revela, y un fallo queda como aviso.
    private static void Run(EditorServices services) =>
        UiTask.Run(services.LongActions.RunAsync(ImportCinemaDng, CinemaDngDialog.Title, scope => CinemaDngDialog.ShowAsync(services, scope), canCancel: true));
}
