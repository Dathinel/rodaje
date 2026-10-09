namespace Rodaje.Audio;

/// <summary>Convierte lecturas (posición del dispositivo, instante QPC) en un contador de cuadros oídos suave y monótono.</summary>
/// <remarks>
/// <para>Todas las posiciones son cuadros en un dominio global que no se reinicia: cuadro N es el N-ésimo
/// cuadro que la salida produjo desde que se abrió. Cada apertura o vaciado del dispositivo empieza un
/// segmento cuya posición 0 corresponde a un cuadro global dado, así que el contador sigue continuo aunque
/// el dispositivo vuelva a contar desde cero.</para>
/// <para>Entre lecturas se interpola con el reloj de pared desde el instante exacto en que el dispositivo
/// tomó la última posición, con dos topes: no más de <c>maxExtrapolation</c> cuadros más allá de esa
/// lectura (un dispositivo atascado no arrastra al reloj) y nunca más allá de lo escrito. Una lectura
/// que retrocede no hace retroceder la salida: el valor se sostiene hasta que el dispositivo lo alcanza.</para>
/// <para>Sin dispositivo, el modelo corre libre con el reloj de pared para que la reproducción siga.</para>
/// </remarks>
internal sealed class DevicePositionModel
{
    private readonly Lock _gate = new();
    private readonly long _sampleRate;
    private readonly long _timestampFrequency;
    private long _maxExtrapolation;
    private long _latency;

    private bool _running;
    private bool _freeRun;
    private long _segmentBase;
    private long _deviceFrames;
    private bool _hasReading;
    private long _readingFrames;
    private long _readingTimestamp;
    private long _startDeviceFrames;
    private long _written;
    private long _lastRaw;
    private long _lastOut;
    private long _freeRunBase;
    private long _freeRunTimestamp;

    public DevicePositionModel(int sampleRate, long timestampFrequency, long maxExtrapolationFrames, long latencyFrames = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _sampleRate = sampleRate;
        _timestampFrequency = timestampFrequency;
        Configure(maxExtrapolationFrames, latencyFrames);
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

    public bool IsFreeRunning
    {
        get
        {
            lock (_gate)
            {
                return _freeRun;
            }
        }
    }

    public long Written
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>Topes que dependen del dispositivo abierto: cuánto interpolar sin lecturas nuevas y cuánta latencia descontar.</summary>
    public void Configure(long maxExtrapolationFrames, long latencyFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExtrapolationFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(latencyFrames);
        lock (_gate)
        {
            _maxExtrapolation = maxExtrapolationFrames;
            _latency = latencyFrames;
        }
    }

    public void AddWritten(long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        lock (_gate)
        {
            _written += frames;
        }
    }

    /// <summary>El dispositivo informó que su cuadro <paramref name="deviceFrames"/> del segmento sonaba en <paramref name="timestamp"/>.</summary>
    public void Observe(long deviceFrames, long timestamp)
    {
        lock (_gate)
        {
            if (_freeRun)
            {
                return;
            }

            _deviceFrames = deviceFrames;
            // Justo después de arrancar, el dispositivo repite la posición en la que se detuvo hasta que
            // de verdad empieza a sonar; interpolar desde ahí adelantaría el reloj.
            if (!_running || deviceFrames == _startDeviceFrames)
            {
                return;
            }

            _hasReading = true;
            _readingFrames = deviceFrames;
            _readingTimestamp = timestamp;
        }
    }

    public void Start(long timestamp)
    {
        lock (_gate)
        {
            _running = true;
            _hasReading = false;
            _startDeviceFrames = _deviceFrames;
            if (_freeRun)
            {
                _freeRunBase = _lastRaw;
                _freeRunTimestamp = timestamp;
            }
        }
    }

    public void Stop(long timestamp)
    {
        lock (_gate)
        {
            UpdateOut(UpdateRaw(timestamp));
            _running = false;
        }
    }

    /// <summary>Empieza un segmento: el dispositivo vuelve a contar desde cero.</summary>
    /// <param name="globalBase">Cuadro global que corresponde a la posición 0 del dispositivo.</param>
    /// <param name="discontinuity">
    /// Verdadero tras un vaciado (seek): lo que había en cola no se oyó y el reloj salta a lo escrito.
    /// Falso al reabrir un dispositivo y repetir lo pendiente: el reloj sigue sin saltar.
    /// </param>
    public void BeginSegment(long globalBase, bool discontinuity)
    {
        lock (_gate)
        {
            _freeRun = false;
            _segmentBase = globalBase;
            _deviceFrames = 0;
            _startDeviceFrames = 0;
            _hasReading = false;
            _lastRaw = Math.Max(_lastRaw, globalBase);
            if (discontinuity)
            {
                _lastOut = Math.Max(_lastOut, globalBase);
            }
        }
    }

    /// <summary>Se perdió el dispositivo: desde aquí el contador avanza con el reloj de pared.</summary>
    public void EnterFreeRun(long timestamp)
    {
        lock (_gate)
        {
            UpdateOut(UpdateRaw(timestamp));
            _freeRun = true;
            _hasReading = false;
            _freeRunBase = _lastRaw;
            _freeRunTimestamp = timestamp;
        }
    }

    /// <summary>Vaciado (seek) sin dispositivo: el contador salta a lo escrito y sigue con el reloj de pared.</summary>
    public void RebaseFreeRun(long timestamp)
    {
        lock (_gate)
        {
            _lastRaw = Math.Max(_lastRaw, _written);
            _lastOut = Math.Max(_lastOut, _lastRaw);
            _freeRunBase = _lastRaw;
            _freeRunTimestamp = timestamp;
        }
    }

    /// <summary>Cuadro global que sale por el dispositivo, sin descontar la latencia extra; base para repetir lo pendiente.</summary>
    public long ReadHeard(long timestamp)
    {
        lock (_gate)
        {
            return UpdateRaw(timestamp);
        }
    }

    /// <summary>Cuadro global que se está oyendo, ya descontada la latencia extra. Monótono; congelado en pausa.</summary>
    public long Read(long timestamp)
    {
        lock (_gate)
        {
            return UpdateOut(UpdateRaw(timestamp));
        }
    }

    private long UpdateRaw(long timestamp)
    {
        if (!_running)
        {
            return _lastRaw;
        }

        long value;
        if (_freeRun)
        {
            value = _freeRunBase + ToFrames(timestamp - _freeRunTimestamp);
        }
        else if (_hasReading)
        {
            long elapsed = Math.Min(ToFrames(timestamp - _readingTimestamp), _maxExtrapolation);
            value = Math.Min(_segmentBase + _readingFrames + elapsed, _written);
        }
        else
        {
            value = _lastRaw;
        }

        _lastRaw = Math.Max(_lastRaw, value);
        return _lastRaw;
    }

    private long UpdateOut(long raw)
    {
        _lastOut = Math.Max(_lastOut, raw - _latency);
        return _lastOut;
    }

    private long ToFrames(long ticks) =>
        ticks <= 0 ? 0 : (long)((Int128)ticks * _sampleRate / _timestampFrequency);
}
