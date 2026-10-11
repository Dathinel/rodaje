namespace Rodaje.Playback.Composition;

/// <summary>Una LUT 3D lista para subir a la GPU: <c>Size</c>³ entradas RGB en orden R más rápido, luego G, luego B.</summary>
public sealed class LutData
{
    public LutData(int size, float[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 65);
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != size * size * size * 3)
        {
            throw new ArgumentException($"Una LUT de {size}³ necesita {size * size * size * 3} valores.", nameof(rgb));
        }

        Size = size;
        Rgb = rgb;
    }

    public int Size { get; }

    public ReadOnlyMemory<float> Rgb { get; }
}

/// <summary>Recursos que piden los efectos al componer. Nunca bloquea: lo que falta se pide y llega después.</summary>
public interface IEffectResources
{
    /// <summary>
    /// La LUT si ya está cargada. Si no, empieza a cargarla fuera del hilo y devuelve <c>false</c>: mientras tanto la
    /// capa sale sin LUT. Un paquete que falta también devuelve <c>false</c>.
    /// </summary>
    bool TryGetLut(ResourceKey key, out LutData? lut);

    /// <summary>Terminó de cargar algo: en pausa hay que volver a componer el cuadro.</summary>
    event EventHandler? Changed;
}
