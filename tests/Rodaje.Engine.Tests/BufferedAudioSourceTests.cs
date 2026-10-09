using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;

namespace Rodaje.Engine.Tests;

public sealed class BufferedAudioSourceTests
{
    private const int Capacity = 24_000;

    [Fact]
    public void Reads_the_decoded_audio_in_order()
    {
        using var source = new BufferedAudioSource(new FakePcm(), Capacity);
        WaitUntil(() => source.BufferedFrames >= 4096);

        float[] buffer = new float[2048 * 2];
        Assert.Equal(2048, source.Read(buffer));
        AssertFrames(buffer, 2048, firstFrame: 0);
        Assert.Equal(2048, source.Read(buffer));
        AssertFrames(buffer, 2048, firstFrame: 2048);
        Assert.Equal(0, source.Underruns);
    }

    [Fact]
    public void Seek_discards_the_buffer_and_continues_from_the_target()
    {
        var pcm = new FakePcm();
        using var source = new BufferedAudioSource(pcm, Capacity);
        WaitUntil(() => source.BufferedFrames >= 4096);

        source.Seek(Time.FromSamples(96_000, 48_000));
        WaitUntil(() => source.BufferedFrames >= 1024);

        float[] buffer = new float[512 * 2];
        Assert.Equal(512, source.Read(buffer));
        AssertFrames(buffer, 512, firstFrame: 96_000);
        Assert.Equal(1, pcm.Seeks);
    }

    [Fact]
    public void Late_audio_is_skipped_so_the_sound_stays_on_the_clock()
    {
        var pcm = new FakePcm { Gate = new SemaphoreSlim(0) };
        using var source = new BufferedAudioSource(pcm, Capacity);

        float[] buffer = new float[1000 * 2];
        Assert.Equal(0, source.Read(buffer));
        Assert.Equal(1, source.Underruns);

        pcm.Gate.Release(100);
        WaitUntil(() => source.BufferedFrames >= 4000);
        Assert.Equal(1000, source.Read(buffer));
        // Los 1000 primeros cuadros se oyeron como silencio: lo que suena ahora es lo que toca, el 1000.
        AssertFrames(buffer, 1000, firstFrame: 1000);
        Assert.Equal(1000, source.SkippedFrames);
    }

    [Fact]
    public void The_end_of_the_audio_is_not_an_underrun()
    {
        using var source = new BufferedAudioSource(new FakePcm(lengthFrames: 1500), Capacity);
        WaitUntil(() => source.IsEnded);

        float[] buffer = new float[1000 * 2];
        Assert.Equal(1000, source.Read(buffer));
        Assert.Equal(500, source.Read(buffer));
        AssertFrames(buffer, 500, firstFrame: 1000);
        Assert.Equal(0, source.Read(buffer));
        Assert.Equal(0, source.Underruns);
    }

    [Fact]
    public void A_decoder_error_silences_until_the_next_seek()
    {
        var pcm = new FakePcm { ThrowOnRead = new InvalidOperationException("paquete dañado") };
        using var source = new BufferedAudioSource(pcm, Capacity);
        WaitUntil(() => source.Errors >= 1);

        float[] buffer = new float[256 * 2];
        Assert.Equal(0, source.Read(buffer));

        pcm.ThrowOnRead = null;
        source.Seek(Time.Zero);
        WaitUntil(() => source.BufferedFrames >= 256);
        Assert.Equal(256, source.Read(buffer));
        AssertFrames(buffer, 256, firstFrame: 0);
    }

    [Fact]
    public void Dispose_stops_the_thread_and_releases_the_decoder()
    {
        var log = new DisposalLog();
        var pcm = new FakePcm(log) { Gate = new SemaphoreSlim(0) };
        var source = new BufferedAudioSource(pcm, Capacity);
        pcm.Gate.Release(1);

        var disposing = new Thread(source.Dispose);
        disposing.Start();
        pcm.Gate.Release(10);

        Assert.True(disposing.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(["pcm"], log.Entries);
    }

    private static void AssertFrames(float[] buffer, int frames, long firstFrame)
    {
        for (int i = 0; i < frames; i++)
        {
            Assert.Equal(firstFrame + i, buffer[i * 2]);
            Assert.Equal(-(firstFrame + i), buffer[(i * 2) + 1]);
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "El hilo de audio no avanzó en 5 s.");
            Thread.Sleep(1);
        }
    }
}
