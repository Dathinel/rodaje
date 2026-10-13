namespace Rodaje.Audio.Processing;

/// <summary>
/// Saturación suave: tanh con la entrada subida <see cref="DriveDb"/> y la salida bajada lo mismo (a poco nivel sale
/// igual), de simétrica (cinta) a asimétrica (válvula, armónicos pares) según <see cref="Character"/>. Después un pasa
/// bajos de 6 dB por octava (<see cref="ToneHz"/>), la ganancia de salida y la mezcla en paralelo.
/// </summary>
public sealed record SaturationSettings(double DriveDb, double Character, double ToneHz, double OutputDb, double Mix) : AudioProcessorSettings
{
    public override int LatencyFrames(double sampleRate) => 1;

    internal override IAudioStage CreateStage(double sampleRate) => new SaturationStage(this, sampleRate);
}

/// <summary>
/// La curva va con antialiasing por antiderivada de primer orden (ADAA): la salida es la pendiente media de la
/// antiderivada entre dos muestras, que suprime casi todo el plegado de los armónicos sin sobremuestrear. Esa media
/// apaga los agudos como un promedio de dos muestras (cos(ω/2)); un FIR simétrico de tres coeficientes (1 + 2a, −a, −a,
/// a = 1/8) lo compensa: −0,00004 dB en 1 kHz y −0,5 dB en 10 kHz en lugar de −2. Sale una muestra tarde.
/// </summary>
internal sealed class SaturationStage : IAudioStage
{
    private const double Ln2 = 0.6931471805599453;
    private const double Emphasis = 0.125;

    // Estado por canal: entrada anterior (ya con la ganancia), quitar continua (entrada y salida), tono, las dos salidas
    // anteriores de la curva (para el FIR) y la entrada anterior sin tocar (el seco de la mezcla, alineado).
    private const int PerChannel = 7;

    private readonly double _sampleRate;
    private readonly double[] _state = new double[PerChannel * 2];
    private double _drive, _bias, _biasTanh, _biasF, _tone, _dc, _output, _mix;
    private bool _toneOn;

    public SaturationStage(SaturationSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        _dc = Math.Exp(-2 * Math.PI * 10 / sampleRate);
        Configure(settings);
    }

    public int Latency => 1;

    public int StateLength => _state.Length;

    public DynamicsReading Reading => default;

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from) => from.CopyTo(_state);

    public void Configure(AudioProcessorSettings settings)
    {
        var s = (SaturationSettings)settings;
        _drive = Math.Pow(10, Math.Clamp(s.DriveDb, 0, 48) / 20);
        _bias = 0.35 * Math.Clamp(s.Character, 0, 1);
        _biasTanh = Math.Tanh(_bias);
        _biasF = LogCosh(_bias);
        _toneOn = s.ToneHz < _sampleRate * 0.45 && s.ToneHz > 0;
        _tone = _toneOn ? Math.Exp(-2 * Math.PI * s.ToneHz / _sampleRate) : 0;
        _output = Math.Pow(10, s.OutputDb / 20);
        _mix = Math.Clamp(s.Mix, 0, 1);
    }

    public void Reset() => Array.Clear(_state);

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        double drive = _drive, inverse = 1 / _drive, output = _output, mix = _mix;
        for (int c = 0; c < 2; c++)
        {
            int at = c * PerChannel;
            double previous = _state[at], dcIn = _state[at + 1], dcOut = _state[at + 2], tone = _state[at + 3];
            double y1 = _state[at + 4], y2 = _state[at + 5], dryPrevious = _state[at + 6];
            for (int i = c; i < interleaved.Length; i += 2)
            {
                double input = interleaved[i];
                double dry = dryPrevious;
                dryPrevious = input;
                double u = input * drive;
                double delta = u - previous;
                double shaped = Math.Abs(delta) > 1e-6
                    ? (Antiderivative(u) - Antiderivative(previous)) / delta
                    : Curve(0.5 * (u + previous));
                previous = u;
                double compensated = ((1 + (2 * Emphasis)) * y1) - (Emphasis * (shaped + y2));
                y2 = y1;
                y1 = shaped;
                double wet = compensated * inverse;
                if (_bias != 0)
                {
                    // Lo asimétrico deja continua: un pasa altos de 10 Hz la quita.
                    double y = wet - dcIn + (_dc * dcOut);
                    dcIn = wet;
                    dcOut = y;
                    wet = y;
                }

                if (_toneOn)
                {
                    tone = ((1 - _tone) * wet) + (_tone * tone);
                    wet = tone;
                }

                interleaved[i] = (float)((dry * (1 - mix)) + (wet * output * mix));
            }

            _state[at] = previous;
            _state[at + 1] = dcIn;
            _state[at + 2] = Math.Abs(dcOut) < 1e-30 ? 0 : dcOut;
            _state[at + 3] = Math.Abs(tone) < 1e-30 ? 0 : tone;
            _state[at + 4] = y1;
            _state[at + 5] = y2;
            _state[at + 6] = dryPrevious;
        }
    }

    private double Curve(double x) => Math.Tanh(x + _bias) - _biasTanh;

    /// <summary>∫ (tanh(x + b) − tanh b) dx = ln cosh(x + b) − x·tanh b (menos lo de x = 0).</summary>
    private double Antiderivative(double x) => LogCosh(x + _bias) - (x * _biasTanh) - _biasF;

    /// <summary>ln cosh x sin desbordar: |x| + ln(1 + e^(−2|x|)) − ln 2.</summary>
    private static double LogCosh(double x)
    {
        double a = Math.Abs(x);
        return a + Math.Log(1 + Math.Exp(-2 * a)) - Ln2;
    }
}
