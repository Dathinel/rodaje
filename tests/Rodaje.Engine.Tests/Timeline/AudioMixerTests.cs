using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class AudioMixerTests
{
    private static readonly SourceKey A = Key("a");
    private static readonly SourceKey B = Key("b");

    [Fact]
    public void A_clip_starts_on_its_exact_sample_and_the_gap_before_it_is_silence()
    {
        var backend = Backend();
        var plan = Plan([new AudioClipPlan("c", A, 1000, 3000, 5000)]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        float[] buffer = Read(mixer, 4096);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal((0f, 0f), (buffer[2 * i], buffer[(2 * i) + 1]));
        }

        for (int i = 1000; i < 3000; i++)
        {
            Assert.Equal(FakeTimelinePcm.Left(5000 + i - 1000), buffer[2 * i]);
            Assert.Equal(FakeTimelineBackend.RightValue(A.Path), buffer[(2 * i) + 1]);
        }

        Assert.All(buffer[6000..], s => Assert.Equal(0f, s));
        Assert.Equal(4096, mixer.Position);
    }

    [Fact]
    public void Adjacent_clips_switch_source_on_the_boundary_sample()
    {
        var backend = Backend();
        var plan = Plan([new AudioClipPlan("x", A, 0, 2999, 100), new AudioClipPlan("y", B, 2999, 6000, 70_000)]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        float[] first = Read(mixer, 1024);
        float[] rest = Read(mixer, 5000);
        float[] all = [.. first, .. rest];

        Assert.Equal(FakeTimelinePcm.Left(100 + 2998), all[2 * 2998]);
        Assert.Equal(FakeTimelineBackend.RightValue(A.Path), all[(2 * 2998) + 1]);
        Assert.Equal(FakeTimelinePcm.Left(70_000), all[2 * 2999]);
        Assert.Equal(FakeTimelineBackend.RightValue(B.Path), all[(2 * 2999) + 1]);
        Assert.Equal(FakeTimelinePcm.Left(70_000 + 3000), all[2 * 5999]);
        Assert.Equal(0f, all[2 * 6000]);
    }

    [Fact]
    public void Tracks_are_summed_sample_by_sample()
    {
        var backend = Backend();
        var plan = new AudioPlan([
            new AudioTrackPlan("t0", [new AudioClipPlan("x", A, 0, 4000, 0)]),
            new AudioTrackPlan("t1", [new AudioClipPlan("y", B, 500, 4000, 1234)]),
        ]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        float[] buffer = Read(mixer, 3000);

        Assert.Equal(FakeTimelinePcm.Left(10), buffer[20]);
        Assert.Equal(FakeTimelinePcm.Left(2000) + FakeTimelinePcm.Left(1234 + 1500), buffer[4000], 6);
        Assert.Equal(FakeTimelineBackend.RightValue(A.Path) + FakeTimelineBackend.RightValue(B.Path), buffer[4001], 6);
    }

    [Fact]
    public void Soft_clip_is_the_identity_below_the_knee_and_stays_below_one_above_it()
    {
        Assert.Equal(0.5f, MixRenderer.SoftClip(0.5f));
        Assert.Equal(-MixRenderer.Knee, MixRenderer.SoftClip(-MixRenderer.Knee));
        Assert.Equal(0f, MixRenderer.SoftClip(float.NaN));
        float previous = 0;
        for (float x = 0; x < 20; x += 0.01f)
        {
            float y = MixRenderer.SoftClip(x);
            Assert.True(y >= previous, $"No es monótona en {x}.");
            Assert.True(y < 1f, $"Pasa de 1 en {x}.");
            Assert.Equal(-y, MixRenderer.SoftClip(-x));
            previous = y;
        }

        Assert.Equal(MixRenderer.Knee, MixRenderer.SoftClip(MixRenderer.Knee + 1e-6f), 4);
        Assert.True(MixRenderer.SoftClip(float.PositiveInfinity) < 1f);
    }

    [Fact]
    public void Loud_tracks_are_limited_instead_of_overflowing()
    {
        var plan = new AudioPlan([.. Enumerable.Range(0, 8).Select(i => new AudioTrackPlan($"t{i}", [new AudioClipPlan($"c{i}", A, 0, 4800, 4095)]))]);
        var backend = Backend();
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        float[] buffer = Read(mixer, 4800);

        Assert.All(buffer, s => Assert.True(MathF.Abs(s) < 1f));
        Assert.True(buffer[0] > MixRenderer.Knee);
    }

    [Fact]
    public void Seek_repositions_every_decoder_on_its_next_read()
    {
        var backend = Backend();
        var plan = new AudioPlan([
            new AudioTrackPlan("t0", [new AudioClipPlan("x", A, 0, 96_000, 0)]),
            new AudioTrackPlan("t1", [new AudioClipPlan("y", B, 48_000, 96_000, 10)]),
        ]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);
        Read(mixer, 1024);

        mixer.Seek(Time.FromSamples(60_000, 48_000));
        float[] buffer = Read(mixer, 16);

        Assert.Equal(FakeTimelinePcm.Left(60_000) + FakeTimelinePcm.Left(10 + 12_000), buffer[0], 6);
        Assert.Equal(60_016, mixer.Position);
        Assert.Equal(2, backend.Pcms.Count);
    }

    [Fact]
    public void The_two_halves_of_a_split_clip_share_one_decoder_without_seeking()
    {
        var backend = Backend();
        var plan = Plan([new AudioClipPlan("izq", A, 0, 2400, 0), new AudioClipPlan("der", A, 2400, 4800, 2400)]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        Read(mixer, 1000);
        Read(mixer, 3800);

        FakeTimelinePcm pcm = Assert.Single(backend.Pcms);
        Assert.Equal(1, pcm.Seeks);
        Assert.Equal(1, mixer.DecodersOpen);
    }

    [Fact]
    public void A_media_that_fails_is_silent_and_the_rest_keeps_playing()
    {
        var backend = Backend();
        backend.FailOpen.Add(B.Path);
        var plan = new AudioPlan([
            new AudioTrackPlan("t0", [new AudioClipPlan("x", A, 0, 4000, 0)]),
            new AudioTrackPlan("t1", [new AudioClipPlan("y", B, 0, 4000, 0)]),
        ]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);

        float[] buffer = Read(mixer, 2000);
        Read(mixer, 1000);

        Assert.Equal(FakeTimelinePcm.Left(1500), buffer[3000]);
        Assert.Equal(1, mixer.Errors);
    }

    [Fact]
    public void Idle_decoders_close_after_the_timeout()
    {
        var time = new ManualTimeProvider();
        var backend = Backend();
        var plan = Plan([new AudioClipPlan("x", A, 0, 1000, 0), new AudioClipPlan("y", B, 1000, 100_000, 0)]);
        using var mixer = new MixRenderer(plan, backend.OpenAudio, TimeSpan.FromSeconds(1), time);

        Read(mixer, 2000);
        Assert.Equal(2, mixer.DecodersOpen);
        time.Advance(TimeSpan.FromSeconds(2));
        Read(mixer, 100);

        Assert.Equal(1, mixer.DecodersOpen);
        Assert.True(backend.Pcms.Single(p => p.Path == A.Path).Disposed);
    }

    [Fact]
    public void A_new_plan_is_adopted_on_the_next_read()
    {
        var backend = Backend();
        using var mixer = new MixRenderer(Plan([new AudioClipPlan("x", A, 0, 100_000, 0)]), backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);
        Read(mixer, 100);

        mixer.SetPlan(AudioPlan.Empty);
        float[] buffer = Read(mixer, 100);

        Assert.All(buffer, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void The_buffered_mixer_serves_the_device_and_reload_remixes_what_is_not_yet_heard()
    {
        var backend = Backend();
        var plan = Plan([new AudioClipPlan("x", A, 0, 480_000, 0)]);
        using var mixer = new AudioMixer(plan, backend.OpenAudio, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeProvider.System);
        WaitFor(() => mixer.BufferedFrames >= 20_000);

        var first = new float[2 * 4800];
        Assert.Equal(4800, mixer.Read(first));
        Assert.Equal(FakeTimelinePcm.Left(4799), first[2 * 4799]);

        mixer.Reload(AudioPlan.Empty);
        WaitFor(() => mixer.BufferedFrames >= 10_000);
        var next = new float[2 * 6000];
        Assert.Equal(6000, mixer.Read(next));

        Assert.Equal(FakeTimelinePcm.Left(4800 + 2399), next[2 * 2399]);
        Assert.All(next[(2 * 2400)..], s => Assert.Equal(0f, s));
        Assert.Equal(0, mixer.Underruns);
    }

    [Fact]
    public void Seek_through_the_buffer_restarts_the_mix_at_the_new_position()
    {
        var backend = Backend();
        using var mixer = new AudioMixer(Plan([new AudioClipPlan("x", A, 0, 480_000, 0)]), backend.OpenAudio,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeProvider.System);

        mixer.Seek(S(2));
        WaitFor(() => mixer.BufferedFrames >= 4800);
        var buffer = new float[2 * 100];
        mixer.Read(buffer);

        Assert.Equal(FakeTimelinePcm.Left(96_000), buffer[0]);
    }

    private static FakeTimelineBackend Backend()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(0, 0);
        b.AddMedia("a", Rational.Fps30, 30_000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 30_000, backend: backend);
        return backend;
    }

    private static SourceKey Key(string id) => new(id, TimelineBuilder.PathOf(id));

    private static AudioPlan Plan(IReadOnlyList<AudioClipPlan> clips) => new([new AudioTrackPlan("t0", clips)]);

    private static float[] Read(MixRenderer mixer, int frames)
    {
        var buffer = new float[frames * 2];
        Assert.Equal(frames, mixer.Read(buffer));
        return buffer;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "La mezcla no llegó a tiempo.");
            Thread.Sleep(1);
        }
    }
}
