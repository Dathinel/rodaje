using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Render;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Views.Player;

/// <summary>El visor en Windows: D3D11 y una cadena de intercambio de composición en el <c>SwapChainPanel</c>.</summary>
internal sealed partial class PlayerEngine
{
    public int SwapChainWidth => _swapChain?.Width ?? 0;

    public int SwapChainHeight => _swapChain?.Height ?? 0;

    private bool GpuOpened => _gpu is not null;

    private bool HasViewer => _swapChain is not null;

    private void OpenGpu()
    {
        _gpu = GpuDevice.Create();
        DeviceDescription = _gpu.ToString();
        _presenter = new Presenter(_gpu) { Surround = _surround };
        _gpuTimer = new GpuTimer(_gpu);
    }

    private bool IsGpuLost() => _gpu is { IsLost: true };

    /// <summary>
    /// La GPU se perdió (TDR, controlador que se actualiza, DEVICE_REMOVED): todo lo que colgaba del dispositivo viejo se
    /// suelta y se rehace sobre uno nuevo, con la misma cadena del visor (que la interfaz vuelve a recibir), la misma
    /// secuencia, el cabezal donde estaba y reproduciendo si reproducía. Corre en el hilo de render.
    /// </summary>
    private void RecoverGpu()
    {
        Time position = _session?.Position ?? Time.Zero;
        bool playing = _session?.IsPlaying ?? false;
        SizeRequest? viewer = _swapChain is { } chain ? new SizeRequest(chain.Width, chain.Height, chain.ScaleX, chain.ScaleY, Stopwatch.GetTimestamp()) : null;
        SizeRequest? external = _external is { } other ? new SizeRequest(other.Width, other.Height, other.ScaleX, other.ScaleY, Stopwatch.GetTimestamp()) : null;

        CloseSession();
        _compositor?.Dispose();
        _compositor = null;
        _scopes?.Dispose();
        _scopes = null;
        _looks?.Dispose();
        _looks = null;
        _transitionStills?.Dispose();
        _transitionStills = null;
        _titles?.Dispose();
        _titles = null;
        _gpuTimer?.Dispose();
        _gpuTimer = null;
        _gpuPending = false;
        _presenter?.Dispose();
        _presenter = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _external?.Dispose();
        _external = null;
        _gpu?.Dispose();
        _gpu = null;

        OpenGpu();
        CreateTitles();
        if (_fontFiles is { } fonts)
        {
            _titles!.RegisterFonts(fonts);
        }

        if (viewer is { } size)
        {
            ApplyViewerSize(size);
        }

        if (external is { } second)
        {
            lock (_pendingLock)
            {
                _pendingExternal ??= second;
            }
        }

        OpenSession();
        _session?.Seek(position);
        if (playing)
        {
            _session?.Play();
        }

        _dirty = true;
    }

    private void ApplyViewerSize(SizeRequest size)
    {
        if (_swapChain is null)
        {
            _swapChain = new CompositionSwapChain(_gpu!, size.Width, size.Height, size.ScaleX, size.ScaleY, UsesTenBits(), UsesScRgb());
            _metrics.Milestone("cadena_creada");
            ViewerIsScRgb = _swapChain.ScRgb;
            if (_swapChain.ScRgb)
            {
                _metrics.Note("visor HDR (scRGB R16G16B16A16_FLOAT)");
            }
            else if (_swapChain.TenBits)
            {
                _metrics.Note("visor de 10 bits (R10G10B10A2)");
            }
            SwapChainCreated?.Invoke(_swapChain.NativeSwapChain);
        }
        else
        {
            _swapChain.Resize(size.Width, size.Height, size.ScaleX, size.ScaleY);
            _resizeSince = size.RequestedAt;
        }
    }

    private void WaitForRefresh() => _ = WaitForSingleObjectEx((HANDLE)_swapChain!.FrameWaitHandle, 100, true);

    private Compositor CreateCompositor(int width, int height) => new(_gpu!, width, height);

    private Rodaje.Render.Text.ITitleRasterizer CreateTitleRasterizer() => Rodaje.Render.Text.TitleRasterizers.Create(Rodaje.Render.Gpu.D3D11.D3D11Gpu.For(_gpu!));

    private void PresentViewer()
    {
        // La misma cuenta que el contorno y las guías del visor (ViewerGeometry), con la forma de la secuencia y no la de la composición.
        PixelRect placement = ViewerGeometry.Image(_swapChain!.Width, _swapChain.Height, _session!.Width, _session.Height, _zoom);
        _swapChain!.Render(_presenter!, _compositor, placement);
    }
}
