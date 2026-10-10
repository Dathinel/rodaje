using Rodaje.App.Services.Shortcuts;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>
/// La distribución de teclado que Windows tiene activa en el hilo de la interfaz (<c>GetKeyboardLayout</c>). Se consulta
/// en cada llamada, así un cambio de idioma de entrada se ve al volver a dibujar.
/// </summary>
internal sealed class SystemKeyboardLayout : IKeyboardLayout
{
    private const uint MapVkToChar = 2;
    private const uint MapScanToVkEx = 3;
    private const uint DeadKeyFlag = 0x80000000;

    public static SystemKeyboardLayout Instance { get; } = new();

    public int VirtualKeyFromScanCode(int scanCode) =>
        (int)MapVirtualKeyExW((uint)scanCode, MapScanToVkEx, GetKeyboardLayout(0));

    public char? CharacterOf(int virtualKey)
    {
        // Las teclas muertas (´ ` ^ en español) traen el bit alto encendido: se muestra su acento igual.
        uint value = MapVirtualKeyExW((uint)virtualKey, MapVkToChar, GetKeyboardLayout(0)) & ~DeadKeyFlag;
        return value is 0 or > 0xFFFF ? null : (char)value;
    }
}
