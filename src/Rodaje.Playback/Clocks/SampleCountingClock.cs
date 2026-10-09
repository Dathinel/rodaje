using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Base para relojes de dispositivo: posición = destino del último seek + muestras oídas desde entonces.</summary>
/// <remarks>
/// La subclase solo informa cuántas muestras ha reproducido el dispositivo, con un contador que no
/// retrocede entre pausas y con la latencia de salida ya descontada.
/// </remarks>
public abstract class SampleCountingClock : IAudioClock
{
    private readonly Lock _gate = new();
    private Time _origin;
    private long _baseline;
    private bool _running;

    protected SampleCountingClock(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    public Time Position
    {
        get
        {
            lock (_gate)
            {
                return _origin + Time.FromSamples(ReadSamplesPlayed() - _baseline, SampleRate);
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
                OnStart();
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
                OnPause();
                _running = false;
            }
        }
    }

    public void Seek(Time position)
    {
        lock (_gate)
        {
            OnSeek(position);
            _origin = position;
            _baseline = ReadSamplesPlayed();
        }
    }

    /// <summary>Muestras que ya salieron por el dispositivo desde que se abrió.</summary>
    protected abstract long ReadSamplesPlayed();

    protected virtual void OnStart()
    {
    }

    protected virtual void OnPause()
    {
    }

    /// <summary>Descarta el audio en cola: lo que no se oyó no debe sumar a <see cref="ReadSamplesPlayed"/>.</summary>
    protected virtual void OnSeek(Time position)
    {
    }
}
