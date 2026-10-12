using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Views.Subtitles;

/// <summary>
/// «Subtítulos automáticos...» en el menú del clip, registrado con el id de la barra rápida
/// (<see cref="QuickTools.AutoSubtitles"/>) para que la barra, el menú y un atajo futuro lancen lo mismo. Sin atajo por omisión.
/// </summary>
internal static class SubtitleCommands
{
    public const string AutoSubtitles = QuickTools.AutoSubtitles;

    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(AutoSubtitles, () => AutoSubtitlesDialog.Run(services));
    }
}
