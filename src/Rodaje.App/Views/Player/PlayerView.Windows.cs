using Microsoft.UI.Dispatching;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App.Views.Player;

/// <summary>El visor en Windows: la cadena de intercambio del hilo de render va directa al <c>SwapChainPanel</c>.</summary>
internal sealed partial class PlayerView
{
    private bool _viewerClosed;

    private void AttachViewer(PlayerEngine engine, DispatcherQueue dispatcher) =>
        engine.SwapChainCreated += swapChain => dispatcher.RunLater(() =>
        {
            if (_viewerClosed)
            {
                return;
            }

            SwapChainPanelNative.SetSwapChain(VideoPanel, swapChain);
            Program.Metrics.Milestone("cadena_en_panel");
            UpdateHdrBadge();
        });

    /// <summary>Al cerrar la ventana: las cadenas que lleguen tarde no se enlazan y el panel suelta la que tiene.</summary>
    public void DetachViewer()
    {
        _viewerClosed = true;
        try
        {
            SwapChainPanelNative.SetSwapChain(VideoPanel, 0);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ObjectDisposedException or InvalidCastException)
        {
            // La ventana ya se desmonta: sin panel que desenlazar.
        }
    }

    private (float X, float Y) PanelScale() => (VideoPanel.CompositionScaleX, VideoPanel.CompositionScaleY);
}
