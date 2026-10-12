using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.AudioAi.Vad;

/// <summary>
/// Detección de voz con Silero VAD (MIT): audio mono de 16 kHz en ventanas de 512 muestras (32 ms) más las 64 anteriores
/// de contexto, con el estado de la red llevado de una ventana a la siguiente. Devuelve la probabilidad de voz de cada
/// ventana; los tramos los arma quien llama (umbral con histéresis). En CPU: es pequeño y va de a una ventana.
/// </summary>
internal sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16_000;
    public const int Window = 512;
    public const int Context = 64;

    private readonly InferenceSession _session;

    public SileroVad(ModelFiles models)
    {
        ArgumentNullException.ThrowIfNull(models);
        using SessionOptions options = new() { IntraOpNumThreads = 1, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(models.Path(ModelFiles.SileroVad), options);
    }

    public static long WindowCount(long samples) => (samples + Window - 1) / Window;

    public Stream Begin(long samples) => new(this, samples);

    public void Dispose() => _session.Dispose();

    /// <summary>Una pasada sobre un audio; <see cref="Step"/> avanza un tramo de ventanas.</summary>
    internal sealed class Stream : IDisposable
    {
        private readonly SileroVad _vad;
        private readonly long _samples;
        private readonly float[] _input = new float[Context + Window];
        private readonly float[] _stateIn = new float[2 * 128];
        private readonly float[] _stateOut = new float[2 * 128];
        private readonly float[] _probability = new float[1];
        private readonly long[] _rate = [SampleRate];
        private readonly OrtValue _inputValue;
        private readonly OrtValue _stateInValue;
        private readonly OrtValue _stateOutValue;
        private readonly OrtValue _probabilityValue;
        private readonly OrtValue _rateValue;
        private long _next;

        internal Stream(SileroVad vad, long samples)
        {
            _vad = vad;
            _samples = samples;
            Windows = WindowCount(samples);
            _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, Context + Window]);
            _stateInValue = OrtValue.CreateTensorValueFromMemory(_stateIn, [2, 1, 128]);
            _stateOutValue = OrtValue.CreateTensorValueFromMemory(_stateOut, [2, 1, 128]);
            _probabilityValue = OrtValue.CreateTensorValueFromMemory(_probability, [1, 1]);
            _rateValue = OrtValue.CreateTensorValueFromMemory(_rate, []);
        }

        public long Windows { get; }

        public double Progress => Windows == 0 ? 1 : (double)_next / Windows;

        public bool IsDone => _next >= Windows;

        /// <summary>Hasta <paramref name="maxWindows"/> ventanas de <paramref name="audio"/> (mono, 16 kHz) a <paramref name="probabilities"/>.</summary>
        public void Step(ReadOnlySpan<float> audio, Span<float> probabilities, int maxWindows, RunOptions run)
        {
            string[] inputNames = ["input", "state", "sr"];
            string[] outputNames = ["output", "stateN"];
            OrtValue[] inputs = [_inputValue, _stateInValue, _rateValue];
            OrtValue[] outputs = [_probabilityValue, _stateOutValue];
            for (int n = 0; n < maxWindows && _next < Windows; n++, _next++)
            {
                // El contexto son las últimas 64 muestras de la ventana anterior (ceros al empezar).
                _input.AsSpan(Window, Context).CopyTo(_input);
                long start = _next * Window;
                int available = (int)Math.Clamp(_samples - start, 0, Window);
                audio.Slice((int)start, available).CopyTo(_input.AsSpan(Context));
                _input.AsSpan(Context + available).Clear();
                _vad._session.Run(run, inputNames, inputs, outputNames, outputs);
                _stateOut.CopyTo(_stateIn, 0);
                probabilities[(int)_next] = _probability[0];
            }
        }

        public void Dispose()
        {
            _inputValue.Dispose();
            _stateInValue.Dispose();
            _stateOutValue.Dispose();
            _probabilityValue.Dispose();
            _rateValue.Dispose();
        }
    }
}
