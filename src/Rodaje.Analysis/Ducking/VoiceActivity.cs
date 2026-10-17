using Rodaje.Analysis.Dsp;
using Rodaje.Analysis.Silence;

namespace Rodaje.Analysis.Ducking;

/// <summary>
/// Detector de voz intercambiable: energía en la banda de voz o el módulo VAD (Silero), sin cambiar el ducking.
/// </summary>
public interface IVoiceActivityDetector
{
    string Name { get; }

    IVoiceActivitySession Begin(int sampleRate, int channels);
}

/// <summary>Recibe el audio de una pasada y devuelve los tramos con voz (s desde el inicio de ese audio).</summary>
public interface IVoiceActivitySession : IAudioAnalyzer
{
    /// <summary>Método que produjo el resultado final (puede ser energía si el módulo VAD falló).</summary>
    string MethodName { get; }

    Task<IReadOnlyList<TimeRange>> FinishAsync(CancellationToken cancellationToken = default);
}

/// <summary>Ajustes del detector de voz por energía.</summary>
public sealed record VoiceOptions
{
    /// <summary>Umbral en la banda de voz relativo a su nivel de referencia (percentil 95), en dB.</summary>
    public double ThresholdDb { get; init; } = -24;

    /// <summary>Nivel absoluto mínimo de la banda de voz para contar como voz.</summary>
    public double AbsoluteFloorDb { get; init; } = -55;

    /// <summary>La banda de voz (250–4000 Hz) debe llevar al menos esta parte de la energía total, en dB.</summary>
    public double MinimumBandShareDb { get; init; } = -10;

    /// <summary>Pausas más cortas que esto no cortan la voz (entre palabras).</summary>
    public double MinimumGap { get; init; } = 0.3;

    /// <summary>Tramos de voz más cortos que esto se ignoran (golpes, clics).</summary>
    public double MinimumVoice { get; init; } = 0.08;
}

/// <summary>Voz por energía: banda 250–4000 Hz (dos paso alto y dos paso bajo de segundo orden) en bloques de 10 ms.</summary>
public sealed class EnergyVoiceDetector(VoiceOptions? options = null) : IVoiceActivityDetector
{
    public VoiceOptions Options { get; } = options ?? new VoiceOptions();

    public string Name => "Energía en la banda de voz";

    public IVoiceActivitySession Begin(int sampleRate, int channels) => new Session(sampleRate, channels, Options);

    /// <summary>Tramos con voz a partir de los niveles por bloque de la banda y del total.</summary>
    public static List<TimeRange> Detect(IReadOnlyList<double> band, IReadOnlyList<double> full, double blockSeconds, double duration, VoiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(band);
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(options);
        double reference = AnalyzerGuard.Percentile(band, 0.95, options.AbsoluteFloorDb);
        if (double.IsNaN(reference))
        {
            return [];
        }

        double threshold = Math.Max(reference + options.ThresholdDb, options.AbsoluteFloorDb);
        var raw = new List<TimeRange>();
        int start = -1;
        for (int i = 0; i <= band.Count; i++)
        {
            bool voiced = i < band.Count && band[i] >= threshold && band[i] - full[i] >= options.MinimumBandShareDb;
            if (voiced && start < 0)
            {
                start = i;
            }
            else if (!voiced && start >= 0)
            {
                raw.Add(new(start * blockSeconds, Math.Min(i * blockSeconds, duration)));
                start = -1;
            }
        }

        return [.. TimeRange.Merge(raw, options.MinimumGap).Where(r => r.Duration >= options.MinimumVoice)];
    }

    private sealed class Session : IVoiceActivitySession
    {
        private readonly VoiceOptions _options;
        private readonly Biquad[] _filters;
        private readonly List<double> _band = [];
        private readonly List<double> _full = [];
        private readonly int _blockLength;
        private double _bandSum, _fullSum;
        private int _filled;
        private long _frames;

        public Session(int sampleRate, int channels, VoiceOptions options)
        {
            AnalyzerGuard.Check(sampleRate, channels);
            SampleRate = sampleRate;
            Channels = channels;
            _options = options;
            _blockLength = Math.Max(1, sampleRate / 100);
            _filters =
            [
                Biquad.HighPass(sampleRate, 250), Biquad.HighPass(sampleRate, 250),
                Biquad.LowPass(sampleRate, 4000), Biquad.LowPass(sampleRate, 4000),
            ];
        }

        public int SampleRate { get; }

        public int Channels { get; }

        public string MethodName => "Energía en la banda de voz";

        public void Push(ReadOnlySpan<float> interleaved)
        {
            AnalyzerGuard.CheckFrames(interleaved, Channels);
            int frames = interleaved.Length / Channels;
            for (int i = 0; i < frames; i++)
            {
                double x = AnalyzerGuard.Mono(interleaved, i, Channels);
                double y = x;
                foreach (Biquad filter in _filters)
                {
                    y = filter.Process(y);
                }

                _fullSum += x * x;
                _bandSum += y * y;
                if (++_filled == _blockLength)
                {
                    Flush();
                }
            }

            _frames += frames;
        }

        public Task<IReadOnlyList<TimeRange>> FinishAsync(CancellationToken cancellationToken = default)
        {
            if (_filled > 0)
            {
                Flush();
            }

            IReadOnlyList<TimeRange> ranges = Detect(_band, _full, (double)_blockLength / SampleRate, (double)_frames / SampleRate, _options);
            return Task.FromResult(ranges);
        }

        private void Flush()
        {
            _band.Add(LevelEnvelope.ToDb(_bandSum / _filled));
            _full.Add(LevelEnvelope.ToDb(_fullSum / _filled));
            _bandSum = 0;
            _fullSum = 0;
            _filled = 0;
        }
    }
}
