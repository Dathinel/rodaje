using Rodaje.Audio.Metering;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>
/// Decide qué tramo medir en cada lectura de los medidores: desde la anterior hasta lo que suena ahora. En pausa (o
/// recorriendo con la lanzadera, que no suena) da silencio, así las barras caen a cero. Solo desde el hilo que dibuja.
/// </summary>
public sealed class AudioMeterReader
{
    /// <summary>Lo más que se extrapola la posición desde la última vuelta del render.</summary>
    public static readonly Time MaxExtrapolation = Time.FromSeconds(0.1);

    /// <summary>Un salto mayor que este es un seek: se empieza a medir de nuevo desde ahí.</summary>
    public static readonly Time Jump = Time.FromSeconds(0.25);

    private object? _source;
    private Time _lastRead;
    private Time _rmsFloor;

    /// <summary>Hasta dónde midió la última lectura que sonaba; nulo en pausa o sin audio.</summary>
    public Time? LastRead => _source is null ? null : _lastRead;

    /// <summary>Desde dónde vale medir: el último salto de posición.</summary>
    public Time Floor => _rmsFloor;

    /// <param name="source">Lo que se mide (la sesión de reproducción), o <c>null</c> si no hay audio.</param>
    /// <param name="position">Posición publicada por el render.</param>
    /// <param name="playing">Si suena de verdad.</param>
    /// <param name="sincePosition">Tiempo de pared desde que se publicó <paramref name="position"/>.</param>
    /// <param name="measure">Mide <c>(desde, hasta, piso del RMS)</c>; estático para no reservar en cada cuadro.</param>
    public AudioLevels Take<TSource>(TSource? source, Time position, bool playing, TimeSpan sincePosition, Func<TSource, Time, Time, Time, AudioLevels> measure)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (source is null || !playing)
        {
            _source = null;
            return AudioLevels.Silence;
        }

        // Entre dos vueltas del render el reloj del dispositivo sigue avanzando al ritmo del reloj de pared.
        Time elapsed = Time.FromSeconds(Math.Max(0, sincePosition.TotalSeconds));
        position += elapsed < MaxExtrapolation ? elapsed : MaxExtrapolation;

        Time last = _lastRead;
        bool jumped = !ReferenceEquals(source, _source) || position - last > Jump || last - position > Jump;
        if (jumped)
        {
            _source = source;
            _rmsFloor = position;
            last = position;
        }
        else if (position < last)
        {
            // La extrapolación anterior se pasó un poco: no se vuelve a medir lo ya medido.
            position = last;
        }

        _lastRead = position;
        return measure(source, last, position, _rmsFloor);
    }
}
