using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Rodaje.Module.AudioAi.DeepFilter;
using Rodaje.Module.AudioAi.Vad;

namespace Rodaje.Module.AudioAi;

/// <summary>Un búfer de audio que se vuelve a pedir en cada tramo (la memoria compartida no se puede retener entre esperas).</summary>
internal delegate Span<float> AudioBuffer();

internal sealed record NoiseResult(string Device, int ChannelsProcessed, double Seconds);

/// <summary>
/// Las funciones del módulo sin el protocolo (las prueban las pruebas sin abrir un proceso): reducción de ruido y detección
/// de voz por tramos, con avance entre tramos y cancelación que también corta la inferencia en curso.
/// </summary>
internal sealed class AudioAiFunctions(ModelFiles models) : IDisposable
{
    /// <summary>Ventanas de Silero por tramo entre avisos de avance (unos 33 s de audio).</summary>
    private const int VadWindowsPerStep = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<ComputeDevice, DeepFilterNet3> _denoisers = [];
    private SileroVad? _vad;

    public ModelFiles Models { get; } = models;

    /// <summary>
    /// Limpia <paramref name="frames"/> cuadros intercalados de 48 kHz. Si los dos canales son iguales (mono grabado en
    /// estéreo, lo habitual en cámaras) se procesa uno y se copia.
    /// </summary>
    public async ValueTask<NoiseResult> ReduceNoiseAsync(AudioBuffer input, AudioBuffer output, int channels, long frames, ComputeDevice device,
        Func<double, ValueTask> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        DeepFilterNet3 model = Denoiser(device);
        long start = Stopwatch.GetTimestamp();
        bool dualMono = channels == 2 && IsDualMono(input(), frames);
        int processed = dualMono ? 1 : channels;
        using RunOptions run = new();
        using CancellationTokenRegistration stop = cancellationToken.Register(() => run.Terminate = true);
        for (int c = 0; c < processed; c++)
        {
            DeepFilterNet3.Channel channel = model.Begin(frames, channels, c);
            while (!channel.IsDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    channel.Step(input(), output(), run);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await progress((c + channel.Progress) / processed).ConfigureAwait(false);
            }
        }

        if (dualMono)
        {
            Span<float> data = output();
            for (long i = 0; i < frames; i++)
            {
                data[(int)((i * 2) + 1)] = data[(int)(i * 2)];
            }
        }

        return new NoiseResult(model.Device, processed, Stopwatch.GetElapsedTime(start).TotalSeconds);
    }

    /// <summary>Probabilidad de voz de cada ventana de 32 ms de <paramref name="samples"/> muestras mono de 16 kHz.</summary>
    public async ValueTask<long> DetectVoiceAsync(AudioBuffer input, AudioBuffer probabilities, long samples,
        Func<double, ValueTask> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(progress);
        SileroVad vad = Vad();
        using SileroVad.Stream stream = vad.Begin(samples);
        using RunOptions run = new();
        using CancellationTokenRegistration stop = cancellationToken.Register(() => run.Terminate = true);
        while (!stream.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                stream.Step(input(), probabilities(), VadWindowsPerStep, run);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await progress(stream.Progress).ConfigureAwait(false);
        }

        return stream.Windows;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (DeepFilterNet3 model in _denoisers.Values)
            {
                model.Dispose();
            }

            _denoisers.Clear();
            _vad?.Dispose();
            _vad = null;
        }
    }

    internal static bool IsDualMono(ReadOnlySpan<float> interleaved, long frames)
    {
        float peak = 0, difference = 0;
        for (long i = 0; i < frames; i++)
        {
            float l = interleaved[(int)(i * 2)], r = interleaved[(int)((i * 2) + 1)];
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            difference = Math.Max(difference, Math.Abs(l - r));
        }

        return difference <= peak * 1e-4f;
    }

    private DeepFilterNet3 Denoiser(ComputeDevice device)
    {
        lock (_gate)
        {
            if (!_denoisers.TryGetValue(device, out DeepFilterNet3? model))
            {
                model = new DeepFilterNet3(Models, new SessionFactory(device));
                _denoisers[device] = model;
            }

            return model;
        }
    }

    private SileroVad Vad()
    {
        lock (_gate)
        {
            return _vad ??= new SileroVad(Models);
        }
    }
}
