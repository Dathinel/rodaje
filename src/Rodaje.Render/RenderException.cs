using System.Globalization;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DXGI;

namespace Rodaje.Render;

/// <summary>Error de Direct3D al componer o presentar.</summary>
public sealed class RenderException : Exception
{
    public RenderException()
    {
    }

    public RenderException(string message)
        : base(message)
    {
    }

    public RenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RenderException(string message, int hresult)
        : base(message) => HResult = hresult;

    internal static void Check(HRESULT hr, string operation)
    {
        if (hr.SUCCEEDED)
        {
            return;
        }

        string hint = hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET
            ? " La GPU se reinició o se perdió (por ejemplo, al actualizar el controlador)."
            : "";
        throw new RenderException(string.Create(CultureInfo.InvariantCulture, $"{operation} falló con HRESULT 0x{hr.Value:X8}.{hint}"), hr.Value);
    }
}
