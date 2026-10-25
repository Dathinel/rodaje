namespace Rodaje.App.Services.Settings;

/// <summary>Profundidad de la cadena de intercambio del visor (Preferencias › Rendimiento › «Visor de 10 bits»).</summary>
public enum TenBitViewer
{
    /// <summary>10 bits si algún monitor conectado declara 10 bits por color o más; si no, 8.</summary>
    Auto,
    On,
    Off,
}

/// <summary>Visor HDR en scRGB (Preferencias › Rendimiento › «Visor HDR»): con él una secuencia HDR se ve en HDR de verdad.</summary>
public enum HdrViewer
{
    /// <summary>scRGB si algún monitor conectado está en modo HDR de Windows; si no, SDR.</summary>
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

    /// <summary>Si el visor se crea en scRGB (<c>R16G16B16A16_FLOAT</c>, BT.709 lineal con 1,0 = 80 nits) según la preferencia y el monitor; manda sobre los 10 bits.</summary>
    public static bool UsesScRgb(HdrViewer setting, bool monitorInHdr) => setting switch
    {
        HdrViewer.On => true,
        HdrViewer.Off => false,
        _ => monitorInHdr,
    };
}
