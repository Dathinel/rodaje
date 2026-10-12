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

/// <summary>Un cuadro de máscara de imagen.</summary>
/// <param name="Width">Ancho en píxeles.</param>
/// <param name="Height">Alto en píxeles.</param>
/// <param name="Gray">Un byte por píxel, de 0 (oculto) a 255 (visible), filas sin relleno.</param>
/// <param name="Stamp">Distinto para cada cuadro distinto: el render vuelve a subir la textura solo si cambia.</param>
public sealed record MatteFrame(int Width, int Height, byte[] Gray, long Stamp);

/// <summary>Recursos que piden los efectos al componer. Nunca bloquea: lo que falta se pide y llega después.</summary>
public interface IEffectResources
{
    /// <summary>
    /// La LUT si ya está cargada. Si no, empieza a cargarla fuera del hilo y devuelve <c>false</c>: mientras tanto la
    /// capa sale sin LUT. Un paquete que falta también devuelve <c>false</c>.
    /// </summary>
    bool TryGetLut(ResourceKey key, out LutData? lut);

    /// <summary>
    /// El cuadro del video gris de una máscara de imagen (<c>rodaje.mask</c>) en <paramref name="seconds"/> desde su
    /// inicio. Se pide por cuadro: quien lo implementa lo lee rápido (es pequeño y FFV1) y guarda el último.
    /// </summary>
    bool TryGetMatte(ResourceKey key, double seconds, out MatteFrame? frame)
    {
        frame = null;
        return false;
    }

    /// <summary>Terminó de cargar algo: en pausa hay que volver a componer el cuadro.</summary>
    event EventHandler? Changed;
}
