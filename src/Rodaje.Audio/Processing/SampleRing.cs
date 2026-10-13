namespace Rodaje.Audio.Processing;

/// <summary>
/// Línea de retardo larga indexada por la cuenta absoluta de muestras de su etapa: no entra en el estado que se copia
/// en cada lectura. Conserva <see cref="Retention"/> muestras más que el retardo máximo, así que restaurar un estado de
/// hasta ese tanto atrás la encuentra como estaba.
/// </summary>
internal sealed class SampleRing
{
    /// <summary>Lo que la mezcla puede volver atrás: 64 lecturas de 1024 cuadros (1,37 s a 48 kHz).</summary>
    public const int Retention = 64 * 1024;

    private readonly float[] _data;

    public SampleRing(int maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelay);
        _data = new float[maxDelay + Retention + 1024];
    }

    public int Length => _data.Length;

    public float this[long index]
    {
        get => _data[(int)(index % _data.Length)];
        set => _data[(int)(index % _data.Length)] = value;
    }

    public void Clear() => Array.Clear(_data);

    /// <summary>
    /// Si un estado guardado con la cuenta <paramref name="restored"/> todavía encuentra su historia, sabiendo que lo
    /// último escrito fue <paramref name="written"/> y que lee hasta <paramref name="maxDelay"/> atrás.
    /// </summary>
    public bool Holds(long restored, long written, int maxDelay) =>
        restored <= written && written - restored + maxDelay + 1 <= _data.Length;
}
