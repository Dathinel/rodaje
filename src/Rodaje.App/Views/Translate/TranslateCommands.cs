using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Translate;

/// <summary>
/// «Traducir pista...»: la acción para el menú de la pista de subtítulos, Editar y la barra de herramientas.
/// Sin atajo por omisión.
/// </summary>
internal static class TranslateCommands
{
    public const string TranslateTrack = TranslateDialog.ActionId;
    public const string Label = "Traducir pista...";
    public const string Glyph = "";

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(TranslateTrack, () => Run(services));
    }

    private static async void Run(EditorServices services)
    {
        try
        {
            await TranslateDialog.ShowAsync(services);
        }
#pragma warning disable CA1031 // Un fallo del diálogo no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, "Traducir pista", ex.Message);
        }
    }
}
