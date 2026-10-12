using Rodaje.Audio;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Voice;

/// <summary>
/// «Escuchar»: reproduce la voz generada (o un sonido de la biblioteca) por la salida de audio predeterminada, sin tocar el proyecto. Abre su propio
/// flujo WASAPI compartido (48 kHz estéreo) y lo cierra al terminar o al pedir otra.
/// </summary>
public sealed partial class VoicePreviewPlayer : IDisposable
{
    public const int OutputRate = 48_000;

    private IAudioOutput? _output;
    private Source? _source;

    /// <summary>Por qué no se oye (sin dispositivo), o <c>null</c>.</summary>
    public string? Problem { get; private set; }

    public bool IsPlaying => _source is { Finished: false } && _output is not null;

    public void Play(SynthesizedVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        Stop();
        _source = new Source(ToStereo48k(voice.Samples, voice.SampleRate));
        _output = AudioOutput.Open(_source, new AudioOutputOptions { SampleRate = OutputRate });
        Problem = _output.FallbackReason;
        _output.Clock.Start();
    }

    /// <summary>Reproduce un sonido de la biblioteca: muestras intercaladas de 1 a 8 canales (se oyen los dos primeros).</summary>
    public void Play(float[] interleaved, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        Stop();
        _source = new Source(ToStereo48k(interleaved, channels, sampleRate));
        _output = AudioOutput.Open(_source, new AudioOutputOptions { SampleRate = OutputRate });
        Problem = _output.FallbackReason;
        _output.Clock.Start();
    }

    public void Stop()
    {
        _output?.Dispose();
        _output = null;
        _source = null;
    }

    public void Dispose() => Stop();

    /// <summary>Lleva audio mono a estéreo intercalado de 48 kHz (interpolación lineal).</summary>
    public static float[] ToStereo48k(ReadOnlySpan<float> mono, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (mono.IsEmpty)
        {
            return [];
        }

        long frames = (long)Math.Ceiling(mono.Length * (double)OutputRate / sampleRate);
        float[] stereo = new float[checked(frames * 2)];
        double step = sampleRate / (double)OutputRate;
        for (long i = 0; i < frames; i++)
        {
            double at = i * step;
            int a = (int)at;
            float x = a + 1 < mono.Length ? (float)(mono[a] + ((mono[a + 1] - mono[a]) * (at - a))) : mono[Math.Min(a, mono.Length - 1)];
            stereo[i * 2] = x;
            stereo[(i * 2) + 1] = x;
        }

        return stereo;
    }

    /// <summary>Lleva audio intercalado de cualquier frecuencia a estéreo de 48 kHz: mono a los dos lados, de más canales los dos primeros.</summary>
    public static float[] ToStereo48k(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (channels == 1)
        {
            return ToStereo48k(interleaved, sampleRate);
        }

        int frames = interleaved.Length / channels;
        float[] left = new float[frames];
        float[] right = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            left[i] = interleaved[i * channels];
            right[i] = interleaved[(i * channels) + 1];
        }

        float[] l = ToStereo48k(left, sampleRate);
        float[] r = ToStereo48k(right, sampleRate);
        for (int i = 1; i < l.Length; i += 2)
        {
            l[i] = r[i];
        }

        return l;
    }

    private sealed partial class Source(float[] stereo) : IAudioSampleSource
    {
        private int _at;

        public bool Finished => Volatile.Read(ref _at) >= stereo.Length;

        public int Read(Span<float> interleaved)
        {
            int count = Math.Min(interleaved.Length, stereo.Length - _at) & ~1;
            if (count <= 0)
            {
                return 0;
            }

            stereo.AsSpan(_at, count).CopyTo(interleaved);
            Volatile.Write(ref _at, _at + count);
            return count / 2;
        }

        public void Seek(Time position) => Volatile.Write(ref _at, (int)Math.Clamp(position.ToSamples(OutputRate) * 2, 0, stereo.Length));
    }
}
