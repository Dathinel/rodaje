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
        _gpu = GpuDevice.TryCreate(out string? gpuFailure) ?? throw new InvalidOperationException("Hace falta una GPU con Direct3D 11. " + gpuFailure);
        DeviceDescription = _gpu.ToString();
        _presenter = new Presenter(_gpu) { Surround = _surround };
        _gpuTimer = new GpuTimer(_gpu);
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

    private Rodaje.Render.Text.ITitleRasterizer CreateTitleRasterizer() => new Rodaje.Render.Text.TitleRasterizer(_gpu!);

    private void PresentViewer()
    {
        // La misma cuenta que el contorno y las guías del visor (ViewerGeometry), con la forma de la secuencia y no la de la composición.
        PixelRect placement = ViewerGeometry.Image(_swapChain!.Width, _swapChain.Height, _session!.Width, _session.Height, _zoom);
        _swapChain!.Render(_presenter!, _compositor, placement);
    }
}
