using TerraFX.Interop.Windows;

namespace Rodaje.Render;

[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
internal static unsafe class Com
{
    public static void Release<T>(ref T* pointer)
        where T : unmanaged, IUnknown.Interface
    {
        if (pointer != null)
        {
            pointer->Release();
            pointer = null;
        }
    }
}
