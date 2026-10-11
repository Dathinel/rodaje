using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

/// <summary>
/// Peticiones aproximadas de alta velocidad: descartan cuadros solo cuando se piden así y con la opción activa, cada cuadro
/// lleva su índice real, y una petición exacta (pausa, seek, <see cref="IVideoFrameSource"/>) nunca sale de un descarte.
/// </summary>
public sealed class HighSpeedDecodeTests
{
    public static TheoryData<string, DecodeMode> SoftwareCases => DecodeModes.Software(TestClips.H264, TestClips.Hevc, TestClips.H264Mkv);

    public static TheoryData<string> AllClips => new(TestClips.H264.Name, TestClips.Hevc.Name, TestClips.H264Mkv.Name);

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void Exact_requests_never_skip_software(string clip, DecodeMode mode) => ExactNeverSkips(DecodeModes.Clip(clip), mode);

    [HardwareTheory]
    [MemberData(nameof(AllClips))]
    public void Exact_requests_never_skip_hardware(string clip) => ExactNeverSkips(DecodeModes.Clip(clip), DecodeMode.Hardware);

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void Skipping_non_reference_frames_decodes_less_and_labels_truthfully_software(string clip, DecodeMode mode) =>
        SkipsNonReference(DecodeModes.Clip(clip), mode);

    [HardwareTheory]
    [MemberData(nameof(AllClips))]
    public void Skipping_non_reference_frames_decodes_less_and_labels_truthfully_hardware(string clip) =>
        SkipsNonReference(DecodeModes.Clip(clip), DecodeMode.Hardware);

    [FfmpegLibraryTheory]
    [MemberData(nameof(SoftwareCases))]
    public void Keyframe_mode_delivers_only_keyframes_software(string clip, DecodeMode mode) => DeliversKeyframes(DecodeModes.Clip(clip), mode);

    [HardwareTheory]
    [MemberData(nameof(AllClips))]
    public void Keyframe_mode_delivers_only_keyframes_hardware(string clip) => DeliversKeyframes(DecodeModes.Clip(clip), DecodeMode.Hardware);

    [FfmpegLibraryFact]
    public void Without_the_option_fast_requests_are_exact()
    {
        ClipSpec spec = TestClips.H264;
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), DecodeModes.Options(DecodeMode.SoftwareCpu));
        for (int n = 0; n < spec.Frames; n += 3)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, n % 2 == 0 ? DecodeAccuracy.Keyframe : DecodeAccuracy.SkipNonReference);
            Assert.NotNull(frame);
            Assert.Equal(n, frame.Index);
            Assert.Equal(reference[n], Hash(frame));
        }
    }

    private static void ExactNeverSkips(ClipSpec spec, DecodeMode mode)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), Fast(mode));
        var random = new Random(spec.Frames);
        var failures = new List<string>();
        for (int round = 0; round < 12; round++)
        {
            // Unos cuadros a alta velocidad desde un punto cualquiera y después la pausa o un seek exacto.
            int start = random.Next(spec.Frames - 40);
            DecodeAccuracy fast = round % 3 == 2 ? DecodeAccuracy.Keyframe : DecodeAccuracy.SkipNonReference;
            for (int n = start; n < start + 30; n += 2 + (round % 3))
            {
                decoder.GetFrame(n, fast)?.Dispose();
            }

            long seeksBefore = decoder.Statistics.Seeks;
            int[] exact = round % 2 == 0
                ? [start + 31, start + 32, start + 33]
                : [random.Next(spec.Frames), start + 29];
            foreach (int n in exact)
            {
                using DecodedFrame? frame = n == exact[0] && round % 4 == 1
                    ? ((IVideoFrameSource)decoder).GetFrameAsync(n, CancellationToken.None).AsTask().GetAwaiter().GetResult()
                    : decoder.GetFrame(n);
                Assert.NotNull(frame);
                if (frame.Index != n || Hash(frame) != reference[n])
                {
                    failures.Add($"ronda {round}: pedido {n}, entregado {frame.Index}");
                }
            }

            Assert.True(decoder.Statistics.Seeks > seeksBefore, $"Ronda {round}: tras descartar, la petición exacta debe buscar de nuevo.");
        }

        Assert.True(failures.Count == 0, $"{spec.Name} [{mode}]: {string.Join("; ", failures)}");
    }

    private static void SkipsNonReference(ClipSpec spec, DecodeMode mode)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        long exactDecoded = DecodedAt2x(spec, mode, DecodeAccuracy.Exact, reference, out int exactApproximations);
        long skipDecoded = DecodedAt2x(spec, mode, DecodeAccuracy.SkipNonReference, reference, out int approximations);
        Assert.Equal(0, exactApproximations);
        Assert.True(approximations > 0, $"{spec.Name} [{mode}]: ningún cuadro pedido fue de los descartados.");
        Assert.True(skipDecoded * 10 < exactDecoded * 8,
            $"{spec.Name} [{mode}]: {skipDecoded} cuadros decodificados descartando y {exactDecoded} sin descartar.");
    }

    private static long DecodedAt2x(ClipSpec spec, DecodeMode mode, DecodeAccuracy accuracy, IReadOnlyList<ulong> reference, out int approximations)
    {
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), Fast(mode));
        approximations = 0;
        long previous = -1;
        for (int n = 0; n < spec.Frames; n += 2)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, accuracy);
            Assert.NotNull(frame);
            Assert.InRange(frame.Index, Math.Max(0, previous), n);
            Assert.True(reference[(int)frame.Index] == Hash(frame), $"{spec.Name} [{mode}]: el cuadro entregado no es el {frame.Index} (pedido {n}).");
            approximations += frame.Index < n ? 1 : 0;
            previous = frame.Index;
        }

        Assert.Equal(1, decoder.Statistics.Seeks);
        return decoder.Statistics.DecodedFrames;
    }

    private static void DeliversKeyframes(ClipSpec spec, DecodeMode mode)
    {
        IReadOnlyList<ulong> reference = TestClips.LumaReference(spec);
        IReadOnlyList<int> keys = TestClips.KeyFrames(spec);
        using VideoDecoder decoder = VideoDecoder.Open(TestClips.PathOf(spec), Fast(mode));
        for (int n = 0; n < spec.Frames; n += 16)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, DecodeAccuracy.Keyframe);
            Assert.NotNull(frame);
            int expected = keys.Last(k => k <= n);
            Assert.Equal(expected, frame.Index);
            Assert.Equal(reference[expected], Hash(frame));
        }

        Assert.True(decoder.Statistics.DecodedFrames <= keys.Count + 1,
            $"{spec.Name} [{mode}]: {decoder.Statistics.DecodedFrames} cuadros decodificados con {keys.Count} keyframes.");
    }

    private static VideoDecoderOptions Fast(DecodeMode mode) => DecodeModes.Options(mode) with { SkipNonReference = true };

    private static ulong Hash(DecodedFrame frame) => TestClips.HashLuma(FrameReadback.ReadLuma(frame).Pixels);
}
