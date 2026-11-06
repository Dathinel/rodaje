namespace Rodaje.App.Services.Shortcuts;

/// <summary>Una tecla dibujada del teclado, en unidades de tecla (1 = una letra).</summary>
/// <param name="ScanCode">Código de exploración (0xE0xx si es extendida), con el que la distribución da su tecla virtual.</param>
/// <param name="VirtualKey">Tecla virtual fija (no depende de la distribución), o 0 para pedirla a la distribución.</param>
/// <param name="Label">Texto fijo («Tab», «Bloq Mayús»), o <c>null</c> para usar el nombre de la tecla.</param>
/// <param name="Modifier">Si es Ctrl, Mayús o Alt: al pulsarla en el dibujo se ven las combinaciones con ella.</param>
public sealed record KeyCap(
    double X,
    double Y,
    double Width,
    double Height,
    int ScanCode,
    int VirtualKey = 0,
    string? Label = null,
    KeyModifiers Modifier = KeyModifiers.None)
{
    /// <summary>No lanza acciones: modificadores, bloqueos y la tecla Windows.</summary>
    public bool IsInert => Modifier != KeyModifiers.None || VirtualKey is 0x14 or 0x5B or 0x5C or 0x5D or 0x90 or 0xA5;
}
