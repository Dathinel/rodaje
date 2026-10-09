using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Reloj de pared para medios sin pista de audio.</summary>
public sealed class SystemClock : IAudioClock
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private Time _origin;
    private long _startTimestamp;
    private bool _running;

    public SystemClock()
        : this(TimeProvider.System)
    {
    }

    public SystemClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public Time Position
    {
        get
        {
            lock (_gate)
            {
                return CurrentPosition();
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
            if (!_running)
            {
                _startTimestamp = _timeProvider.GetTimestamp();
                _running = true;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_running)
            {
                _origin = CurrentPosition();
                _running = false;
            }
        }
    }

    public void Seek(Time position)
    {
        lock (_gate)
        {
            _origin = position;
            _startTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private Time CurrentPosition()
    {
        if (!_running)
        {
            return _origin;
        }

        Int128 elapsed = _timeProvider.GetTimestamp() - _startTimestamp;
        return _origin + Time.FromTicks((long)(elapsed * Time.TicksPerSecond / _timeProvider.TimestampFrequency));
    }
}
