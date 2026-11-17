using Microsoft.UI.Dispatching;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App.Views.Player;

/// <summary>El visor en Windows: la cadena de intercambio del hilo de render va directa al <c>SwapChainPanel</c>.</summary>
internal sealed partial class PlayerView
{
    private void AttachViewer(PlayerEngine engine, DispatcherQueue dispatcher) =>
        engine.SwapChainCreated += swapChain => dispatcher.RunLater(() =>
        {
            SwapChainPanelNative.SetSwapChain(VideoPanel, swapChain);
            Program.Metrics.Milestone("cadena_en_panel");
            UpdateHdrBadge();
        });

    private (float X, float Y) PanelScale() => (VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
}
