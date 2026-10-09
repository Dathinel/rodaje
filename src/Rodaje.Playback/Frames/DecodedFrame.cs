using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Cuadro decodificado, con conteo de referencias sobre su contenido opaco (por ejemplo una textura).</summary>
/// <remarks>
/// Nace con una referencia, la de quien lo creó. Cada <see cref="TryRetain"/> suma una y cada
/// <see cref="Dispose"/> la devuelve; el contenido se libera cuando no queda ninguna. Así la caché puede
/// expulsar un cuadro que la pantalla todavía muestra.
/// </remarks>
public sealed class DecodedFrame : IDisposable
{
    private int _references = 1;

    public DecodedFrame(long index, Time pts, Time duration, IDisposable? payload, long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration.Ticks, nameof(duration));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        Index = index;
        Pts = pts;
        Duration = duration;
        Payload = payload;
        SizeBytes = sizeBytes;
    }

    /// <summary>Número del cuadro dentro del medio de origen.</summary>
    public long Index { get; }

    /// <summary>Instante de presentación en tiempo del origen.</summary>
    public Time Pts { get; }

    public Time Duration { get; }

    public Time End => Pts + Duration;

    public IDisposable? Payload { get; }

    /// <summary>Costo en memoria que la caché descuenta de su presupuesto.</summary>
    public long SizeBytes { get; }

    public bool IsReleased => Volatile.Read(ref _references) == 0;

    public bool Contains(Time sourceTime) => sourceTime >= Pts && sourceTime < End;

    /// <summary>Toma una referencia más; falla si el contenido ya se liberó.</summary>
    public bool TryRetain()
    {
        int current = Volatile.Read(ref _references);
        while (current > 0)
        {
            int seen = Interlocked.CompareExchange(ref _references, current + 1, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }

        return false;
    }

    public void Dispose()
    {
        int current = Volatile.Read(ref _references);
        while (current > 0)
        {
            int seen = Interlocked.CompareExchange(ref _references, current - 1, current);
            if (seen == current)
            {
                if (current == 1)
                {
                    Payload?.Dispose();
                }

                return;
            }

            current = seen;
        }
    }
}
