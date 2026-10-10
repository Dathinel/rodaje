using Rodaje.Media.Gpu;

namespace Rodaje.Render.Tests.Support;

internal static class GpuAvailability
{
    /// <summary>Motivo para omitir las pruebas de GPU, o <c>null</c> si hay Direct3D 11 por hardware con NV12 y P010.</summary>
    public static readonly Lazy<string?> SkipReason = new(() =>
    {
        using GpuDevice? gpu = GpuDevice.TryCreate(out string? failure);
        if (gpu is null)
        {
            return "Omitida: " + failure;
        }

        return gpu.SupportsNv12Textures && gpu.SupportsP010Textures
            ? null
            : $"Omitida: la GPU {gpu.AdapterName} no muestrea texturas NV12 y P010.";
    });
}

/// <summary>Prueba que necesita Direct3D 11 por hardware; se omite con el motivo si no lo hay.</summary>
internal sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute() => Skip = GpuAvailability.SkipReason.Value;
}

internal sealed class GpuTheoryAttribute : TheoryAttribute
{
    public GpuTheoryAttribute() => Skip = GpuAvailability.SkipReason.Value;
}
