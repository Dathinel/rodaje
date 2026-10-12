namespace Rodaje.Audio.Processing;

/// <summary>Lo que se aplica a un clip, a una pista o a la mezcla: ecualizador y luego compresor; cualquiera puede faltar.</summary>
public sealed record AudioEffectsSettings(EqualizerSettings? Equalizer, CompressorSettings? Compressor)
{
    public bool IsEmpty => Equalizer is null && Compressor is null;

    /// <summary>Pista cuya mezcla dispara el compresor (sidechain externo); nula, lo dispara el propio audio.</summary>
    public string? SidechainTrackId { get; init; }

    /// <summary>Muestras que la cadena retrasa el audio (anticipación y limitador).</summary>
    public int LatencyFrames(double sampleRate) => Compressor?.LatencyFrames(sampleRate) ?? 0;
}

/// <summary>Lo que hizo el compresor en la última lectura, para los medidores del panel.</summary>
/// <param name="InputPeak">Pico de la entrada del compresor (después del ecualizador), lineal.</param>
/// <param name="OutputPeak">Pico de la salida, lineal.</param>
/// <param name="ReductionDb">Mayor reducción de ganancia del compresor, en dB (positiva).</param>
/// <param name="LimiterReductionDb">Mayor reducción del limitador final, en dB.</param>
public readonly record struct DynamicsReading(float InputPeak, float OutputPeak, float ReductionDb, float LimiterReductionDb);

/// <summary>
/// Ecualizador y compresor sobre estéreo intercalado. El mismo código suena en la vista previa y se escribe en la
/// exportación, así que la salida es la misma muestra a muestra para la misma entrada y el mismo estado.
/// </summary>
/// <remarks>
/// <para>
/// Todo lo que decide la salida (retardos de los filtros, envolventes, líneas de anticipación y el limitador) está en
/// <see cref="State"/>, para guardarlo y restaurarlo cuando la mezcla vuelve atrás tras una edición. <see cref="Configure"/>
/// cambia los coeficientes sin tocar el estado; solo lo rehace si cambia su forma (bandas, anticipación o limitador).
/// </para>
/// <para>
/// Con anticipación o limitador la salida va <see cref="Latency"/> muestras por detrás de la entrada: quien la usa le da
/// la entrada adelantada eso mismo. <see cref="Bypass"/> y <see cref="ListenSidechain"/> son de la escucha: no tocan el
/// estado y salen con el mismo retraso, así se cambian sin saltos.
/// </para>
/// <para>No reserva memoria al procesar mientras no cambie la forma ni crezca el bloque.</para>
/// </remarks>
public sealed class AudioEffectChain
{
    private const int Channels = 2;
    private const int EqualizerState = EqualizerSettings.BandCount * Channels * 2;
    private const int CrossoverStatePerChannel = 18;
    private const double DbPerNeper = 8.685889638065035;

    private readonly double _sampleRate;
    private readonly Biquad?[] _bands = new Biquad?[EqualizerSettings.BandCount];
    private readonly TruePeakLimiter _limiter = new();
    private readonly double[] _kneeStart = new double[3];
    private readonly double[] _thresholds = new double[3];
    private readonly double[] _bandMakeup = new double[3];
    private readonly double[] _bandMakeupGain = new double[3];

    private CompressorSettings? _compressor;
    private (int Bands, int Lookahead, bool Limiter) _shape = (-1, 0, false);
    private int _envelopes, _slow, _rms, _sidechain, _crossover, _lines, _linePosition, _limiterState;
    private double _attack, _release, _slowAttack, _slowRelease, _rmsCoefficient;
    private Biquad _sidechainFilter, _lowPass1, _highPass1, _allPass2, _lowPass2, _highPass2;
    private double _ratio, _knee, _mix, _mixSlope;
    private float[] _monitorLine = [];
    private float[] _monitorOut = [];
    private float[] _filteredKey = [];
    private float[] _low = [];
    private float[] _mid = [];
    private float[] _high = [];
    private int _monitorPosition;

    public AudioEffectChain(AudioEffectsSettings settings, double sampleRate = 48_000)
    {
        _sampleRate = sampleRate;
        Configure(settings);
    }

    public AudioEffectsSettings Settings { get; private set; } = null!;

    /// <summary>El estado completo; su tamaño depende de la forma (<see cref="Configure"/> lo cambia si hace falta).</summary>
    public double[] State { get; private set; } = new double[EqualizerState];

    /// <summary>Muestras que la salida va por detrás de la entrada.</summary>
    public int Latency { get; private set; }

    /// <summary>Comparar A/B: sale la entrada del compresor tal cual (con el mismo retraso) y el compresor sigue por debajo.</summary>
    public bool Bypass { get; set; }

    /// <summary>Escuchar el sidechain: sale lo que oye el detector (la llave filtrada), con el mismo retraso.</summary>
    public bool ListenSidechain { get; set; }

    public DynamicsReading Reading { get; private set; }

    public void Configure(AudioEffectsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Equals(Settings))
        {
            return;
        }

        Settings = settings;
        for (int b = 0; b < _bands.Length; b++)
        {
            _bands[b] = settings.Equalizer?.Band(b, _sampleRate);
            if (_bands[b] is null)
            {
                Array.Clear(State, b * Channels * 2, Channels * 2);
            }
        }

        _compressor = settings.Compressor;
        Shape(_compressor is { } shaped ? (shaped.Bands, shaped.LookaheadFrames(_sampleRate), shaped.CeilingDb is not null) : (0, 0, false));
        if (_compressor is not { } c)
        {
            Latency = 0;
            return;
        }

        Latency = c.LatencyFrames(_sampleRate);
        _attack = Coefficient(c.AttackMs);
        _release = Coefficient(c.ReleaseMs);
        _slowAttack = Coefficient(250);
        _slowRelease = Coefficient(c.ReleaseMs * 5);
        _rmsCoefficient = Coefficient(Math.Max(0.1, c.RmsWindowMs));
        _sidechainFilter = c.SidechainHighPassHz > 0 ? Biquad.HighPass(_sampleRate, c.SidechainHighPassHz) : Biquad.Identity;
        if (c.Multiband is { } bands)
        {
            double low = Math.Clamp(bands.LowHz, 20, _sampleRate / 4);
            double high = Math.Clamp(bands.HighHz, low * 1.5, _sampleRate * 0.45);
            _lowPass1 = Biquad.LowPass(_sampleRate, low);
            _highPass1 = Biquad.HighPass(_sampleRate, low);
            _allPass2 = Biquad.AllPass(_sampleRate, high);
            _lowPass2 = Biquad.LowPass(_sampleRate, high);
            _highPass2 = Biquad.HighPass(_sampleRate, high);
        }

        if (c.CeilingDb is { } ceiling)
        {
            _limiter.Configure(ceiling, _sampleRate);
        }

        if (_monitorLine.Length != Latency * Channels * 2)
        {
            _monitorLine = new float[Latency * Channels * 2];
            _monitorPosition = 0;
        }

        Automate(c.ThresholdDb, c.Ratio, c.Mix, 0);
    }

    /// <summary>
    /// Umbral, relación y mezcla del tramo que sigue (keyframes): la mezcla empieza en <paramref name="mix"/> y avanza
    /// <paramref name="mixSlope"/> por muestra. Sin keyframes valen los de <see cref="Settings"/>.
    /// </summary>
    public void Automate(double thresholdDb, double ratio, double mix, double mixSlope)
    {
        if (_compressor is not { } c)
        {
            return;
        }

        _ratio = ratio;
        _knee = c.KneeDb;
        _mix = mix;
        _mixSlope = mixSlope;
        double auto = c.AutoMakeup ? CompressorSettings.AutoMakeupDb(thresholdDb, ratio) : 0;
        for (int b = 0; b < c.Bands; b++)
        {
            _thresholds[b] = thresholdDb + (c.Multiband?.ThresholdOffset(b) ?? 0);
            _kneeStart[b] = Math.Max(1e-9, Math.Pow(10, (_thresholds[b] - (c.KneeDb / 2)) / 20));
            _bandMakeup[b] = c.MakeupDb + auto + (c.Multiband?.GainDb(b) ?? 0);
            _bandMakeupGain[b] = Math.Pow(10, _bandMakeup[b] / 20);
        }
    }

    /// <summary>Vuelve al umbral, la relación y la mezcla de <see cref="Settings"/>.</summary>
    public void ClearAutomation()
    {
        if (_compressor is { } c)
        {
            Automate(c.ThresholdDb, c.Ratio, c.Mix, 0);
        }
    }

    public void Reset()
    {
        Array.Clear(State);
        if (_compressor?.CeilingDb is not null)
        {
            TruePeakLimiter.Initialize(State.AsSpan(_limiterState));
        }

        Array.Clear(_monitorLine);
    }

    /// <summary>Procesa en el sitio; <paramref name="key"/> es el sidechain externo alineado con la entrada (vacío: el propio audio).</summary>
    public void Process(Span<float> interleaved, ReadOnlySpan<float> key = default)
    {
        for (int b = 0; b < _bands.Length; b++)
        {
            for (int c = 0; _bands[b] is not null && c < Channels; c++)
            {
                _bands[b]!.Value.Process(interleaved, c, Channels, State.AsSpan(((b * Channels) + c) * 2, 2));
            }
        }

        if (_compressor is { } settings)
        {
            key = key.Length >= interleaved.Length ? key[..interleaved.Length] : default;
            bool monitoring = Bypass || ListenSidechain;
            if (monitoring || Latency > 0)
            {
                Monitor(interleaved, key, settings, monitoring);
            }

            Compress(interleaved, key, settings);
            if (monitoring)
            {
                _monitorOut.AsSpan(0, interleaved.Length).CopyTo(interleaved);
            }
        }
        else
        {
            Reading = default;
        }
    }

    /// <summary>Guarda la entrada y la llave con el retraso de la cadena; deja en <see cref="_monitorOut"/> la que se escucha.</summary>
    private void Monitor(ReadOnlySpan<float> input, ReadOnlySpan<float> key, CompressorSettings settings, bool monitoring)
    {
        if (monitoring && _monitorOut.Length < input.Length)
        {
            _monitorOut = new float[input.Length];
        }

        bool listen = ListenSidechain;
        Span<double> state = State;
        int sidechain = _sidechain;
        bool filter = settings.SidechainHighPassHz > 0;
        double z1 = 0, z2 = 0, z3 = 0, z4 = 0;
        if (filter)
        {
            // El filtro de la escucha es una copia del del detector: no toca su estado.
            (z1, z2, z3, z4) = (state[sidechain], state[sidechain + 1], state[sidechain + 2], state[sidechain + 3]);
        }

        int length = Latency;
        for (int i = 0; i + 1 < input.Length; i += Channels)
        {
            float dryL = input[i], dryR = input[i + 1];
            float keyL = key.IsEmpty ? dryL : key[i], keyR = key.IsEmpty ? dryR : key[i + 1];
            if (filter)
            {
                keyL = (float)_sidechainFilter.Tick(keyL, ref z1, ref z2);
                keyR = (float)_sidechainFilter.Tick(keyR, ref z3, ref z4);
            }

            if (length > 0)
            {
                int at = _monitorPosition * 4;
                (float oldDryL, float oldDryR, float oldKeyL, float oldKeyR) = (_monitorLine[at], _monitorLine[at + 1], _monitorLine[at + 2], _monitorLine[at + 3]);
                (_monitorLine[at], _monitorLine[at + 1], _monitorLine[at + 2], _monitorLine[at + 3]) = (dryL, dryR, keyL, keyR);
                _monitorPosition = (_monitorPosition + 1) % length;
                (dryL, dryR, keyL, keyR) = (oldDryL, oldDryR, oldKeyL, oldKeyR);
            }

            if (monitoring)
            {
                _monitorOut[i] = listen ? keyL : dryL;
                _monitorOut[i + 1] = listen ? keyR : dryR;
            }
        }
    }

    private void Compress(Span<float> io, ReadOnlySpan<float> key, CompressorSettings settings)
    {
        double[] state = State;
        int lookahead = _shape.Lookahead;
        int position = lookahead > 0 ? (int)state[_linePosition] : 0;
        bool filter = settings.SidechainHighPassHz > 0;
        double inputPeak = 0, reduction = 0;
        if (settings.Bands == 1)
        {
            position = CompressBand(io, key, filter, 0, position, ref inputPeak, ref reduction);
        }
        else
        {
            // El detector de cada banda es la propia banda; con llave externa, la llave (filtrada una sola vez).
            ReadOnlySpan<float> detector = key;
            if (!key.IsEmpty && filter)
            {
                Span<float> filtered = Scratch(ref _filteredKey, key.Length);
                key.CopyTo(filtered);
                for (int ch = 0; ch < Channels; ch++)
                {
                    _sidechainFilter.Process(filtered, ch, Channels, state.AsSpan(_sidechain + (ch * 2), 2));
                }

                detector = filtered;
            }

            for (int i = 0; i < io.Length; i++)
            {
                inputPeak = Math.Max(inputPeak, Math.Abs(io[i]));
            }

            Span<float> low = Scratch(ref _low, io.Length);
            Span<float> mid = Scratch(ref _mid, io.Length);
            Span<float> high = Scratch(ref _high, io.Length);
            io.CopyTo(low);
            io.CopyTo(mid);
            for (int ch = 0; ch < Channels; ch++)
            {
                int s = _crossover + (ch * CrossoverStatePerChannel);
                _lowPass1.Process(low, ch, Channels, state.AsSpan(s, 2));
                _lowPass1.Process(low, ch, Channels, state.AsSpan(s + 2, 2));
                _allPass2.Process(low, ch, Channels, state.AsSpan(s + 4, 2));
                _highPass1.Process(mid, ch, Channels, state.AsSpan(s + 6, 2));
                _highPass1.Process(mid, ch, Channels, state.AsSpan(s + 8, 2));
            }

            mid.CopyTo(high);
            for (int ch = 0; ch < Channels; ch++)
            {
                int s = _crossover + (ch * CrossoverStatePerChannel);
                _lowPass2.Process(mid, ch, Channels, state.AsSpan(s + 10, 2));
                _lowPass2.Process(mid, ch, Channels, state.AsSpan(s + 12, 2));
                _highPass2.Process(high, ch, Channels, state.AsSpan(s + 14, 2));
                _highPass2.Process(high, ch, Channels, state.AsSpan(s + 16, 2));
            }

            double ignored = 0;
            CompressBand(low, detector, false, 0, position, ref ignored, ref reduction);
            CompressBand(mid, detector, false, 1, position, ref ignored, ref reduction);
            position = CompressBand(high, detector, false, 2, position, ref ignored, ref reduction);
            for (int i = 0; i < io.Length; i++)
            {
                io[i] = low[i] + mid[i] + high[i];
            }
        }

        if (lookahead > 0)
        {
            state[_linePosition] = position;
        }

        double limited = settings.CeilingDb is null ? 0 : _limiter.Process(io, State.AsSpan(_limiterState));
        float outputPeak = 0;
        for (int i = 0; i < io.Length; i++)
        {
            outputPeak = MathF.Max(outputPeak, MathF.Abs(io[i]));
        }

        Reading = new DynamicsReading((float)inputPeak, outputPeak, (float)reduction, (float)limited);
    }

    /// <summary>
    /// Comprime una banda en el sitio con el estado en variables locales durante el bloque: detector, curva, ataque y
    /// relajación, anticipación y mezcla en paralelo. Devuelve dónde queda la línea de anticipación.
    /// </summary>
    private int CompressBand(Span<float> io, ReadOnlySpan<float> key, bool filter, int band, int position, ref double inputPeak, ref double reduction)
    {
        CompressorSettings settings = _compressor!;
        double[] state = State;
        bool external = !key.IsEmpty;
        bool rms = settings.Detector == LevelDetector.Rms;
        bool linked = settings.Linked;
        bool auto = settings.AutoRelease;
        bool wetOnly = _mix >= 1 && _mixSlope == 0;
        int lookahead = _shape.Lookahead;
        int lineL = _lines + (band * Channels * lookahead), lineR = lineL + lookahead;
        int envelopes = _envelopes + (band * Channels), slow = _slow + (band * Channels), squares = _rms + (band * Channels);
        double start = _kneeStart[band];
        double compare = rms ? start * start : start;
        double makeup = _bandMakeup[band], makeupGain = _bandMakeupGain[band];
        double attack = _attack, release = _release, slowAttack = _slowAttack, slowRelease = _slowRelease, a = _rmsCoefficient;
        double envL = state[envelopes], envR = state[envelopes + 1];
        double slowL = state[slow], slowR = state[slow + 1];
        double msL = state[squares], msR = state[squares + 1];
        double s1 = state[_sidechain], s2 = state[_sidechain + 1], s3 = state[_sidechain + 2], s4 = state[_sidechain + 3];
        double peakIn = inputPeak, most = reduction;
        int frame = 0;

        for (int i = 0; i + 1 < io.Length; i += Channels, frame++)
        {
            double xl = io[i], xr = io[i + 1];
            peakIn = Math.Max(peakIn, Math.Max(Math.Abs(xl), Math.Abs(xr)));
            double dl = external ? key[i] : xl, dr = external ? key[i + 1] : xr;
            if (filter)
            {
                dl = _sidechainFilter.Tick(dl, ref s1, ref s2);
                dr = _sidechainFilter.Tick(dr, ref s3, ref s4);
            }

            double levelL, levelR;
            if (rms)
            {
                msL = (a * msL) + ((1 - a) * dl * dl);
                msR = (a * msR) + ((1 - a) * dr * dr);
                levelL = msL;
                levelR = msR;
            }
            else
            {
                levelL = Math.Abs(dl);
                levelR = Math.Abs(dr);
            }

            if (linked)
            {
                levelL = Math.Max(levelL, levelR);
            }

            // Por debajo de la rodilla la curva es la identidad: sin logaritmo.
            double redL = levelL > compare ? Reduction(levelL, band, rms) : 0;
            envL = ((redL > envL ? attack : release) * (envL - redL)) + redL;
            if (envL < 1e-9)
            {
                envL = 0;
            }

            double appliedL = envL;
            if (auto)
            {
                // La lenta sigue a lo que pide la curva: se carga con la compresión sostenida y no con un golpe suelto.
                slowL = ((redL > slowL ? slowAttack : slowRelease) * (slowL - redL)) + redL;
                appliedL = Math.Max(envL, slowL < 1e-9 ? slowL = 0 : slowL);
            }

            double gainL = appliedL == 0 ? makeupGain : Math.Exp((makeup - appliedL) / DbPerNeper);
            double gainR = gainL;
            most = Math.Max(most, appliedL);
            if (!linked)
            {
                double redR = levelR > compare ? Reduction(levelR, band, rms) : 0;
                envR = ((redR > envR ? attack : release) * (envR - redR)) + redR;
                if (envR < 1e-9)
                {
                    envR = 0;
                }

                double appliedR = envR;
                if (auto)
                {
                    slowR = ((redR > slowR ? slowAttack : slowRelease) * (slowR - redR)) + redR;
                    appliedR = Math.Max(envR, slowR < 1e-9 ? slowR = 0 : slowR);
                }

                gainR = appliedR == 0 ? makeupGain : Math.Exp((makeup - appliedR) / DbPerNeper);
                most = Math.Max(most, appliedR);
            }

            if (lookahead > 0)
            {
                (xl, state[lineL + position]) = (state[lineL + position], xl);
                (xr, state[lineR + position]) = (state[lineR + position], xr);
                if (++position == lookahead)
                {
                    position = 0;
                }
            }

            if (wetOnly)
            {
                io[i] = (float)(xl * gainL);
                io[i + 1] = (float)(xr * gainR);
            }
            else
            {
                double mix = Math.Clamp(_mix + (_mixSlope * frame), 0, 1);
                io[i] = (float)(xl + (mix * ((xl * gainL) - xl)));
                io[i + 1] = (float)(xr + (mix * ((xr * gainR) - xr)));
            }
        }

        state[envelopes] = envL;
        state[envelopes + 1] = envR;
        state[slow] = slowL;
        state[slow + 1] = slowR;
        state[squares] = msL < 1e-20 ? 0 : msL;
        state[squares + 1] = msR < 1e-20 ? 0 : msR;
        if (filter)
        {
            (state[_sidechain], state[_sidechain + 1], state[_sidechain + 2], state[_sidechain + 3]) = (s1, s2, s3, s4);
            FlushDenormals(state.AsSpan(_sidechain, 4));
        }

        inputPeak = peakIn;
        reduction = most;
        return position;
    }

    /// <summary>Reducción en dB que pide la curva para <paramref name="level"/> (pico lineal, o cuadrado medio en RMS).</summary>
    private double Reduction(double level, int band, bool squared)
    {
        double db = DbPerNeper * Math.Log(level) * (squared ? 0.5 : 1);
        return db - CompressorSettings.Curve(db, _thresholds[band], _ratio, _knee);
    }

    private static Span<float> Scratch(ref float[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new float[length];
        }

        return buffer.AsSpan(0, length);
    }
    private static void FlushDenormals(Span<double> state)
    {
        for (int i = 0; i < state.Length; i++)
        {
            if (Math.Abs(state[i]) < 1e-30)
            {
                state[i] = 0;
            }
        }
    }

    /// <summary>Reparte el estado del compresor; si la forma cambia lo rehace (el ecualizador conserva el suyo).</summary>
    private void Shape((int Bands, int Lookahead, bool Limiter) shape)
    {
        if (shape == _shape)
        {
            return;
        }

        _shape = shape;
        int bands = Math.Max(shape.Bands, 1);
        _envelopes = EqualizerState;
        _slow = _envelopes + (bands * Channels);
        _rms = _slow + (bands * Channels);
        _sidechain = _rms + (bands * Channels);
        _crossover = _sidechain + 4;
        _lines = _crossover + (shape.Bands == 3 ? CrossoverStatePerChannel * Channels : 0);
        _linePosition = _lines + (bands * Channels * shape.Lookahead);
        _limiterState = _linePosition + 1;
        int size = shape.Bands == 0 ? EqualizerState : _limiterState + (shape.Limiter ? TruePeakLimiter.StateSize : 0);
        double[] state = new double[size];
        Array.Copy(State, state, EqualizerState);
        State = state;
        if (shape.Limiter)
        {
            TruePeakLimiter.Initialize(State.AsSpan(_limiterState));
        }
    }

    private double Coefficient(double milliseconds) => Math.Exp(-1 / (Math.Max(0.01, milliseconds) / 1000 * _sampleRate));
}
