using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Rodaje.Module.Voice.Synthesis;

/// <summary>
/// Kokoro-82M v1.0 en ONNX Runtime (CPU): fonemas → ids del vocabulario, vector de estilo de la voz según el largo,
/// audio f32 mono a 24 kHz. La sesión se carga una vez; <see cref="Run"/> se puede cortar con el token.
/// </summary>
internal sealed class KokoroEngine : IDisposable
{
    public const int SampleRate = 24_000;
    public const int MaxTokens = 510;
    private const int StyleSize = 256;

    // Vocabulario de hexgrad/Kokoro-82M (config.json, Apache-2.0).
    private static readonly Dictionary<char, long> Vocab = BuildVocab();

    private readonly InferenceSession _session;
    private readonly ConcurrentDictionary<string, float[]> _voices = new(StringComparer.Ordinal);
    private readonly string _root;

    public KokoroEngine(string root, int threads = 0)
    {
        _root = root;
        using SessionOptions options = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = threads > 0 ? threads : DefaultThreads(),
            InterOpNumThreads = 1,
        };
        string model = Environment.GetEnvironmentVariable("RODAJE_VOZ_MODELO") is { Length: > 0 } m ? m : Voices.ModelFile;
        _session = new InferenceSession(Path.Combine(root, model), options);
    }

    public static int DefaultThreads() =>
        int.TryParse(Environment.GetEnvironmentVariable("RODAJE_VOZ_HILOS"), out int n) && n > 0 ? n : Math.Clamp(Environment.ProcessorCount / 2, 1, 16);

    public static bool IsKnown(char c) => Vocab.ContainsKey(c);

    public static int CountTokens(string phonemes) => phonemes.Count(Vocab.ContainsKey);

    public float[] Voice(string id) => _voices.GetOrAdd(id, v =>
    {
        byte[] bytes = File.ReadAllBytes(Voices.VoiceFile(_root, v));
        if (bytes.Length % (StyleSize * 4) != 0 || bytes.Length == 0) throw new InvalidDataException($"La voz {v} está dañada.");
        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    });

    /// <summary>Habla <paramref name="phonemes"/> (como mucho <see cref="MaxTokens"/> símbolos conocidos).</summary>
    public float[] Run(string phonemes, string voice, float speed, CancellationToken cancellationToken)
    {
        List<long> ids = [0];
        foreach (char c in phonemes)
            if (Vocab.TryGetValue(c, out long id)) ids.Add(id);
        int count = ids.Count - 1;
        if (count == 0) return [];
        if (count > MaxTokens) throw new ArgumentException($"Demasiados fonemas en un trozo ({count} > {MaxTokens}).", nameof(phonemes));
        ids.Add(0);

        float[] styles = Voice(voice);
        int rows = styles.Length / StyleSize;
        float[] style = styles.AsSpan(Math.Min(count, rows - 1) * StyleSize, StyleSize).ToArray();

        using RunOptions run = new();
        using CancellationTokenRegistration reg = cancellationToken.Register(() => run.Terminate = true);
        cancellationToken.ThrowIfCancellationRequested();
        using OrtValue input = OrtValue.CreateTensorValueFromMemory(ids.ToArray(), [1, ids.Count]);
        using OrtValue styleValue = OrtValue.CreateTensorValueFromMemory(style, [1, StyleSize]);
        using OrtValue speedValue = OrtValue.CreateTensorValueFromMemory(new[] { speed }, [1]);
        try
        {
            using IDisposableReadOnlyCollection<OrtValue> outputs = _session.Run(run,
                ["input_ids", "style", "speed"], [input, styleValue, speedValue], ["waveform"]);
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public void Dispose() => _session.Dispose();

    private static Dictionary<char, long> BuildVocab()
    {
        const string chars = ";:,.!?—…\"()“” ̃ʣʥʦʨᵝꭧAIOQSTWYᵊabcdefhijklmnopqrstuvwxyzɑɐɒæβɔɕçɖðʤəɚɛɜɟɡɥɨɪʝɯɰŋɳɲɴøɸθœɹɾɻʁɽʂʃʈʧʊʋʌɣɤχʎʒʔˈˌːʰʲ↓→↗↘ᵻ";
        long[] ids =
        [
            1, 2, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 31, 33, 35, 36, 39, 41, 42, 43, 44, 45,
            46, 47, 48, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 75, 76, 77, 78, 80,
            81, 82, 83, 85, 86, 87, 90, 92, 99, 101, 102, 103, 110, 111, 112, 113, 114, 115, 116, 118, 119, 120, 123, 125, 126, 128,
            129, 130, 131, 132, 133, 135, 136, 138, 139, 140, 142, 143, 147, 148, 156, 157, 158, 162, 164, 169, 171, 172, 173, 177,
        ];
        if (chars.Length != ids.Length) throw new InvalidOperationException("Vocabulario de Kokoro inconsistente.");
        Dictionary<char, long> vocab = [];
        for (int i = 0; i < chars.Length; i++) vocab[chars[i]] = ids[i];
        return vocab;
    }
}
