namespace Rodaje.App.Shell;

internal sealed partial class MainWindow
{
    /// <summary>El render en caché de la ventana (para la autoprueba).</summary>
    internal Services.RenderCache.RenderCacheHost RenderCache => _renderCache;

    /// <summary>La línea de tiempo (para la autoprueba).</summary>
    internal Views.Timeline.TimelineView TimelinePane => Timeline;
}
