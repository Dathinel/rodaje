using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>
/// Cada cuadro entregado, en orden o por acceso aleatorio, debe tener exactamente el mismo plano Y que el
/// cuadro con ese número en la decodificación secuencial de referencia de ffmpeg.exe.
/// </summary>
public sealed class VideoDecoderAccuracyTests(ITestOutputHelper output)
{
    private const int RandomRequests = 200;

    public static TheoryData<string, DecodeMode> SoftwareCases =>
        DecodeModes.Software(TestClips.H264, TestClips.Hevc, TestClips.Hevc10, TestClips.H264Mkv);

    public static TheoryData<string> AllClips => new(TestClips.H264.Name, TestClips.Hevc.Name, TestClips.Hevc10.Name, TestClips.H264Mkv.Name);

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void Sequential_decode_matches_reference_software(string clip, DecodeMode mode) =>
        SequentialMatches(DecodeModes.Clip(clip), mode);

    [HardwareTheory]
    [MemberData(nameof(AllClips))]
    public void Sequential_decode_matches_reference_hardware(string clip) =>
        SequentialMatches(DecodeModes.Clip(clip), DecodeMode.Hardware);

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void Random_access_matches_reference_software(string clip, DecodeMode mode) =>
        RandomAccessMatches(DecodeModes.Clip(clip), mode);

    [HardwareTheory]
    [MemberData(nameof(AllClips))]
    public void Random_access_matches_reference_hardware(string clip) =>
        RandomAccessMatches(DecodeModes.Clip(clip), DecodeMode.Hardware);

    private void SequentialMatches(ClipSpec spec, DecodeMode mode)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        Assert.Equal(spec.Frames, reference.Count);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), DecodeModes.Options(mode));
        Assert.Equal(spec.Frames, decoder.FrameCount);
        var rate = new Rational(spec.RateNum, spec.RateDen);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int n = 0; n < reference.Count; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
            Assert.Equal(n, frame.Index);
            Assert.Equal(Time.FromFrames(n, rate), frame.Pts);
            Assert.Equal(Time.FromFrames(n + 1, rate) - Time.FromFrames(n, rate), frame.Duration);
            AssertPayload(frame, mode);
            Assert.True(reference[n] == Hash(frame), $"El cuadro {n} no coincide con la referencia ({spec.Name}, {mode}).");
        }

        watch.Stop();
        Assert.Null(decoder.GetFrame(reference.Count));
        Assert.Equal(1, decoder.Statistics.Seeks);
        Assert.Equal(mode == DecodeMode.Hardware, decoder.UsesHardwareDecoding);
        output.WriteLine($"{spec.Name} [{mode}]: {reference.Count} cuadros en orden, con lectura de luma, en {watch.ElapsedMilliseconds} ms.");
    }

    private void RandomAccessMatches(ClipSpec spec, DecodeMode mode)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        IReadOnlyList<int> keys = TestClips.KeyFrames(spec);
        Assert.True(keys.Count >= 3, $"El clip debería tener varios keyframes y tiene {keys.Count}.");
        List<int> requests = RandomRequestOrder(reference.Count, keys);

        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), DecodeModes.Options(mode));
        var failures = new List<int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (int n in requests)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.True(frame is not null, $"El cuadro {n} no se entregó ({spec.Name}, {mode}).");
            Assert.Equal(n, frame.Index);
            if (reference[n] != Hash(frame))
            {
                failures.Add(n);
            }
        }

        watch.Stop();
        Assert.True(failures.Count == 0,
            $"{failures.Count} de {requests.Count} cuadros no coinciden con la referencia ({spec.Name}, {mode}): {string.Join(", ", failures.Take(20))}");
        output.WriteLine($"{spec.Name} [{mode}]: {requests.Count} accesos aleatorios exactos en {watch.ElapsedMilliseconds} ms; " +
            $"keyframes {string.Join(",", keys)}; {decoder.Statistics}.");
    }

    /// <summary>Primero los casos de borde (0, último, keyframes y sus vecinos) y después aleatorios, todo mezclado.</summary>
    internal static List<int> RandomRequestOrder(int frames, IReadOnlyList<int> keys)
    {
        var random = new Random(20261009);
        var requests = new List<int> { 0, frames - 1, frames - 2, 1 };
        foreach (int k in keys)
        {
            requests.AddRange(new[] { k - 1, k, k + 1, k + 2 }.Where(i => i >= 0 && i < frames));
        }

        while (requests.Count < RandomRequests)
        {
            requests.Add(random.Next(frames));
        }

        requests = requests.Take(RandomRequests).ToList();
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(requests));

        // Además, pares consecutivos y repetidos para cubrir el camino secuencial y la repetición.
        int[] probe = [requests[0], requests[1]];
        foreach (int start in probe)
        {
            if (start + 1 < frames)
            {
                requests.Add(start);
                requests.Add(start + 1);
                requests.Add(start + 1);
            }
        }

        return requests;
    }

    private static ulong Hash(DecodedFrame frame) => TestClips.HashLuma(FrameReadback.ReadLuma(frame).Pixels);

    private static void AssertPayload(DecodedFrame frame, DecodeMode mode)
    {
        switch (mode)
        {
            case DecodeMode.Hardware:
                var hw = Assert.IsType<D3D11TexturePayload>(frame.Payload);
                Assert.True(hw.IsDecoderSurface);
                break;
            case DecodeMode.SoftwareUpload:
                var up = Assert.IsType<D3D11TexturePayload>(frame.Payload);
                Assert.False(up.IsDecoderSurface);
                break;
            default:
                Assert.IsType<CpuFramePayload>(frame.Payload);
                break;
        }
    }
}
