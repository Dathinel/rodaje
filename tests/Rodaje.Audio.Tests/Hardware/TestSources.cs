using System.Diagnostics;
using Rodaje.Core.Timing;

namespace Rodaje.Audio.Tests.Hardware;

/// <summary>Silencio que anota el último salto.</summary>
internal sealed class SilenceSource : IAudioSampleSource
{
    private long _lastSeekTicks = -1;

    public Time? LastSeek => Interlocked.Read(ref _lastSeekTicks) is var t && t >= 0 ? Time.FromTicks(t) : null;

    public int Read(Span<float> interleaved)
    {
        interleaved.Clear();
        return interleaved.Length / 2;
    }

    public void Seek(Time position) => Interlocked.Exchange(ref _lastSeekTicks, position.Ticks);
}

/// <summary>Silencio con un clic de 1 ms en el cuadro <paramref name="atFrame"/>; anota cuándo lo entregó (Stopwatch en 100 ns).</summary>
internal sealed class ClickSource(int rate, long atFrame) : IAudioSampleSource
{
    private long _frame;

    public long Produced100ns { get; private set; }

    public static long Now100ns() => (long)((Int128)Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    public int Read(Span<float> interleaved)
    {
        interleaved.Clear();
        int frames = interleaved.Length / 2;
        for (int f = 0; f < frames; f++)
        {
            long global = _frame + f;
            if (global >= atFrame && global < atFrame + (rate / 1_000))
            {
                if (global == atFrame)
                {
                    Produced100ns = Now100ns();
                }

                interleaved[f * 2] = 0.9f;
                interleaved[(f * 2) + 1] = 0.9f;
            }
        }

        _frame += frames;
        return frames;
    }
}
