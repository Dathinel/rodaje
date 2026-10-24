namespace Rodaje.App.Services.Settings;

/// <summary>Profundidad de la cadena de intercambio del visor (Preferencias › Rendimiento › «Visor de 10 bits»).</summary>
public enum TenBitViewer
{
    /// <summary>10 bits si algún monitor conectado declara 10 bits por color o más; si no, 8.</summary>
    Auto,
    On,
    Off,
}

public static class ViewerBitDepth
{
    /// <summary>Si el visor se crea en <c>R10G10B10A2</c> (true) o en <c>B8G8R8A8</c> (false) según la preferencia y el monitor.</summary>
    public static bool UsesTenBits(TenBitViewer setting, int monitorBitsPerColor) => setting switch
    {
        TenBitViewer.On => true,
        TenBitViewer.Off => false,
        _ => monitorBitsPerColor >= 10,
    };
}
