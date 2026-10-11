using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

public sealed class KeyframeIndexTests
{
    public static TheoryData<string> Clips => new(TestClips.H264.Name, TestClips.Hevc.Name, TestClips.H264Mkv.Name);

    [FfmpegLibraryTheory]
    [MemberData(nameof(Clips))]
    [TenBitClipData(software: false)]
    public void Both_sources_find_the_decoded_keyframes(string clip)
    {
        ClipSpec spec = DecodeModes.Clip(clip);
        string path = TestClips.PathOf(spec);
        long[] expected = TestClips.KeyFrames(spec).Select(k => (long)k).ToArray();

        KeyframeIndex index = KeyframeIndex.Read(path);
        KeyframeIndex scanned = KeyframeIndex.Read(path, useContainerIndex: false);

        Assert.Equal(spec.Extension == "mp4" ? KeyframeIndexSource.ContainerIndex : KeyframeIndexSource.PacketScan, index.Source);
        Assert.Equal(KeyframeIndexSource.PacketScan, scanned.Source);
        Assert.Equal(expected, index.Frames);
        Assert.Equal(expected, scanned.Frames);
        Assert.Equal(spec.Frames, index.FrameCount);
        Assert.Equal(new Rational(spec.RateNum, spec.RateDen), index.FrameRate);

        // El intervalo real sale de los keyframes decodificados: sin NVENC el HEVC lo genera libkvazaar, que no respeta el GOP.
        long longest = spec.Frames - expected[^1];
        for (int i = 1; i < expected.Length; i++)
        {
            longest = Math.Max(longest, expected[i] - expected[i - 1]);
        }

        Assert.Equal(longest, index.MaxInterval);
        if (Rodaje.TestMedia.Skip.Encoder(TestClips.ExactEncoder(spec.Codec)) is null)
        {
            Assert.Equal(spec.Gop, index.MaxInterval);
        }
    }

    [FfmpegLibraryFact]
    public void Variable_frame_rate_uses_the_decoder_numbering()
    {
        string path = TestClips.VariableFrameRate();
        KeyframeIndex index = KeyframeIndex.Read(path);
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { SkipNonReference = true });
        foreach (long key in index.Frames)
        {
            using DecodedFrame? frame = decoder.GetFrame(key + 1, DecodeAccuracy.Keyframe);
            Assert.NotNull(frame);
            Assert.Equal(key, frame.Index);
        }
    }

    [Fact]
    public void Lookups_cover_the_edges()
    {
        var index = new KeyframeIndex([48, 0, 96, 48, -3], new Rational(25, 1), 120, KeyframeIndexSource.PacketScan);

        Assert.Equal([0L, 48, 96], index.Frames);
        Assert.Equal(0, index.AtOrBefore(0));
        Assert.Equal(0, index.AtOrBefore(47));
        Assert.Equal(48, index.AtOrBefore(48));
        Assert.Equal(96, index.AtOrBefore(1_000));
        Assert.Null(index.AtOrBefore(-1));
        Assert.Equal(48, index.After(0));
        Assert.Equal(96, index.After(50));
        Assert.Null(index.After(96));
        Assert.Equal((48L, 96L), index.GopOf(60));
        Assert.Equal((96L, 120L), index.GopOf(119));
        Assert.Equal(48, index.MaxInterval);

        var late = new KeyframeIndex([10], new Rational(25, 1), 0, KeyframeIndexSource.PacketScan);
        Assert.Equal(11, late.FrameCount);
        Assert.Equal((10L, 11L), late.GopOf(3));
        Assert.Equal(1, late.MaxInterval);

        var empty = new KeyframeIndex([], new Rational(25, 1), 30, KeyframeIndexSource.PacketScan);
        Assert.Null(empty.AtOrBefore(5));
        Assert.Equal((0L, 30L), empty.GopOf(5));
        Assert.Equal(30, empty.MaxInterval);
    }

    [FfmpegLibraryFact]
    public void Reading_can_be_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string path = TestClips.PathOf(TestClips.H264Mkv);
        Assert.Throws<OperationCanceledException>(() => KeyframeIndex.Read(path, cancellationToken: cancelled.Token));
    }

    [FfmpegLibraryFact]
    public void A_file_without_video_is_rejected() =>
        Assert.Throws<MediaException>(() => KeyframeIndex.Read(TestClips.FlacStereo48k()));
}
