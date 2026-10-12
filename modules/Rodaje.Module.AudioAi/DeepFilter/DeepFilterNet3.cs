using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.AudioAi.DeepFilter;

/// <summary>
/// Reducción de ruido con DeepFilterNet3 (Schröter et al., MIT/Apache-2.0) en tres grafos ONNX: codificador, decodificador
/// de máscara ERB y decodificador del filtro profundo. Fuera de línea, por tramos de <see cref="ChunkFrames"/> cuadros con
/// <see cref="WarmupFrames"/> de contexto previo para que las GRU arranquen como si el audio fuera continuo.
/// </summary>
internal sealed class DeepFilterNet3 : IDisposable
{
    /// <summary>Cuadros (de 10 ms) que se procesan por llamada a los grafos.</summary>
    public const int ChunkFrames = 3000;

    public const int WarmupFrames = 150;

    /// <summary>Cuadros posteriores que necesita el último cuadro de un tramo (rasgos adelantados y filtro profundo).</summary>
    private const int LookaheadFrames = DfState.ConvLookahead + DfState.DfLookahead;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _erbDecoder;
    private readonly InferenceSession _dfDecoder;

    public DeepFilterNet3(ModelFiles models, SessionFactory sessions)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(sessions);
        _encoder = sessions.Create(models.Path(ModelFiles.DfnEncoder));
        _erbDecoder = sessions.Create(models.Path(ModelFiles.DfnErbDecoder));
        _dfDecoder = sessions.Create(models.Path(ModelFiles.DfnDfDecoder));
        Device = SessionFactory.Device;
    }

    public string Device { get; }

    /// <summary>Un canal a la vez; ver <see cref="Channel"/>.</summary>
    public Channel Begin(long frames, int channels, int channel) => new(this, frames, channels, channel);

    public void Dispose()
    {
        _encoder.Dispose();
        _erbDecoder.Dispose();
        _dfDecoder.Dispose();
    }

    /// <summary>
    /// Limpia un canal de audio intercalado a 48 kHz tramo por tramo (<see cref="Step"/>), para poder informar el avance y
    /// cancelar entre tramos. La salida queda alineada con la entrada (se descuenta el retardo de la STFT).
    /// </summary>
    internal sealed class Channel
    {
        private readonly DeepFilterNet3 _model;
        private readonly DfState _state = new();
        private readonly long _samples;
        private readonly int _channels;
        private readonly int _channel;
        private readonly long _totalFrames;
        private readonly int _capacity = WarmupFrames + ChunkFrames + LookaheadFrames + 4;
        private readonly float[] _spectra;
        private readonly float[] _erb;
        private readonly float[] _df;
        private readonly float[] _hopIn = new float[DfState.Hop];
        private readonly float[] _hopOut = new float[DfState.Hop];
        private readonly float[] _enhanced = new float[DfState.Bins * 2];
        private long _generated;
        private long _next;

        internal Channel(DeepFilterNet3 model, long samples, int channels, int channel)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(samples);
            _model = model;
            _samples = samples;
            _channels = channels;
            _channel = channel;
            _totalFrames = (samples + DfState.FftSize + DfState.Hop - 1) / DfState.Hop;
            _spectra = new float[_capacity * DfState.Bins * 2];
            _erb = new float[_capacity * DfState.ErbBands];
            _df = new float[_capacity * DfState.DfBins * 2];
        }

        public double Progress => _totalFrames == 0 ? 1 : (double)_next / _totalFrames;

        public bool IsDone => _next >= _totalFrames;

        /// <summary>Procesa el siguiente tramo: lee de <paramref name="input"/> y escribe en <paramref name="output"/> (mismo intercalado).</summary>
        public void Step(ReadOnlySpan<float> input, Span<float> output, RunOptions? run = null)
        {
            if (IsDone)
            {
                return;
            }

            long a = _next;
            long b = Math.Min(_totalFrames, a + ChunkFrames);
            long s0 = Math.Max(0, a - WarmupFrames);
            long e1 = Math.Min(_totalFrames, b + LookaheadFrames);
            while (_generated < e1)
            {
                Analyze(input, _generated++);
            }

            int length = (int)(e1 - s0);
            (float[] mask, float[] coefs) = Infer(s0, length, run);
            for (long t = a; t < b; t++)
            {
                Enhance(t, (int)(t - s0), mask, coefs);
                _state.Synthesize(_enhanced, _hopOut);
                long first = (t - 1) * DfState.Hop;
                for (int i = 0; i < DfState.Hop; i++)
                {
                    long at = first + i;
                    if (at >= 0 && at < _samples)
                    {
                        output[(int)((at * _channels) + _channel)] = _hopOut[i];
                    }
                }
            }

            _next = b;
        }

        private void Analyze(ReadOnlySpan<float> input, long frame)
        {
            long start = frame * DfState.Hop;
            for (int i = 0; i < DfState.Hop; i++)
            {
                long at = start + i;
                _hopIn[i] = at < _samples ? input[(int)((at * _channels) + _channel)] : 0f;
            }

            int slot = Slot(frame);
            _state.Analyze(_hopIn,
                _spectra.AsSpan(slot * DfState.Bins * 2, DfState.Bins * 2),
                _erb.AsSpan(slot * DfState.ErbBands, DfState.ErbBands),
                _df.AsSpan(slot * DfState.DfBins * 2, DfState.DfBins * 2));
        }

        private (float[] Mask, float[] Coefs) Infer(long s0, int length, RunOptions? run)
        {
            // Rasgos adelantados ConvLookahead cuadros (pad_feat del modelo): la fila i es el cuadro s0 + i + 2.
            var erb = new float[length * DfState.ErbBands];
            var spec = new float[2 * length * DfState.DfBins];
            for (int i = 0; i < length; i++)
            {
                long frame = s0 + i + DfState.ConvLookahead;
                if (frame >= s0 + length)
                {
                    break;
                }

                int slot = Slot(frame);
                _erb.AsSpan(slot * DfState.ErbBands, DfState.ErbBands).CopyTo(erb.AsSpan(i * DfState.ErbBands));
                _df.AsSpan(slot * DfState.DfBins * 2, DfState.DfBins).CopyTo(spec.AsSpan(i * DfState.DfBins));
                _df.AsSpan((slot * DfState.DfBins * 2) + DfState.DfBins, DfState.DfBins).CopyTo(spec.AsSpan((length + i) * DfState.DfBins));
            }

            using OrtValue featErb = OrtValue.CreateTensorValueFromMemory(erb, [1, 1, length, DfState.ErbBands]);
            using OrtValue featSpec = OrtValue.CreateTensorValueFromMemory(spec, [1, 2, length, DfState.DfBins]);
            using RunOptions? owned = run is null ? new RunOptions() : null;
            RunOptions active = run ?? owned!;
            using IDisposableReadOnlyCollection<OrtValue> enc = _model._encoder.Run(active, ["feat_erb", "feat_spec"], [featErb, featSpec],
                ["e0", "e1", "e2", "e3", "emb", "c0"]);
            OrtValue e0 = enc[0], e1 = enc[1], e2 = enc[2], e3 = enc[3], emb = enc[4], c0 = enc[5];
            using IDisposableReadOnlyCollection<OrtValue> m = _model._erbDecoder.Run(active, ["emb", "e3", "e2", "e1", "e0"], [emb, e3, e2, e1, e0], ["m"]);
            using IDisposableReadOnlyCollection<OrtValue> df = _model._dfDecoder.Run(active, ["emb", "c0"], [emb, c0], ["coefs"]);
            return (m[0].GetTensorDataAsSpan<float>().ToArray(), df[0].GetTensorDataAsSpan<float>().ToArray());
        }

        /// <summary>Máscara ERB en todo el espectro y, en los primeros bins, el filtro profundo sobre el espectro con ruido.</summary>
        private void Enhance(long t, int row, float[] mask, float[] coefs)
        {
            ReadOnlySpan<float> x = Spectrum(t);
            int bin = 0;
            for (int band = 0; band < DfState.ErbBands; band++)
            {
                float gain = mask[(row * DfState.ErbBands) + band];
                for (int j = 0; j < DfState.ErbWidths[band]; j++, bin++)
                {
                    _enhanced[2 * bin] = x[2 * bin] * gain;
                    _enhanced[(2 * bin) + 1] = x[(2 * bin) + 1] * gain;
                }
            }

            int coefBase = row * DfState.DfBins * DfState.DfOrder * 2;
            for (int f = 0; f < DfState.DfBins; f++)
            {
                float re = 0, im = 0;
                for (int n = 0; n < DfState.DfOrder; n++)
                {
                    long frame = t + n - (DfState.DfOrder - 1 - DfState.DfLookahead);
                    if (frame < 0 || frame >= _totalFrames)
                    {
                        continue;
                    }

                    ReadOnlySpan<float> s = Spectrum(frame);
                    float sr = s[2 * f], si = s[(2 * f) + 1];
                    int c = coefBase + (((f * DfState.DfOrder) + n) * 2);
                    float cr = coefs[c], ci = coefs[c + 1];
                    re += (sr * cr) - (si * ci);
                    im += (sr * ci) + (si * cr);
                }

                _enhanced[2 * f] = re;
                _enhanced[(2 * f) + 1] = im;
            }
        }

        private ReadOnlySpan<float> Spectrum(long frame) => _spectra.AsSpan(Slot(frame) * DfState.Bins * 2, DfState.Bins * 2);

        private int Slot(long frame) => (int)(frame % _capacity);
    }
}
