using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Translate;

/// <summary>
/// «Traducir pista...»: la acción del menú de análisis de la línea de tiempo. Corre como acción larga
/// (<see cref="EditorServices.LongActions"/>): el segundo clic no abre otro diálogo y un fallo se avisa. Sin atajo por omisión.
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

    private static void Run(EditorServices services) =>
        _ = services.LongActions.RunAsync(TranslateTrack, "Traducir pista", _ => TranslateDialog.ShowAsync(services));
}
