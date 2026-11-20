using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Views.Inspector;

namespace Rodaje.App.Views.Player;

/// <summary>
/// El visor en Linux: Uno no muestra cadenas de intercambio, así que el hilo de render deja cada cuadro en la CPU
/// (<see cref="PlayerEngine.TakeViewerFrame"/>) y aquí se copia a un <see cref="WriteableBitmap"/> dentro del panel.
/// </summary>
internal sealed partial class PlayerView
{
    private readonly Image _viewerImage = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private WriteableBitmap? _viewerBitmap;
    private int _viewerQueued;

    private void AttachViewer(PlayerEngine engine, DispatcherQueue dispatcher)
    {
        VideoPanel.Children.Add(_viewerImage);
        // Uno enlaza los x:Bind al cargarse el control: un Bindings.Update de antes (el de Attach) no llega al tiempo ni al transporte.
        Loaded += (_, _) => Bindings.Update();
        engine.ViewerFrameReady += () =>
        {
            // Uno pendiente a la vez: si la interfaz va atrasada, pinta el último cuadro y no los intermedios.
            if (Interlocked.Exchange(ref _viewerQueued, 1) == 0 && !dispatcher.RunLater(() => ShowViewerFrame(engine)))
            {
                Volatile.Write(ref _viewerQueued, 0);
            }
        };
    }

    public void DetachViewer()
    {
    }

    private void ShowViewerFrame(PlayerEngine engine)
    {
        Volatile.Write(ref _viewerQueued, 0);
        if (engine.TakeViewerFrame() is not { } frame)
        {
            return;
        }

        if (_viewerBitmap is null || _viewerBitmap.PixelWidth != frame.Width || _viewerBitmap.PixelHeight != frame.Height)
        {
            _viewerBitmap = new WriteableBitmap(frame.Width, frame.Height);
            _viewerImage.Source = _viewerBitmap;
            Program.Metrics.Milestone("cadena_en_panel");
        }

        using (Stream pixels = _viewerBitmap.PixelBuffer.AsStream())
        {
            pixels.Write(frame.Bgra);
        }

        _viewerBitmap.Invalidate();
    }

    private (float X, float Y) PanelScale()
    {
        float scale = (float)(XamlRoot?.RasterizationScale ?? 1);
        return (scale, scale);
    }
}
