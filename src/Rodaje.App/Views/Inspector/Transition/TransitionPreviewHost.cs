using System.Runtime.CompilerServices;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>Una caché de miniaturas de transiciones por ventana: la comparten el inspector y la galería.</summary>
internal static class TransitionPreviewHost
{
    private static readonly ConditionalWeakTable<EditorServices, TransitionPreviews> Previews = [];

    public static TransitionPreviews For(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Previews.GetValue(services, s =>
        {
            var previews = new TransitionPreviews(s.Player.RenderTransitionAsync, () => s.Content);
            s.Packages.Changed += (_, _) => previews.Clear();
            return previews;
        });
    }
}
