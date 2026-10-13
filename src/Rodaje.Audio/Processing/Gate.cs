namespace Rodaje.Audio.Processing;

/// <summary>
/// Puerta de ruido y expansor. Por debajo del umbral baja la señal según la relación (1:2 suave, desde
/// <see cref="GateRatio"/> una puerta) sin pasar de <see cref="RangeDb"/>; se abre en el umbral y se cierra
/// <see cref="HysteresisDb"/> por debajo, tras <see cref="HoldMs"/>.
/// </summary>
public sealed record GateSettings(double ThresholdDb, double RangeDb, double Ratio, double AttackMs, double HoldMs, double ReleaseMs)
    : AudioProcessorSettings
{
    /// <summary>Desde esta relación el expansor es una puerta: cerrada baja todo el rango.</summary>
    public const double GateRatio = 20;

    public double HysteresisDb { get; init; } = 3;

    /// <summary>Pasa altos del detector (0, apagado): el retumbe no abre la puerta.</summary>
    public double SidechainHighPassHz { get; init; }

    /// <summary>Ganancia en dB para una entrada estable de <paramref name="inputDb"/> con la puerta cerrada (0 por encima del umbral).</summary>
    public double StaticGainDb(double inputDb)
    {
        if (inputDb >= ThresholdDb)
        {
            return 0;
        }

        double range = Math.Max(0, RangeDb);
        return Ratio >= GateRatio ? -range : Math.Max(-range, (inputDb - ThresholdDb) * (Math.Max(1, Ratio) - 1));
    }

    internal override IAudioStage CreateStage(double sampleRate) => new GateStage(this, sampleRate);
}

/// <summary>
/// La puerta: detector de picos (ataque instantáneo, caída de 10 ms) sobre los dos canales enlazados, estado abierto o
/// cerrado con histéresis y retención, y la ganancia en dB suavizada con el ataque al abrir y la relajación al cerrar.
/// </summary>
internal sealed class GateStage : IAudioStage
{
    private const double DbPerNeper = 8.685889638065035;

    // Estado: envolvente del detector, ganancia aplicada (dB), retención que queda, abierta (0/1), pasa altos (4).
    private const int Envelope = 0;
    private const int Gain = 1;
    private const int Hold = 2;
    private const int Open = 3;
    private const int Filter = 4;

    private readonly double _sampleRate;
    private readonly double[] _state = new double[Filter + 4];
    private readonly double _detectorRelease;
    private GateSettings _settings = null!;
    private double _openLevel, _closeLevel, _attack, _release, _holdFrames;
    private Biquad _highPass;
    private bool _filter;

    public GateStage(GateSettings settings, double sampleRate)
    {
        _sampleRate = sampleRate;
        _detectorRelease = Math.Exp(-1 / (0.010 * sampleRate));
        Configure(settings);
    }

    public int Latency => 0;

    public int StateLength => _state.Length;

    public DynamicsReading Reading { get; private set; }

    public void SaveState(Span<double> into) => _state.CopyTo(into);

    public void LoadState(ReadOnlySpan<double> from) => from.CopyTo(_state);

    public void Configure(AudioProcessorSettings settings)
    {
        var gate = (GateSettings)settings;
        _settings = gate;
        _openLevel = Math.Pow(10, gate.ThresholdDb / 20);
        _closeLevel = Math.Pow(10, (gate.ThresholdDb - Math.Max(0, gate.HysteresisDb)) / 20);
        _attack = 1 - Math.Exp(-1 / (Math.Max(0.01, gate.AttackMs) / 1000 * _sampleRate));
        _release = 1 - Math.Exp(-1 / (Math.Max(0.1, gate.ReleaseMs) / 1000 * _sampleRate));
        _holdFrames = Math.Max(0, gate.HoldMs) / 1000 * _sampleRate;
        _filter = gate.SidechainHighPassHz > 0;
        _highPass = _filter ? Biquad.HighPass(_sampleRate, gate.SidechainHighPassHz) : Biquad.Identity;
    }

    public void Reset()
    {
        Array.Clear(_state);

        // Tras un salto arranca cerrada del todo: el primer sonido fuerte la abre con su ataque.
        _state[Gain] = _settings.StaticGainDb(double.NegativeInfinity);
    }

    public void Process(Span<float> interleaved, ReadOnlySpan<float> key)
    {
        double[] s = _state;
        double envelope = s[Envelope], gain = s[Gain], hold = s[Hold];
        bool open = s[Open] != 0;
        double z1 = s[Filter], z2 = s[Filter + 1], z3 = s[Filter + 2], z4 = s[Filter + 3];
        double inPeak = 0, outPeak = 0, most = 0;
        GateSettings settings = _settings;
        for (int i = 0; i + 1 < interleaved.Length; i += 2)
        {
            double l = interleaved[i], r = interleaved[i + 1];
            double dl = l, dr = r;
            if (_filter)
            {
                dl = _highPass.Tick(dl, ref z1, ref z2);
                dr = _highPass.Tick(dr, ref z3, ref z4);
            }

            double level = Math.Max(Math.Abs(dl), Math.Abs(dr));
            envelope = level > envelope ? level : envelope * _detectorRelease;
            if (envelope >= _openLevel)
            {
                open = true;
                hold = _holdFrames;
            }
            else if (open && envelope < _closeLevel)
            {
                if (hold > 0)
                {
                    hold--;
                }
                else
                {
                    open = false;
                }
            }

            double target = open ? 0 : settings.StaticGainDb(envelope > 1e-12 ? DbPerNeper * Math.Log(envelope) : -240);
            gain += (target - gain) * (target > gain ? _attack : _release);
            double g = gain > -1e-9 ? 1 : Math.Exp(gain / DbPerNeper);
            float outL = (float)(l * g), outR = (float)(r * g);
            interleaved[i] = outL;
            interleaved[i + 1] = outR;
            inPeak = Math.Max(inPeak, Math.Max(Math.Abs(l), Math.Abs(r)));
            outPeak = Math.Max(outPeak, Math.Max(Math.Abs(outL), Math.Abs(outR)));
            most = Math.Max(most, -gain);
        }

        (s[Envelope], s[Gain], s[Hold], s[Open]) = (envelope < 1e-30 ? 0 : envelope, gain, hold, open ? 1 : 0);
        (s[Filter], s[Filter + 1], s[Filter + 2], s[Filter + 3]) = (z1, z2, z3, z4);
        Reading = new DynamicsReading((float)inPeak, (float)outPeak, (float)most, 0);
    }
}
