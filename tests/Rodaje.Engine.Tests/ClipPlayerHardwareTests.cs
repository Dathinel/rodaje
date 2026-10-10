using System.Diagnostics;
using Rodaje.Media;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Engine.Tests;

public sealed class ClipPlayerHardwareTests
{
    [HardwareFact(VideoCodec.H264, Audio = AudioCodec.Aac)]
    public void Opens_a_generated_clip_and_shows_exact_frames_from_the_gpu()
    {
        GeneratedMedia clip = TestMediaGenerator.CreateDefault()!.GetOrCreate(TestMediaPresets.H264Ntsc24Aac);
        using GpuDevice gpu = GpuDevice.Create();
        using ClipPlayer player = ClipPlayer.Open(clip.Path, new ClipPlayerOptions { Device = gpu, EnableAudio = false });

        Assert.Equal(clip.Spec.FrameCount, player.FrameCount);
        Assert.False(player.HasAudioDevice);
        foreach (long frame in new long[] { 0, 100, 37, 500, 501 })
        {
            player.SeekToFrame(frame);
            FramePresentation shown = TickUntilExact(player);
            Assert.Equal(frame, shown.Frame!.Index);
            var texture = Assert.IsType<D3D11TexturePayload>(shown.Frame.Payload);
            Assert.Equal(FramePixelFormat.Nv12, texture.PixelFormat);
            Assert.Equal(1920, texture.Width);
            Assert.True(texture.CodedHeight >= 1080);
        }

        player.Play();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            player.Tick();
            Thread.Sleep(16);
        }

        player.Pause();
        ClipPlayerStats stats = player.Stats;
        Assert.Equal(DecodeMode.Gpu, stats.DecodeMode);
        Assert.True(stats.Playback.FramesShown >= 5, $"Solo se mostraron {stats.Playback.FramesShown} cuadros en 0,5 s.");
        Assert.Equal(0, stats.Playback.DecodeErrors);
    }

    private static FramePresentation TickUntilExact(ClipPlayer player)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            FramePresentation shown = player.Tick();
            if (shown.IsExact && !player.IsSeekPending)
            {
                return shown;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"El cuadro {shown.TargetFrame} no llegó en 5 s.");
            Thread.Sleep(2);
        }
    }
}
