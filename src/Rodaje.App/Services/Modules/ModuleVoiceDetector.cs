using System.Runtime.InteropServices;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Modules;

/// <summary>
/// Detector de voz con modelo (Silero VAD de <c>modulo-audio-ia</c>, función <c>detectar-voz</c>) para el ducking y los
/// silencios. Recibe el mismo audio que el de energía y lo alimenta a la vez: si el módulo no está instalado, la frecuencia
/// no es múltiplo de 16 kHz o el módulo falla, el resultado es el del detector de energía, así que el análisis nunca se
/// queda sin respuesta. Cancelar sí cancela.
/// </summary>
public sealed class ModuleVoiceDetector : IVoiceActivityDetector
{
    public const string Function = "detectar-voz";

    private const int ModelRate = 16_000;
    private const double WindowSeconds = 0.032;
    private const int WindowSamples = 512;
    private const int BlockSamples = 1 << 20;

    private readonly ModuleManager _modules;
    private readonly IVoiceActivityDetector _fallback;

    public ModuleVoiceDetector(ModuleManager modules, IVoiceActivityDetector fallback)
    {
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>Umbrales de Silero; las pausas de menos de 0,3 s no cortan la voz (como el detector de energía).</summary>
    public VoiceProbabilityOptions Options { get; init; } = new() { MinimumSilence = 0.3, MinimumSpeech = 0.08 };

    public string Name => _modules.FindByFunction(Function) is { } module ? $"{module.Name}: voz con Silero VAD" : _fallback.Name;

    /// <summary>El módulo está instalado (la próxima sesión lo usará).</summary>
    public bool UsesModule => _modules.FindByFunction(Function) is not null;

    public IVoiceActivitySession Begin(int sampleRate, int channels)
    {
        IVoiceActivitySession baseline = _fallback.Begin(sampleRate, channels);
        return _modules.FindByFunction(Function) is { } module && sampleRate % ModelRate == 0
            ? new Session(this, module, baseline, sampleRate, channels)
            : baseline;
    }

    private sealed class Session(ModuleVoiceDetector owner, ModuleDescriptor module, IVoiceActivitySession baseline, int sampleRate, int channels)
        : IVoiceActivitySession
    {
        private readonly int _factor = sampleRate / ModelRate;
        private readonly List<float[]> _blocks = [];
        private int _filled = BlockSamples;
        private int _pending;
        private float _sum;

        public int SampleRate => sampleRate;

        public int Channels => channels;

        private long Samples => ((long)(_blocks.Count - 1) * BlockSamples) + _filled;

        /// <summary>Al detector de energía tal cual y, mezclado a mono y diezmado a 16 kHz, a la memoria para el módulo.</summary>
        public void Push(ReadOnlySpan<float> interleaved)
        {
            baseline.Push(interleaved);
            int frames = interleaved.Length / channels;
            for (int i = 0; i < frames; i++)
            {
                float mono = 0;
                for (int c = 0; c < channels; c++)
                {
                    mono += interleaved[(i * channels) + c];
                }

                _sum += mono / channels;
                if (++_pending < _factor)
                {
                    continue;
                }

                if (_filled == BlockSamples)
                {
                    _blocks.Add(new float[BlockSamples]);
                    _filled = 0;
                }

                _blocks[^1][_filled++] = _sum / _factor;
                _pending = 0;
                _sum = 0;
            }
        }

        public async Task<IReadOnlyList<TimeRange>> FinishAsync(CancellationToken cancellationToken = default)
        {
            long samples = _blocks.Count == 0 ? 0 : Samples;
            if (samples < WindowSamples)
            {
                return await baseline.FinishAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await owner._modules.RunAsync(module.Id, "Detectar la voz", call => DetectAsync(call, samples), cancellationToken).ConfigureAwait(false);
            }
            catch (ModuleException)
            {
                // El aviso lo da el ModuleManager; el análisis sigue con el detector de energía.
                cancellationToken.ThrowIfCancellationRequested();
                return await baseline.FinishAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IReadOnlyList<TimeRange>> DetectAsync(ModuleCall call, long samples)
        {
            long windows = (samples + WindowSamples - 1) / WindowSamples;
            long audioBytes = samples * sizeof(float);
            SharedMemoryRegion region = call.CreateRegion(audioBytes + (windows * sizeof(float)));
            long at = 0;
            for (int b = 0; b < _blocks.Count; b++)
            {
                int count = b == _blocks.Count - 1 ? _filled : BlockSamples;
                MemoryMarshal.AsBytes(_blocks[b].AsSpan(0, count)).CopyTo(region.GetSpan(at, (long)count * sizeof(float)));
                at += (long)count * sizeof(float);
            }

            _blocks.Clear();
            SharedBufferRef audio = region.Describe(0, audioBytes, audio: new AudioLayout(ModelRate, 1, samples));
            SharedBufferRef output = region.Describe(audioBytes, windows * sizeof(float));
            ModuleResult result = await call.InvokeAsync(Function, null, [audio, output]).ConfigureAwait(false);
            if (result.Buffers.Count != 1 || result.Buffers[0].Length != output.Length)
            {
                throw new ModuleProtocolException("«detectar-voz» devolvió un búfer inesperado.");
            }

            float[] probabilities = MemoryMarshal.Cast<byte, float>(call.Map(result.Buffers[0])).ToArray();
            return VoiceProbability.ToRanges(probabilities, WindowSeconds, (double)samples / ModelRate, owner.Options);
        }
    }
}
