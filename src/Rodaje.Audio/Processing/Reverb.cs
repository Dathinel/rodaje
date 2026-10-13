namespace Rodaje.Audio.Processing;

/// <summary>
/// Reverb algorítmica propia: predelay, cuatro pasa todo de difusión y una red de retardo con realimentación (FDN) de
/// ocho líneas con matriz de Householder, amortiguación de agudos en el lazo, ancho estéreo y pasa altos de la cola.
/// </summary>
/// <param name="PreDelayMs">Silencio antes de la cola (0 a 200 ms).</param>
/// <param name="Size">Tamaño de la sala, de 0 a 1: escala las líneas (0,35× a 1,65×).</param>
/// <param name="DecaySeconds">Tiempo de reverberación (RT60): lo que tarda la cola en bajar 60 dB en graves y medios.</param>
/// <param name="DampingHz">Por encima de esto la cola se apaga antes (agudos oscuros); 20 kHz o más, sin amortiguar.</param>
/// <param name="Width">Ancho estéreo de la cola: 0 mono, 1 todo.</param>
/// <param name="LowCutHz">Pasa altos de la cola; 20 Hz o menos, apagado.</param>
/// <param name="Mix">0 seco, 0,5 seco y cola a tope, 1 solo la cola (para un bus de envío).</param>
public sealed record ReverbSettings(double PreDelayMs, double Size, double DecaySeconds, double DampingHz, double Width, double LowCutHz, double Mix)
    : AudioProcessorSettings
{
    public const double MaxPreDelayMs = 200;

    internal override IAudioStage CreateStage(double sampleRate) => new ReverbStage(this, sampleRate);
}

/// <summary>
/// Las líneas largas se indexan por la cuenta de muestras (<see cref="SampleRing"/>): el estado que se copia en cada
/// lectura es solo esa cuenta y los filtros. La ganancia de cada línea da exactamente el RT60 pedido:
/// <c>g = 10^(−3·L / (RT60·fs))</c>.
/// </summary>
internal sealed class ReverbStage : IAudioStage
{
    private const int Lines = 8;
    private const int Diffusers = 4;
    private const double MaxScale = 1.65;

    private static readonly int[] LineLengths = [1499, 1693, 1867, 2053, 2251, 2423, 2617, 2797];
    private static readonly int[] DiffuserLengths = [142, 107, 379, 277];
    private static readonly double[] DiffuserGains = [0.7, 0.7, 0.6, 0.6];

    // Signos de la entrada a cada línea y de cada línea a la salida izquierda y derecha.
    private static readonly double[] InputSigns = [1, -1, 1, -1, 1, -1, 1, -1];
    private static readonly double[] LeftSigns = [1, 1, -1, -1, 1, 1, -1, -1];
    private static readonly double[] RightSigns = [1, -1, 1, -1, -1, 1, -1, 1];

    // Estado: cuenta de muestras, amortiguación de cada línea, pasa altos de la cola (2 canales × 2).
    private const int Count = 0;
    private const int Damping = 1;
    private const int LowCut = Damping + Lines;

    private readonly double _sampleRate;
    private readonly SampleRing[] _lines = new SampleRing[Lines];
    private readonly SampleRing[] _diffusers = new SampleRing[Diffusers];
    private readonly SampleRing _preDelay;
    private readonly int[] _lineLength = new int[Lines];
    private readonly int[] _diffuserLength = new int[Diffusers];
    private readonly double[] _gains = new double[Lines];
    private readonly double[] _taps = new double[Lines];
    private readonly double[] _state = new double[LowCut + 4];
    private readonly int _maxDelay;
    private ReverbSettings _settings = null!;
    private int _preDelayFrames;
    private double _damping, _wetGain, _dryGain, _width, _normalize;
    private Biquad _highPass;
    private bool _lowCut;
    private long _written;

    public ReverbStage(ReverbSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        double scale = sampleRate / 48000 * MaxScale;
        for (int i = 0; i < Lines; i++)
        {
            _lines[i] = new SampleRing((int)Math.Ceiling(LineLengths[i] * scale) + 1);
        }

        for (int i = 0; i < Diffusers; i++)
        {
            _diffusers[i] = new SampleRing((int)Math.Ceiling(DiffuserLengths[i] * scale) + 1);
        }

        int preDelay = (int)Math.Ceiling(ReverbSettings.MaxPreDelayMs * sampleRate / 1000) + 1;
        _preDelay = new SampleRing(preDelay);
        _maxDelay = Math.Max(preDelay, (int)Math.Ceiling(LineLengths[^1] * scale) + 1);
        Configure(settings);
    }

    public int Latency => 0;

    public int StateLength => _state.Length;

    public DynamicsReading Reading => default;

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from)
    {
        from.CopyTo(_state);
        long count = (long)_state[Count];
        if (!_preDelay.Holds(count, _written, _maxDelay) || !_lines[^1].Holds(count, _written, _maxDelay))
        {
            Reset();
            return;
        }

        _written = count;
    }

    public void Configure(AudioProcessorSettings settings)
    {
        _settings = (ReverbSettings)settings;
        double scale = (0.35 + (1.3 * Math.Clamp(_settings.Size, 0, 1))) * _sampleRate / 48000;
        double decay = Math.Clamp(_settings.DecaySeconds, 0.05, 60);
        double energy = 0;
        for (int i = 0; i < Lines; i++)
        {
            _lineLength[i] = Math.Max(1, (int)Math.Round(LineLengths[i] * scale));
            _gains[i] = Math.Pow(10, -3 * _lineLength[i] / (decay * _sampleRate));
            energy += 1 / (1 - (_gains[i] * _gains[i]));
        }

        for (int i = 0; i < Diffusers; i++)
        {
            _diffuserLength[i] = Math.Max(1, (int)Math.Round(DiffuserLengths[i] * scale));
        }

        // La cola de un ruido blanco sale con la potencia de la entrada, sea larga o corta.
        _normalize = Math.Sqrt(Lines / energy);
        _preDelayFrames = (int)Math.Round(Math.Clamp(_settings.PreDelayMs, 0, ReverbSettings.MaxPreDelayMs) * _sampleRate / 1000);
        _damping = _settings.DampingHz >= Math.Min(20000, _sampleRate * 0.45) ? 0 : Math.Exp(-2 * Math.PI * Math.Max(200, _settings.DampingHz) / _sampleRate);
        _width = Math.Clamp(_settings.Width, 0, 1);
        double mix = Math.Clamp(_settings.Mix, 0, 1);
        _dryGain = Math.Min(1, 2 * (1 - mix));
        _wetGain = Math.Min(1, 2 * mix);
        _lowCut = _settings.LowCutHz > 20;
        _highPass = _lowCut ? Biquad.HighPass(_sampleRate, _settings.LowCutHz) : Biquad.Identity;
    }

    public void Reset()
    {
        Array.Clear(_state);
        foreach (SampleRing ring in _lines)
        {
            ring.Clear();
        }

        foreach (SampleRing ring in _diffusers)
        {
            ring.Clear();
        }

        _preDelay.Clear();
        _written = 0;
    }

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        double[] s = _state;
        double[] taps = _taps;
        long n = (long)s[Count];
        double z1 = s[LowCut], z2 = s[LowCut + 1], z3 = s[LowCut + 2], z4 = s[LowCut + 3];
        double damping = _damping, undamped = 1 - damping;
        double inputScale = 1 / Math.Sqrt(Lines);
        double outputScale = _normalize / Math.Sqrt(Lines);
        for (int i = 0; i + 1 < interleaved.Length; i += 2, n++)
        {
            double l = interleaved[i], r = interleaved[i + 1];
            _preDelay[n] = (float)(0.5 * (l + r));
            long from = n - _preDelayFrames;
            double x = from >= 0 ? _preDelay[from] : 0;

            for (int d = 0; d < Diffusers; d++)
            {
                SampleRing ring = _diffusers[d];
                long back = n - _diffuserLength[d];
                double delayed = back >= 0 ? ring[back] : 0;
                double g = DiffuserGains[d];
                double v = x + (g * delayed);
                ring[n] = Flush(v);
                x = delayed - (g * v);
            }

            double sum = 0, wetL = 0, wetR = 0;
            for (int k = 0; k < Lines; k++)
            {
                long back = n - _lineLength[k];
                double tap = back >= 0 ? _lines[k][back] : 0;
                wetL += LeftSigns[k] * tap;
                wetR += RightSigns[k] * tap;
                double lp = (undamped * tap) + (damping * s[Damping + k]);
                s[Damping + k] = lp;
                double fed = lp * _gains[k];
                taps[k] = fed;
                sum += fed;
            }

            // Householder: v − (2/N)·Σv, sin pérdida de energía.
            double reflect = 2 * sum / Lines;
            double injected = x * inputScale;
            for (int k = 0; k < Lines; k++)
            {
                _lines[k][n] = Flush(taps[k] - reflect + (InputSigns[k] * injected));
            }

            wetL *= outputScale;
            wetR *= outputScale;
            double mid = 0.5 * (wetL + wetR);
            double side = 0.5 * (wetL - wetR) * _width;
            wetL = mid + side;
            wetR = mid - side;
            if (_lowCut)
            {
                wetL = _highPass.Tick(wetL, ref z1, ref z2);
                wetR = _highPass.Tick(wetR, ref z3, ref z4);
            }

            interleaved[i] = (float)((l * _dryGain) + (wetL * _wetGain));
            interleaved[i + 1] = (float)((r * _dryGain) + (wetR * _wetGain));
        }

        s[Count] = n;
        _written = n;
        for (int k = 0; k < Lines; k++)
        {
            if (Math.Abs(s[Damping + k]) < 1e-30)
            {
                s[Damping + k] = 0;
            }
        }

        (s[LowCut], s[LowCut + 1], s[LowCut + 2], s[LowCut + 3]) = (z1, z2, z3, z4);
    }

    /// <summary>Lo que ya no se oye pasa a cero antes de que la cola llegue a números desnormalizados (lentos).</summary>
    private static float Flush(double v) => Math.Abs(v) < 1e-20 ? 0f : (float)v;
}
