using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.Core.Commands;

namespace Rodaje.App.Views.Timeline;

/// <summary>Ctrl+T: la transición por defecto (<see cref="DefaultTransition"/>) en los cortes de la selección o en el más cercano al cursor.</summary>
internal static class TransitionCommands
{
    public static IDisposable Bind(ShortcutRouter router, EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(ActionIds.AddTransition, () => AddDefault(services));
    }

    /// <summary>Pone la transición por defecto; devuelve las que creó o cambió.</summary>
    public static IReadOnlyList<string> AddDefault(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        EditorSession session = services.Session;
        IReadOnlyList<string> cuts = DefaultTransition.Cuts(session.Sequence, session.SelectedClipIds, session.Position, session.MainTrack);
        if (cuts.Count == 0)
        {
            session.Notify(NotificationSeverity.Informational, "Transición por defecto",
                "No hay un corte entre dos clips pegados: selecciona un clip que toque a otro o acerca el cursor a un corte.");
            return [];
        }

        SequenceEditCommand command = DefaultTransition.Build(session.SequenceId, cuts, services.Settings.Current.Transitions);
        if (!session.Execute(command) || command.CreatedIds.Count == 0)
        {
            return [];
        }

        session.SelectTransition(command.CreatedIds[^1]);
        return command.CreatedIds;
    }
}
