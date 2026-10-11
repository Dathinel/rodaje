using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests.Support;

/// <summary>
/// Motivo para omitir las pruebas que crean un arreglo de texturas NV12/P010 (<c>D3D11_BIND_DECODER</c>, ver
/// <see cref="YuvImage.CreateTexture"/>): <c>null</c> si el dispositivo lo admite. Se suma a la guardia de muestreo
/// de <see cref="GpuFactAttribute"/>; el adaptador WARP muestrea NV12 pero rechaza el bind de decodificador.
/// </summary>
internal static class GpuArray
{
    private static readonly Lazy<string?> ReasonValue = new(Probe);

    public static string? Reason => ReasonValue.Value;

    private static unsafe string? Probe()
    {
        if (Rodaje.TestMedia.Skip.GpuTextures is { } sampling)
        {
            return sampling;
        }

        using GpuDevice? gpu = GpuDevice.TryCreate(out string? failure);
        if (gpu is null)
        {
            return "Omitida: " + failure;
        }

        var device = (ID3D11Device*)gpu.NativeDevice;
        foreach (DXGI_FORMAT format in new[] { DXGI_FORMAT.DXGI_FORMAT_NV12, DXGI_FORMAT.DXGI_FORMAT_P010 })
        {
            uint support;
            uint needed = (uint)D3D11_FORMAT_SUPPORT.D3D11_FORMAT_SUPPORT_DECODER_OUTPUT;
            if (device->CheckFormatSupport(format, &support).FAILED || (support & needed) != needed)
            {
                return $"Omitida: la GPU {gpu.AdapterName} no admite {format} como salida de decodificador (D3D11_BIND_DECODER).";
            }
        }

        return null;
    }
}

/// <summary>Necesita Direct3D 11 por hardware que cree arreglos NV12/P010 con <c>D3D11_BIND_DECODER</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class GpuArrayFactAttribute : FactAttribute
{
    public GpuArrayFactAttribute() => Skip = GpuArray.Reason;
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class GpuArrayTheoryAttribute : TheoryAttribute
{
    public GpuArrayTheoryAttribute() => Skip = GpuArray.Reason;
}
