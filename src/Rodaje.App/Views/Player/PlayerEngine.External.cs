using System.Diagnostics;
using Rodaje.Render;

namespace Rodaje.App.Views.Player;

/// <summary>
/// «Visor en otra pantalla»: una segunda cadena de intercambio que recibe la misma composición que el visor, sin volver a
/// componer y sin esperar su propio vsync (si está ocupada, se salta ese cuadro).
/// </summary>
internal sealed partial class PlayerEngine
{
    private SizeRequest? _pendingExternal;
    private bool _closeExternal;
    private CompositionSwapChain? _external;

    /// <summary>La cadena de la otra pantalla existe: hay que darla a su panel desde el hilo de la interfaz. Llega desde el hilo de render.</summary>
    public event Action<nint>? ExternalSwapChainCreated;

    /// <summary>Abre (o cambia de tamaño) la salida a la otra pantalla, en píxeles físicos. Desde cualquier hilo.</summary>
    public void RequestExternalResize(int width, int height, float scaleX, float scaleY)
    {
        lock (_pendingLock)
        {
            _pendingExternal = new SizeRequest(width, height, scaleX, scaleY, Stopwatch.GetTimestamp());
            _closeExternal = false;
        }

        Wake();
    }

    /// <summary>Cierra la salida a la otra pantalla. Desde cualquier hilo.</summary>
    public void CloseExternal()
    {
        lock (_pendingLock)
        {
            _pendingExternal = null;
            _closeExternal = true;
        }

        Wake();
    }

    private void ApplyExternal()
    {
        SizeRequest? request;
        bool close;
        lock (_pendingLock)
        {
            request = _pendingExternal;
            close = _closeExternal;
            _pendingExternal = null;
            _closeExternal = false;
        }

        if (close)
        {
            _external?.Dispose();
            _external = null;
            return;
        }

        if (request is not { } size || _gpu is null)
        {
            return;
        }

        if (_external is null)
        {
            _external = new CompositionSwapChain(_gpu, size.Width, size.Height, size.ScaleX, size.ScaleY);
            ExternalSwapChainCreated?.Invoke(_external.NativeSwapChain);
        }
        else
        {
            _external.Resize(size.Width, size.Height, size.ScaleX, size.ScaleY);
        }

        _dirty = true;
    }

    private void PresentExternal()
    {
        if (_external is null || _session is null || _presenter is null)
        {
            return;
        }

        PixelRect placement = ViewerGeometry.Image(_external.Width, _external.Height, _session.Width, _session.Height, 0);
        _external.Render(_presenter, _compositor, placement, waitForVsync: false);
    }
}
