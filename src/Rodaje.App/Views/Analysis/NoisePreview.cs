using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;

namespace Rodaje.App.Views.Analysis;

/// <summary>
/// Escucha A/B del diálogo «Reducir ruido»: un tramo corto del original y del limpio en memoria, en bucle, por su propia
/// salida de audio. Cambiar entre A y B (o mover la intensidad) no corta: sigue en la misma muestra.
/// </summary>
internal sealed partial class NoisePreview : IAudioSampleSource, IDisposable
{
    public static readonly Time Length = Time.FromSeconds(15);

    private readonly float[] _original;
    private readonly float[] _clean;
    private IAudioOutput? _output;
    private int _position;
    private volatile bool _listenOriginal;
    private volatile float _limit;

    private NoisePreview(float[] original, float[] clean)
    {
        _original = original;
        _clean = clean;
    }

    /// <summary>Lee <see cref="Length"/> desde <paramref name="sourceTime"/> (tiempo del medio) del original y del limpio.</summary>
    public static NoisePreview Load(CleanAudio clean, Time sourceTime)
    {
        ArgumentNullException.ThrowIfNull(clean);
        Time from = Time.Min(Time.Max(clean.Offset, sourceTime), clean.Offset + clean.Duration - Time.FromSeconds(1));
        from = Time.Max(clean.Offset, from);
        long firstFrame = (from - clean.Offset).ToSamples(NoiseReduction.SampleRate);
        int frames = (int)Math.Min(Length.ToSamples(NoiseReduction.SampleRate), clean.Frames - firstFrame);
        float[] processed = NoiseReduction.Wav.Read(clean.Path, NoiseReduction.Channels, firstFrame, Math.Max(0, frames));
        var original = new float[processed.Length];
        using (AudioDecoder decoder = AudioDecoder.Open(clean.SourcePath))
        {
            decoder.Seek(from);
            int done = 0;
            while (done < original.Length)
            {
                int got = decoder.Read(original.AsSpan(done));
                if (got == 0)
                {
                    break;
                }

                done += got * NoiseReduction.Channels;
            }
        }

        return new NoisePreview(original, processed);
    }

    public bool IsPlaying => _output is not null;

    /// <summary>A: el original; B: el limpio con la intensidad elegida.</summary>
    public bool ListenOriginal
    {
        get => _listenOriginal;
        set => _listenOriginal = value;
    }

    /// <summary>Reducción en dB (≥ <see cref="NoiseReduction.MaxReductionDb"/>: sin límite).</summary>
    public double ReductionDb
    {
        set => _limit = value >= NoiseReduction.MaxReductionDb ? 0f : (float)Math.Pow(10, -value / 20);
    }

    /// <summary>Empieza a sonar; <c>false</c> (con el motivo) si no hay salida de audio.</summary>
    public bool Play(out string? problem)
    {
        problem = null;
        if (_output is not null || _clean.Length == 0)
        {
            return _output is not null;
        }

        IAudioOutput output = AudioOutput.Open(this);
        if (!output.HasDevice)
        {
            problem = output.FallbackReason;
            output.Dispose();
            return false;
        }

        _output = output;
        output.Clock.Start();
        return true;
    }

    public void Stop()
    {
        IAudioOutput? output = Interlocked.Exchange(ref _output, null);
        if (output is not null)
        {
            output.Clock.Pause();
            output.Dispose();
        }
    }

    public int Read(Span<float> interleaved)
    {
        int frames = interleaved.Length / NoiseReduction.Channels;
        float limit = _limit;
        bool original = _listenOriginal;
        for (int i = 0; i < frames; i++)
        {
            if (_position >= _clean.Length)
            {
                _position = 0;
            }

            for (int c = 0; c < NoiseReduction.Channels; c++, _position++)
            {
                interleaved[(i * NoiseReduction.Channels) + c] = original
                    ? _original[_position]
                    : (limit * _original[_position]) + ((1 - limit) * _clean[_position]);
            }
        }

        return frames;
    }

    public void Dispose() => Stop();
}
