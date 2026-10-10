using TerraFX.Interop.Windows;

namespace Rodaje.Render;

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
