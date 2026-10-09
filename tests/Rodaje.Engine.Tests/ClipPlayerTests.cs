using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Media;
using Rodaje.Playback;

namespace Rodaje.Engine.Tests;

public sealed class ClipPlayerTests
{
    private static readonly Rational Rate = Rational.Fps25;

    [Fact]
    public void With_audio_and_device_the_device_clock_is_master()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        using ClipPlayer player = backend.Open();

        Assert.True(player.HasAudioDevice);
        Assert.Equal("Altavoces de prueba", player.AudioStatus);
        player.Play();
        backend.Output!.ManualClock.Advance(Time.FromFrames(10, Rate));
        Assert.Equal(10, player.CurrentFrame);
        Assert.IsType<BufferedAudioSource>(backend.Output.Source);
        Assert.Equal(1, backend.AudioOpens);
    }

    [Fact]
    public void Without_audio_track_the_system_clock_is_used_and_no_audio_is_opened()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100, audio: false));
        using ClipPlayer player = backend.Open();

        Assert.Equal(0, backend.AudioOpens);
        Assert.Null(backend.Output);
        Assert.False(player.HasAudioDevice);
        Assert.Contains("no tiene audio", player.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_audio_does_not_open_the_decoder()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        using ClipPlayer player = backend.Open(new ClipPlayerOptions { EnableAudio = false });

        Assert.Equal(0, backend.AudioOpens);
        Assert.Contains("desactivado", player.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_without_device_reports_the_fallback_reason()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100)) { DeviceAvailable = false };
        using ClipPlayer player = backend.Open();

        Assert.False(player.HasAudioDevice);
        Assert.Equal("No hay altavoces en la prueba.", player.AudioStatus);
        Assert.False(player.Stats.HasAudioDevice);
    }

    [Fact]
    public void Undecodable_audio_falls_back_to_the_system_clock()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100)) { AudioOpenFailure = new MediaException("códec raro") };
        using ClipPlayer player = backend.Open();

        Assert.Null(backend.Output);
        Assert.Contains("códec raro", player.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_without_video_is_rejected()
    {
        var info = Samples.Info(Rate, 100) with { Video = null };
        var backend = new FakeBackend(info);

        Assert.Throws<MediaException>(() => backend.Open());
    }

    [Fact]
    public void Decoder_options_carry_device_choice_and_pool_headroom()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        using ClipPlayer player = backend.Open(new ClipPlayerOptions { AllowHardwareDecoding = false, CacheFrames = 20 });

        Assert.False(backend.VideoOptions!.AllowHardwareDecoding);
        Assert.Equal(24, backend.VideoOptions.ExtraHardwareFrames);
        Assert.Null(backend.VideoOptions.Device);
    }

    [Fact]
    public void Decode_mode_follows_the_decoder()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 10));
        using ClipPlayer player = backend.Open();

        Assert.Equal(DecodeMode.Gpu, player.Stats.DecodeMode);
        backend.Video!.UsesHardwareDecoding = false;
        Assert.Equal(DecodeMode.Cpu, player.Stats.DecodeMode);
        Assert.Equal("CPU", player.Stats.DecodeModeText);
    }

    [Theory]
    [InlineData(1920, 1080, 8, 16, 6)]
    [InlineData(3840, 2160, 10, 16, 6)]
    [InlineData(7680, 4320, 10, 4, 1)]
    public void Cache_plan_respects_the_memory_cap(int width, int height, int bitDepth, int frames, int prefetch)
    {
        var video = Samples.Info(Rate, 10, width: width, height: height, bitDepth: bitDepth).Video!;

        CachePlan plan = ClipPlayer.PlanCache(video, new ClipPlayerOptions());

        Assert.Equal(frames, plan.Frames);
        Assert.Equal(prefetch, plan.PrefetchFrames);
        Assert.True(plan.BudgetBytes <= new ClipPlayerOptions().MaxCacheBytes);
    }

    [Fact]
    public void A_cache_too_small_for_the_prefetch_is_rejected()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 10));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => backend.Open(new ClipPlayerOptions { PrefetchFrames = 8, CacheFrames = 12 }));
        Assert.Contains("18", ex.Message, StringComparison.Ordinal);
    }

    // Regresión: con la caché LRU más chica que 2 × adelanto, cada cuadro adelantado se expulsaba antes de
    // mostrarse y el decodificador volvía al keyframe en cada refresco.
    [Fact]
    public void Steady_playback_requests_every_frame_once_and_in_order()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 300));
        using ClipPlayer player = backend.Open();
        player.Play();
        ManualClock clock = backend.Output!.ManualClock;

        for (int i = 0; i < 200; i++)
        {
            FramePresentation shown = player.Tick();
            Assert.True(shown.IsExact, $"El cuadro {i} no estaba listo.");
            clock.Advance(Time.FromFrames(1, Rate));
        }

        IReadOnlyList<long> requests = backend.Video!.Requests;
        Assert.Equal(requests.Count, requests.Distinct().Count());
        Assert.True(requests.SequenceEqual(requests.Order()), "El decodificador recibió pedidos hacia atrás.");
        PlaybackStats stats = player.Stats.Playback;
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
    }

    [Fact]
    public void Step_frame_pauses_and_moves_exactly_one_frame()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        using ClipPlayer player = backend.Open();
        player.Play();
        backend.Output!.ManualClock.Advance(Time.FromFrames(10, Rate) + Time.FromTicks(1000));

        player.StepFrame(1);
        Assert.False(player.IsPlaying);
        Assert.Equal(11, player.CurrentFrame);
        Assert.Equal(11, player.Tick().Frame!.Index);

        player.StepFrame(-3);
        Assert.Equal(8, player.CurrentFrame);
        Assert.True(player.Tick().IsExact);
    }

    [Fact]
    public void Steps_and_seeks_are_clamped_to_the_clip()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 50));
        using ClipPlayer player = backend.Open();

        player.StepFrame(-1);
        Assert.Equal(0, player.CurrentFrame);
        player.SeekToFrame(1000);
        Assert.Equal(49, player.CurrentFrame);
        player.StepFrame(1);
        Assert.Equal(49, player.CurrentFrame);
        player.Seek(-Time.FromSeconds(3));
        Assert.Equal(0, player.CurrentFrame);
        Assert.Equal(4, player.Stats.Seeks);
    }

    [Fact]
    public void Seek_latency_is_measured_until_the_requested_frame_is_shown()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        using ClipPlayer player = backend.Open();

        player.SeekToFrame(40);
        player.Tick();

        ClipPlayerStats stats = player.Stats;
        Assert.Equal(1, stats.Seeks);
        Assert.True(stats.MaxSeekLatency >= stats.AverageSeekLatency);
    }

    [Fact]
    public void Play_at_the_end_starts_over()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 20));
        using ClipPlayer player = backend.Open();
        bool ended = false;
        player.EndReached += (_, _) => ended = true;
        player.Play();
        backend.Output!.ManualClock.Advance(Time.FromFrames(25, Rate));
        player.Tick();
        Assert.True(ended);
        Assert.False(player.IsPlaying);

        player.Play();

        Assert.True(player.IsPlaying);
        Assert.Equal(0, player.CurrentFrame);
    }

    [Fact]
    public void Dispose_stops_audio_before_its_decoder_and_frees_every_frame()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100));
        ClipPlayer player = backend.Open();
        player.Play();
        player.Tick();

        player.Dispose();
        player.Dispose();

        IReadOnlyList<string> order = backend.Log.Entries;
        Assert.Equal(["executor", "output", "pcm", "video"], order);
        Assert.Equal(0, backend.Video!.LiveFrames);
        Assert.Throws<ObjectDisposedException>(() => player.Tick());
    }

    [Fact]
    public void A_failure_while_opening_releases_what_was_already_open()
    {
        var backend = new FakeBackend(Samples.Info(Rate, 100)) { OutputOpenFailure = new InvalidOperationException("sin WASAPI") };

        Assert.Throws<InvalidOperationException>(() => backend.Open());

        Assert.Equal(["pcm", "video"], backend.Log.Entries);
    }
}
