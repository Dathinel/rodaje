using System.Diagnostics;
using System.Globalization;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests;

/// <summary>
/// HEVC con GOP abierto (<see cref="OpenGopMediaPresets.HevcOpenGop"/>, de libkvazaar): IDR en 0 y CRA con cuadros RASL.
/// Seek exacto, acceso aleatorio, modo Keyframe y descarte de no referenciados, por software (con hilos y con uno solo,
/// que es como decodifica D3D11VA) y por hardware donde lo hay.
/// </summary>
public sealed class OpenGopDecodeTests
{
    private const string Kvazaar = OpenGopMediaPresets.Encoder;

    private static readonly Lazy<string> ClipPath = new(() =>
        TestMediaGenerator.CreateDefault()!.GetOrCreate(OpenGopMediaPresets.HevcOpenGop).Path);

    private static readonly Lazy<IReadOnlyList<ulong>> Reference = new(ReadReference);

    private static MediaSpec Spec => OpenGopMediaPresets.HevcOpenGop;

    [FfmpegLibraryFact(Encoder = Kvazaar)]
    public void The_preset_has_cra_keyframes_with_leading_frames()
    {
        IReadOnlyList<long> keys = KeyframeIndex.Read(ClipPath.Value).Frames;
        Assert.True(keys.Count >= 3 && keys[0] == 0, $"Keyframes: {string.Join(",", keys)}.");

        // Paquetes en orden de decodificación: tras cada keyframe que no es el primero llegan cuadros que se muestran
        // antes que él (los RASL). Eso es lo que hace abierto al GOP.
        List<(long Pts, bool Key)> packets = Packets(ClipPath.Value);
        int keyPackets = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            if (!packets[i].Key || i == 0)
            {
                continue;
            }

            keyPackets++;
            bool leading = packets.Skip(i + 1).TakeWhile(p => !p.Key).Any(p => p.Pts < packets[i].Pts);
            Assert.True(leading, $"El keyframe con pts {packets[i].Pts} no tiene cuadros iniciales: el GOP no es abierto.");
        }

        Assert.True(keyPackets >= 2, $"Solo hay {keyPackets} keyframes después del primero.");
        Assert.Equal(Spec.FrameCount, Reference.Value.Count);
    }

    [FfmpegLibraryFact(Encoder = Kvazaar)]
    public void Random_access_matches_reference_software()
    {
        foreach ((string mode, VideoDecoderOptions options) in SoftwareModes())
        {
            RandomAccessMatches(mode, options);
        }
    }

    [HardwareFact(Encoder = Kvazaar)]
    public void Random_access_matches_reference_hardware() =>
        RandomAccessMatches("hardware", DecodeModes.Options(DecodeMode.Hardware));

    [FfmpegLibraryFact(Encoder = Kvazaar)]
    public void Keyframe_mode_delivers_each_cra_software()
    {
        foreach ((string mode, VideoDecoderOptions options) in SoftwareModes())
        {
            DeliversKeyframes(mode, options);
        }
    }

    [HardwareFact(Encoder = Kvazaar)]
    public void Keyframe_mode_delivers_each_cra_hardware() =>
        DeliversKeyframes("hardware", DecodeModes.Options(DecodeMode.Hardware));

    [FfmpegLibraryFact(Encoder = Kvazaar)]
    public void Skipping_non_reference_frames_labels_truthfully_software()
    {
        foreach ((string mode, VideoDecoderOptions options) in SoftwareModes())
        {
            SkipsTruthfully(mode, options);
        }
    }

    [HardwareFact(Encoder = Kvazaar)]
    public void Skipping_non_reference_frames_labels_truthfully_hardware() =>
        SkipsTruthfully("hardware", DecodeModes.Options(DecodeMode.Hardware));

    [FfmpegLibraryFact(Encoder = Kvazaar)]
    public void Exact_requests_after_fast_ones_are_exact_software()
    {
        foreach ((string mode, VideoDecoderOptions options) in SoftwareModes())
        {
            ExactAfterFast(mode, options);
        }
    }

    [HardwareFact(Encoder = Kvazaar)]
    public void Exact_requests_after_fast_ones_are_exact_hardware() =>
        ExactAfterFast("hardware", DecodeModes.Options(DecodeMode.Hardware));

    // Con un solo hilo el decodificador HEVC suelta varios cuadros de una vez a su cola de salida; es el camino de D3D11VA.
    private static IEnumerable<(string Mode, VideoDecoderOptions Options)> SoftwareModes()
    {
        yield return ("cpu", new VideoDecoderOptions());
        yield return ("cpu-1-hilo", new VideoDecoderOptions { SoftwareThreads = 1 });
        if (MediaTestEnvironment.Gpu is not null)
        {
            yield return ("subida", DecodeModes.Options(DecodeMode.SoftwareUpload));
        }
    }

    private static void RandomAccessMatches(string mode, VideoDecoderOptions options)
    {
        IReadOnlyList<ulong> reference = Reference.Value;
        List<int> keys = [.. KeyframeIndex.Read(ClipPath.Value).Frames.Select(k => (int)k)];
        List<int> requests = VideoDecoderAccuracyTests.RandomRequestOrder(reference.Count, keys);

        // Lo que sigue al final del flujo es lo que dejaba cuadros viejos en la salida: se pide también el último y luego atrás.
        requests.AddRange([reference.Count - 1, keys[^1] + 24, reference.Count - 3, keys[1] - 1, keys[1]]);
        using VideoDecoder decoder = VideoDecoder.Open(ClipPath.Value, options);
        var failures = new List<string>();
        foreach (int n in requests)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            if (frame is null)
            {
                failures.Add($"{n}: null");
            }
            else if (frame.Index != n || Hash(frame) != reference[n])
            {
                failures.Add($"{n}: entregado {frame.Index}");
            }
        }

        Assert.True(failures.Count == 0,
            $"[{mode}] {failures.Count} de {requests.Count} peticiones exactas fallan: {string.Join("; ", failures.Take(20))}");
    }

    private static void DeliversKeyframes(string mode, VideoDecoderOptions options)
    {
        IReadOnlyList<ulong> reference = Reference.Value;
        KeyframeIndex keyIndex = KeyframeIndex.Read(ClipPath.Value);
        IReadOnlyList<long> keys = keyIndex.Frames;
        foreach (KeyframeIndex? index in new[] { null, keyIndex })
        {
            using VideoDecoder decoder = VideoDecoder.Open(ClipPath.Value, options with { SkipNonReference = true, Keyframes = index });
            for (int n = 0; n < reference.Count; n += 8)
            {
                using DecodedFrame? frame = decoder.GetFrame(n, DecodeAccuracy.Keyframe);
                Assert.NotNull(frame);
                long expected = keys.Last(k => k <= n);
                Assert.True(frame.Index == expected, $"[{mode}, índice: {index is not null}] pedido {n}: entregado {frame.Index}, se esperaba {expected}.");
                Assert.Equal(reference[(int)expected], Hash(frame));
            }

            long limit = index is null ? keys.Count + 1 : keys.Count;
            Assert.True(decoder.Statistics.DecodedFrames <= limit,
                $"[{mode}, índice: {index is not null}]: {decoder.Statistics.DecodedFrames} cuadros decodificados con {keys.Count} keyframes.");
        }
    }

    // libkvazaar no deja cuadros sin referenciar, así que no se exige que descarte nada: solo que lo entregado sea verdad.
    private static void SkipsTruthfully(string mode, VideoDecoderOptions options)
    {
        IReadOnlyList<ulong> reference = Reference.Value;
        using VideoDecoder decoder = VideoDecoder.Open(ClipPath.Value, options with { SkipNonReference = true });
        long previous = -1;
        for (int n = 0; n < reference.Count; n += 2)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, DecodeAccuracy.SkipNonReference);
            Assert.NotNull(frame);
            Assert.InRange(frame.Index, Math.Max(0, previous), n);
            Assert.True(reference[(int)frame.Index] == Hash(frame), $"[{mode}] el cuadro entregado no es el {frame.Index} (pedido {n}).");
            previous = frame.Index;
        }

        Assert.Equal(1, decoder.Statistics.Seeks);
    }

    private static void ExactAfterFast(string mode, VideoDecoderOptions options)
    {
        IReadOnlyList<ulong> reference = Reference.Value;
        IReadOnlyList<long> keys = KeyframeIndex.Read(ClipPath.Value).Frames;
        using VideoDecoder decoder = VideoDecoder.Open(ClipPath.Value, options with { SkipNonReference = true });
        var random = new Random(64);
        var failures = new List<string>();
        for (int round = 0; round < 12; round++)
        {
            int start = random.Next(reference.Count - 40);
            DecodeAccuracy fast = round % 2 == 0 ? DecodeAccuracy.Keyframe : DecodeAccuracy.SkipNonReference;
            for (int n = start; n < start + 30; n += 3)
            {
                decoder.GetFrame(n, fast)?.Dispose();
            }

            // Un keyframe CRA, un RASL suyo (anterior en presentación) y un cuadro cualquiera.
            int key = (int)keys[1 + (round % (keys.Count - 1))];
            foreach (int n in new[] { key, key - 1 - (round % 15), random.Next(reference.Count) })
            {
                using DecodedFrame? frame = decoder.GetFrame(n);
                if (frame is null || frame.Index != n || Hash(frame) != reference[n])
                {
                    failures.Add($"ronda {round}: pedido {n}, entregado {frame?.Index.ToString(CultureInfo.InvariantCulture) ?? "null"}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"[{mode}] {string.Join("; ", failures)}");
    }

    private static ulong Hash(DecodedFrame frame) => TestClips.HashLuma(FrameReadback.ReadLuma(frame).Pixels);

    /// <summary>Hash del plano Y de cada cuadro según ffmpeg.exe, comprobando de paso el número dibujado.</summary>
    private static List<ulong> ReadReference()
    {
        int width = Spec.Width;
        int height = Spec.Height;
        var hashes = new List<ulong>();
        FfmpegTool.Default!.Run(
            ["-v", "error", "-i", ClipPath.Value, "-map", "0:v:0", "-fps_mode", "passthrough", "-vf", "extractplanes=y", "-f", "rawvideo", "-"],
            readOutput: stdout =>
            {
                var buffer = new byte[width * height];
                while (true)
                {
                    int total = 0;
                    while (total < buffer.Length && stdout.Read(buffer, total, buffer.Length - total) is var read and > 0)
                    {
                        total += read;
                    }

                    if (total < buffer.Length)
                    {
                        break;
                    }

                    Assert.Equal(hashes.Count, FrameCode.Read(buffer, width, height, width));
                    hashes.Add(TestClips.HashLuma(buffer));
                }
            });
        return hashes;
    }

    /// <summary>Paquetes de video en orden de decodificación: marca de presentación y si es keyframe.</summary>
    private static List<(long Pts, bool Key)> Packets(string path)
    {
        var info = new ProcessStartInfo(MediaTestEnvironment.FfprobeExe)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts,flags", "-of", "csv=p=0", path })
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("No se pudo iniciar ffprobe.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Select(f => (long.Parse(f[0], CultureInfo.InvariantCulture), f[1].StartsWith('K')))
            .ToList();
    }
}
