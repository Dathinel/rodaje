using System.Diagnostics;
using Rodaje.Render;
using Rodaje.Render.Gpu.Vulkan;
using Rodaje.Render.Text;

namespace Rodaje.App.Views.Player;

/// <summary>
/// El visor en Linux (D-066, fase 8): compone con Vulkan y lee cada cuadro a la CPU para que la interfaz lo pinte en un
/// <c>WriteableBitmap</c>. Sin cadena de intercambio no hay vsync: el ritmo lo marca un reloj a 60 Hz.
/// </summary>
internal sealed partial class PlayerEngine
{
    private static readonly long RefreshTicks = Stopwatch.Frequency / 60;
    private VulkanGpu? _vulkan;
    private int _viewerWidth;
    private int _viewerHeight;
    private long _nextRefresh;
    private ViewerFrame? _viewerFrame;

    /// <summary>Hay un cuadro nuevo para <see cref="TakeViewerFrame"/>. Llega desde el hilo de render.</summary>
    public event Action? ViewerFrameReady;

    public int SwapChainWidth => _viewerWidth;

    public int SwapChainHeight => _viewerHeight;

    private bool GpuOpened => _vulkan is not null;

    private bool HasViewer => _viewerWidth > 0;

    /// <summary>El último cuadro del visor, si no se recogió ya; desde el hilo de la interfaz.</summary>
    public ViewerFrame? TakeViewerFrame() => Interlocked.Exchange(ref _viewerFrame, null);

    private void OpenGpu()
    {
        _vulkan = VulkanGpu.Create();
        DeviceDescription = _vulkan.Caps.Adapter;
    }

    partial void CloseGpu() => _vulkan?.Dispose();

    private void ApplyViewerSize(SizeRequest size)
    {
        if (_viewerWidth == 0)
        {
            _metrics.Milestone("cadena_creada");
        }
        else
        {
            _resizeSince = size.RequestedAt;
        }

        _viewerWidth = size.Width;
        _viewerHeight = size.Height;
    }

    private void WaitForRefresh()
    {
        long wait = _nextRefresh - Stopwatch.GetTimestamp();
        if (wait > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency));
        }

        _nextRefresh = Math.Max(_nextRefresh + RefreshTicks, Stopwatch.GetTimestamp());
    }

    private Compositor CreateCompositor(int width, int height) => new(_vulkan!, width, height);

    private SkiaTitleRasterizer CreateTitleRasterizer() => new(_vulkan!);

    // shortcut: una lectura a la CPU y una copia por cuadro; el visor nativo con la cadena de Vulkan es la fase 9.
    private void PresentViewer()
    {
        if (_compositor is not { } compositor)
        {
            return;
        }

        byte[] pixels = compositor.ReadbackRgba();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255;
        }

        Volatile.Write(ref _viewerFrame, new ViewerFrame(pixels, compositor.Width, compositor.Height));
        ViewerFrameReady?.Invoke();
    }
}

/// <summary>Un cuadro del visor en BGRA opaco, fila tras fila.</summary>
internal sealed record ViewerFrame(byte[] Bgra, int Width, int Height);
