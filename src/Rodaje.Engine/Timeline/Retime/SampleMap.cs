using Rodaje.Core.Timing;

namespace Rodaje.Engine.Timeline.Retime;

/// <summary>
/// Posición del origen, en muestras con fracción, de cada muestra de la línea de tiempo. Se calcula exacta con
/// <see cref="ClipTimeMap.ExtendedSource"/> cada <see cref="Grid"/> muestras (alineadas a la línea de tiempo) y se
/// interpola en línea recta entre ellas: el error no se acumula y a velocidad constante es exacto.
/// </summary>
/// <remarks>
/// La posición va en el sentido de reproducción (<c>u</c>): hacia adelante es la muestra del origen; en reversa es su
/// opuesta (contada desde el final de cada muestra), así que <c>u</c> siempre crece con la línea de tiempo y los algoritmos no distinguen el sentido.
/// </remarks>
internal sealed class SampleMap
{
    public const int Grid = 64;

    private const double TicksPerSample = (double)Time.TicksPerSecond / TimelinePlan.AudioSampleRate;

    private ClipTimeMap _map;
    private long _cell = long.MinValue;
    private double _u0;
    private double _u1;

    public SampleMap(ClipTimeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
    }

    public bool Reverse => _map.Reverse;

    /// <summary>Velocidad más alta del clip, en valor absoluto (en una curva, la de su nudo más rápido).</summary>
    public double MaxRate() => _map.RateRange(_map.Start, _map.End).Max;

    /// <summary>
    /// Pasa a otro mapeo con el mismo recorrido (<see cref="ClipTimeMap.SameCourse"/>): las posiciones ya calculadas
    /// siguen valiendo, solo cambia hasta dónde llega antes de seguir a la velocidad del borde.
    /// </summary>
    public void Retarget(ClipTimeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = map;
        _cell = long.MinValue;
    }

    public double At(long sample)
    {
        long cell = sample - (((sample % Grid) + Grid) % Grid);
        if (cell != _cell)
        {
            if (cell == _cell + Grid)
            {
                _u0 = _u1;
            }
            else
            {
                _u0 = Exact(cell);
            }

            _u1 = Exact(cell + Grid);
            _cell = cell;
        }

        return _u0 + ((_u1 - _u0) * (sample - cell) / Grid);
    }

    /// <summary>Velocidad (valor absoluto) en la celda de <paramref name="sample"/>.</summary>
    public double RateAt(long sample)
    {
        At(sample);
        return Math.Abs(_u1 - _u0) / Grid;
    }

    /// <summary>
    /// Sin interpolar. En reversa el tick <c>t</c> del origen es el último de la muestra <c>⌊t⌋</c>, así que se cuenta
    /// desde su final: <c>u = −(t + 1)</c> y la muestra es <c>−u − 1</c> (ver <see cref="SourceWindow.Read"/>).
    /// </summary>
    public double Exact(long sample)
    {
        long ticks = _map.ExtendedSource(Time.FromSamples(sample, TimelinePlan.AudioSampleRate)).Ticks;
        return _map.Reverse ? -(ticks + 1) / TicksPerSample : ticks / TicksPerSample;
    }
}
