using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Render.Export;

/// <summary>
/// Prioridad con que el planificador de la GPU atiende al dispositivo de la exportación (<c>IDXGIDevice::SetGPUThreadPriority</c>,
/// de −7 a 7). En «Bajo rendimiento» se baja para que la vista previa, en su propio dispositivo, pase antes.
/// </summary>
public static unsafe class ExportGpuPriority
{
    public const int Lowest = -7;

    /// <returns><c>false</c> si el controlador no lo admite (la exportación sigue igual).</returns>
    public static bool TrySet(GpuDevice gpu, int priority)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentOutOfRangeException.ThrowIfLessThan(priority, -7);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(priority, 7);
        var device = (IUnknown*)gpu.NativeDevice;
        IDXGIDevice* dxgi;
        if (device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi).FAILED)
        {
            return false;
        }

        try
        {
            return dxgi->SetGPUThreadPriority(priority).SUCCEEDED;
        }
        finally
        {
            dxgi->Release();
        }
    }
}
