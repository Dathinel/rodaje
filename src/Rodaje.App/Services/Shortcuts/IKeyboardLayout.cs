namespace Rodaje.App.Services.Shortcuts;

/// <summary>
/// La distribución de teclado activa: qué tecla virtual da cada tecla física y qué carácter lleva impreso. En la app la
/// da Windows (<c>MapVirtualKeyEx</c> con la distribución del hilo); en las pruebas, una tabla.
/// </summary>
public interface IKeyboardLayout
{
    /// <summary>Código virtual de la tecla física con ese código de exploración (con 0xE000 si es extendida), o 0.</summary>
    int VirtualKeyFromScanCode(int scanCode);

    /// <summary>El carácter que escribe la tecla sin modificadores, o <c>null</c> si no escribe ninguno.</summary>
    char? CharacterOf(int virtualKey);
}
