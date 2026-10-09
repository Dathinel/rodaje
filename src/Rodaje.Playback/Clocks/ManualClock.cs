using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Reloj que solo avanza cuando se le pide; sirve para pruebas deterministas.</summary>
public sealed class ManualClock : IAudioClock
{
    private readonly Lock _gate = new();
    private Time _position;
    private bool _running;

    public ManualClock(Time position = default) => _position = position;

    public Time Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    public void Seek(Time position)
    {
        lock (_gate)
        {
            _position = position;
        }
    }

    /// <summary>Avanza <paramref name="delta"/> si el reloj corre; en pausa no hace nada, como un dispositivo real.</summary>
    public void Advance(Time delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta.Ticks, nameof(delta));
        lock (_gate)
        {
            if (_running)
            {
                _position += delta;
            }
        }
    }
}
