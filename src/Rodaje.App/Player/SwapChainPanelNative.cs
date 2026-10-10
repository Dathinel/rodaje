using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Rodaje.App.Player;

/// <summary>
/// <c>ISwapChainPanelNative</c> de WinUI 3 (<c>microsoft.ui.xaml.media.dxinterop.h</c>), llamado por su tabla virtual:
/// sin <c>ComImport</c> ni reflexión, así que funciona con Native AOT.
/// </summary>
internal static unsafe class SwapChainPanelNative
{
    // {63AAD0B8-7C24-40FF-85A8-640D944CC325}
    private static readonly Guid Iid = new(0x63aad0b8, 0x7c24, 0x40ff, 0x85, 0xa8, 0x64, 0x0d, 0x94, 0x4c, 0xc3, 0x25);

    /// <summary>Muestra <paramref name="swapChain"/> (un <c>IDXGISwapChain1*</c> de composición) en el panel. Solo desde el hilo de la interfaz.</summary>
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        ArgumentNullException.ThrowIfNull(panel);
        nint inspectable = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Guid iid = Iid;
        int hr = Marshal.QueryInterface(inspectable, in iid, out nint native);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            // Tabla: QueryInterface, AddRef, Release y luego SetSwapChain(IDXGISwapChain*).
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)native)[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }
}
