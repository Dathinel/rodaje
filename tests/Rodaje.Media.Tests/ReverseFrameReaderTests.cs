using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>
/// Lectura hacia atrás (fase 5c.3): un clip entero da N-1…0 exactos frente a la referencia de ffmpeg.exe, con GOP
/// largos y cortos y cuadros B, en una pasada o en varias cuando el GOP no cabe en <c>M</c>, y cuánto decodifica.
/// </summary>
public sealed class ReverseFrameReaderTests(ITestOutputHelper output)
{
    /// <summary>GOP de 250: con <c>M</c> = 64 hay que partirlo en varias pasadas.</summary>
    internal static readonly ClipSpec LongGop = new("h264-25-g250-bf2", ClipCodec.H264, 25, 1, 300, Gop: 250);

    /// <summary>GOP de 6: varios GOP enteros van en una sola pasada.</summary>
    internal static readonly ClipSpec ShortGop = new("h264-25-g6-bf2", ClipCodec.H264, 25, 1, 120, Gop: 6);

    private static readonly ClipSpec[] Clips = [TestClips.H264, TestClips.Hevc, TestClips.H264Mkv, LongGop, ShortGop];

    public static TheoryData<string, DecodeMode, int> SoftwareCases
    {
        get
        {
            var data = new TheoryData<string, DecodeMode, int>();
            foreach ((string clip, DecodeMode mode) in Pairs(DecodeModes.Software(Clips)))
            {
                data.Add(clip, mode, 128);
                data.Add(clip, mode, 16);
            }

            return data;
        }
    }

    public static TheoryData<string, int> HardwareCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            // Con D3D11VA lo retenido sale del pool, que admite 64 superficies con las referencias dentro.
            foreach (ClipSpec clip in Clips)
            {
                data.Add(clip.Name, 40);
                data.Add(clip.Name, 16);
            }

            return data;
        }
    }

    [Fact]
    public void The_plan_joins_whole_gops_that_fit_and_splits_the_rest_from_the_top()
    {
        var gop30 = new KeyframeIndex(Enumerable.Range(0, 10).Select(k => k * 30L), Rational.Fps30, 300, KeyframeIndexSource.PacketScan);
        IReadOnlyList<long[]> joined = ReverseFrameReader.Plan(gop30, 0, 299, 64);
        Assert.Equal(5, joined.Count);
        Assert.Equal(Range(240, 299), joined[0]);
        Assert.Equal(Range(0, 59), joined[^1]);

        var gop100 = new KeyframeIndex([0, 100], Rational.Fps30, 200, KeyframeIndexSource.PacketScan);
        IReadOnlyList<long[]> split = ReverseFrameReader.Plan(gop100, 0, 199, 32);
        Assert.Equal(Range(168, 199), split[0]);
        Assert.Equal(Range(136, 167), split[1]);
        Assert.Equal(Range(104, 135), split[2]);

        // Lo que sobra del GOP de arriba (100–103) es su propia pasada, corta: juntarlo con el de abajo obligaría a
        // decodificar ese GOP entero una vez más.
        Assert.Equal(Range(100, 103), split[3]);
        Assert.Equal(Range(68, 99), split[4]);
        Assert.Equal(Range(0, 3), split[^1]);
        Assert.Equal(8, split.Count);

        // Un resto pequeño sí se junta con los GOP enteros de abajo que quepan.
        var mixed = new KeyframeIndex([0, 10, 50], Rational.Fps30, 100, KeyframeIndexSource.PacketScan);
        IReadOnlyList<long[]> merged = ReverseFrameReader.Plan(mixed, 0, 99, 32);
        Assert.Equal([Range(68, 99), Range(50, 67), Range(18, 49), Range(0, 17)], merged);

        IReadOnlyList<long[]> even = ReverseFrameReader.Plan(gop30, 10, 95, 64, i => i % 2 == 0);
        Assert.All(even.SelectMany(c => c), i => Assert.True(i % 2 == 0));
        Assert.Equal(43, even.Sum(c => c.Length));
    }

    [Fact]
    public void Any_plan_covers_exactly_what_is_wanted_in_descending_chunks()
    {
        var random = new Random(53);
        for (int round = 0; round < 500; round++)
        {
            long count = random.Next(1, 600);
            var keys = new SortedSet<long> { 0 };
            for (int k = random.Next(0, 20); k > 0; k--)
            {
                keys.Add(random.NextInt64(count));
            }

            var index = new KeyframeIndex(keys, Rational.Fps25, count, KeyframeIndexSource.PacketScan);
            long first = random.NextInt64(count);
            long last = first + random.NextInt64(count - first);
            int chunk = random.Next(1, 80);
            int step = random.Next(1, 5);
            IReadOnlyList<long[]> plan = ReverseFrameReader.Plan(index, first, last, chunk, i => i % step == 0);

            long[] flat = [.. plan.SelectMany(c => c.Reverse())];
            long[] expected = [.. Enumerable.Range(0, (int)(last - first + 1)).Select(i => last - i).Where(i => i % step == 0)];
            Assert.Equal(expected, flat);
            Assert.All(plan, c => Assert.InRange(c.Length, 1, chunk));
            Assert.All(plan, c => Assert.Equal(c.Order(), c));
        }
    }

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void A_whole_clip_backwards_is_exact_software(string clip, DecodeMode mode, int maxRetained) =>
        WholeClipBackwards(Clip(clip), DecodeModes.Options(mode), mode.ToString(), maxRetained);

    [HardwareTheory]
    [MemberData(nameof(HardwareCases))]
    public void A_whole_clip_backwards_is_exact_hardware(string clip, int maxRetained) =>
        WholeClipBackwards(Clip(clip), DecodeModes.Options(DecodeMode.Hardware) with
        {
            ExtraHardwareFrames = maxRetained + 4,
            CompactReferencePool = true,
        }, "D3D11VA", maxRetained);

    [FfmpegLibraryFact]
    public void Only_the_wanted_frames_are_kept_and_the_reading_is_deterministic()
    {
        ClipSpec spec = TestClips.H264;
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        string path = TestClips.PathOf(spec);
        KeyframeIndex keyframes = KeyframeIndex.Read(path);
        var runs = new List<long[]>();
        foreach (bool lookahead in new[] { true, false })
        {
            using VideoDecoder decoder = VideoDecoder.Open(path, DecodeModes.Options(DecodeMode.SoftwareCpu));
            using var reader = new ReverseFrameReader(decoder, keyframes, 20, 250,
                new ReverseFrameReaderOptions { MaxRetained = 24, Lookahead = lookahead, Wanted = i => i % 2 == 1 });
            var delivered = new List<long>();
            while (reader.Next() is { } frame)
            {
                using (frame)
                {
                    Assert.Equal(reference[(int)frame.Index], Hash(frame));
                    delivered.Add(frame.Index);
                }
            }

            runs.Add([.. delivered]);
        }

        Assert.Equal(Enumerable.Range(0, 115).Select(i => 249L - (2 * i)), runs[0]);
        Assert.Equal(runs[0], runs[1]);
    }

    [FfmpegLibraryFact]
    public void Disposing_midway_releases_every_frame_it_holds()
    {
        ClipSpec spec = TestClips.H264;
        string path = TestClips.PathOf(spec);
        using VideoDecoder decoder = VideoDecoder.Open(path, DecodeModes.Options(DecodeMode.SoftwareCpu));
        var reader = new ReverseFrameReader(decoder, KeyframeIndex.Read(path), 0, spec.Frames - 1, new ReverseFrameReaderOptions { MaxRetained = 64 });
        reader.Next()!.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.Next());

        // El decodificador queda libre: se puede volver a usar.
        using DecodedFrame? again = decoder.GetFrame(5);
        Assert.Equal(5, again!.Index);
    }

    /// <summary>
    /// 1080p H.264 con el número de cuadro dibujado (GOP 60 con cuadros B) recorrido entero hacia atrás por D3D11VA:
    /// cada cuadro lleva su número, y lo que tarda dice si la reversa llega a 1× sin proxy.
    /// </summary>
    [HardwareFact(VideoCodec.H264)]
    public void A_1080p_clip_backwards_shows_its_drawn_numbers_faster_than_real_time()
    {
        MediaSpec spec = TestMediaPresets.H264Ntsc30BFrames;
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(spec).Path;
        KeyframeIndex keyframes = KeyframeIndex.Read(path);
        const int MaxRetained = 48;
        using VideoDecoder decoder = VideoDecoder.Open(path, DecodeModes.Options(DecodeMode.Hardware) with
        {
            ExtraHardwareFrames = MaxRetained + 4,
            CompactReferencePool = true,
        });
        using var reader = new ReverseFrameReader(decoder, keyframes, 0, spec.FrameCount - 1, new ReverseFrameReaderOptions { MaxRetained = MaxRetained });
        var watch = Stopwatch.StartNew();
        long expected = spec.FrameCount - 1;
        var wrong = new List<string>();
        while (reader.Next() is { } frame)
        {
            using (frame)
            {
                LumaImage luma = FrameReadback.ReadLuma(frame);
                int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
                if (frame.Index != expected || code != expected)
                {
                    wrong.Add($"esperado {expected}: índice {frame.Index}, código {code}");
                }
            }

            expected--;
        }

        double seconds = watch.Elapsed.TotalSeconds;
        double perFrame = (double)reader.DecodedFrames / spec.FrameCount;
        output.WriteLine($"1080p hacia atrás: {spec.FrameCount} cuadros en {seconds:F2} s ({spec.FrameCount / seconds:F0} fps, " +
            $"{spec.FrameCount / seconds / 29.97:F1}× el tiempo real, con la lectura de la luma), {perFrame:F2} decodificaciones por cuadro, " +
            $"{reader.Chunks.Count} tramos, GOP de {keyframes.MaxInterval}.");
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong.Take(20)));
        Assert.Equal(-1, expected);
        Assert.Equal(0, decoder.Statistics.DecodeErrors);
        long planned = reader.Chunks.Sum(c => c[^1] - (keyframes.AtOrBefore(c[0]) ?? 0) + 1);
        Assert.InRange(reader.DecodedFrames, spec.FrameCount, planned + (8L * reader.Chunks.Count));
    }

    private void WholeClipBackwards(ClipSpec spec, VideoDecoderOptions options, string mode, int maxRetained)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        string path = TestClips.PathOf(spec);
        KeyframeIndex keyframes = KeyframeIndex.Read(path);
        int frames = reference.Count;
        using VideoDecoder decoder = VideoDecoder.Open(path, options);
        using var reader = new ReverseFrameReader(decoder, keyframes, 0, frames - 1, new ReverseFrameReaderOptions { MaxRetained = maxRetained });
        var wrong = new List<string>();
        long expected = frames - 1;
        while (reader.Next() is { } frame)
        {
            using (frame)
            {
                if (frame.Index != expected || Hash(frame) != reference[(int)expected])
                {
                    wrong.Add($"esperado {expected}, entregado {frame.Index}");
                }
            }

            expected--;
        }

        // Lo que cuesta cada tramo: desde su keyframe hasta su cuadro más alto, más el reordenamiento de los cuadros B.
        long planned = reader.Chunks.Sum(c => c[^1] - (keyframes.AtOrBefore(c[0]) ?? 0) + 1);
        double perFrame = (double)reader.DecodedFrames / frames;
        output.WriteLine($"{spec.Name} [{mode}, M = {maxRetained}]: {frames} cuadros, {reader.Chunks.Count} tramos, " +
            $"{reader.DecodedFrames} decodificados ({perFrame:F2} por cuadro; el plan pide {planned}), GOP de {keyframes.MaxInterval}.");
        Assert.True(wrong.Count == 0, $"{spec.Name} [{mode}]: {string.Join("; ", wrong.Take(20))}");
        Assert.Equal(-1, expected);
        Assert.Equal(frames, reader.Delivered);
        Assert.InRange(reader.DecodedFrames, frames, planned + (8L * reader.Chunks.Count));
        if (maxRetained / 2 >= keyframes.MaxInterval)
        {
            Assert.True(perFrame <= 1.25, $"Con los GOP enteros en un tramo, {perFrame:F2} decodificaciones por cuadro.");
        }
    }

    private static ClipSpec Clip(string name) => Clips.Single(c => c.Name == name);

    private static IEnumerable<(string, DecodeMode)> Pairs(TheoryData<string, DecodeMode> data) =>
        data.Select(row => ((string)row[0], (DecodeMode)row[1]));

    private static long[] Range(long from, long to) => [.. Enumerable.Range((int)from, (int)(to - from + 1)).Select(i => (long)i)];

    private static ulong Hash(DecodedFrame frame) => TestClips.HashLuma(FrameReadback.ReadLuma(frame).Pixels);
}
