using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Rodaje.Module.Subtitles.Tests;

/// <summary>Una frase del corpus puesta en la pista de prueba: dónde empieza y dónde empieza la voz.</summary>
internal sealed record Utterance(string Text, double Offset, double SpeechStart, double SpeechEnd);

/// <summary>
/// Voz real para medir: FLEURS es_419 dev (CC-BY-4.0, Google), que baja <c>herramientas\preparar-modelos.ps1 -Corpus</c>
/// a <c>.entorno\corpus\fleurs-es</c>. Las frases se ponen seguidas con silencios conocidos entre ellas: el inicio
/// verdadero de la primera palabra de cada frase es el silencio más el arranque de la voz medido por energía.
/// </summary>
internal static class Corpus
{
    public const int Rate = 16000;

    /// <summary>La carpeta <c>.entorno</c>: <c>RODAJE_ENTORNO</c> o la primera que aparezca hacia arriba (también desde un worktree).</summary>
    public static string? Environment
    {
        get
        {
            if (System.Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } env && Directory.Exists(env))
            {
                return env;
            }

            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".entorno");
                if (Directory.Exists(Path.Combine(candidate, "modelos")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static string? ModelPath =>
        Environment is { } env && Path.Combine(env, "modelos", "ggml-large-v3-turbo-q5_0.bin") is var p && File.Exists(p) ? p : null;

    public static string? FleursDirectory =>
        Environment is { } env && Path.Combine(env, "corpus", "fleurs-es") is var d && File.Exists(Path.Combine(d, ".audio-dev")) ? d : null;

    /// <summary>Frases de FLEURS hasta sumar <paramref name="seconds"/>, con <paramref name="gap"/> s de silencio entre ellas.</summary>
    public static (float[] Samples, List<Utterance> Utterances) Build(double seconds, double gap = 0.8)
    {
        string dir = FleursDirectory ?? throw new InvalidOperationException("Falta el corpus FLEURS.");
        List<float> samples = [];
        List<Utterance> utterances = [];
        AddSilence(samples, gap);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(Path.Combine(dir, "dev.tsv"), Encoding.UTF8))
        {
            string[] cols = line.Split('\t');
            string wav = Path.Combine(dir, "dev", cols[1]);
            // FLEURS repite cada frase con varias voces: una por frase, como en un video real.
            if (cols.Length < 3 || !File.Exists(wav) || !seen.Add(cols[0]))
            {
                continue;
            }

            float[] audio = ReadWav(wav);
            (double on, double off) = SpeechBounds(audio);
            double offset = (double)samples.Count / Rate;
            samples.AddRange(audio);
            utterances.Add(new Utterance(cols[2], offset, offset + on, offset + off));
            AddSilence(samples, gap);
            if ((double)samples.Count / Rate >= seconds)
            {
                break;
            }
        }

        return ([.. samples], utterances);
    }

    /// <summary>WAV PCM de 16 bits o flotante de 32, mono, a 16 kHz.</summary>
    public static float[] ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int format = 0, channels = 0, rate = 0, bits = 0;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            string id = Encoding.ASCII.GetString(bytes, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            ReadOnlySpan<byte> body = bytes.AsSpan(at + 8, Math.Min(size, bytes.Length - at - 8));
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != Rate)
                {
                    throw new InvalidDataException($"{path}: {channels} canales a {rate} Hz");
                }

                return (format, bits) switch
                {
                    (3, 32) => [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(body)],
                    (1, 16) => [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(body).ToArray().Select(s => s / 32768f)],
                    _ => throw new InvalidDataException($"{path}: formato {format} de {bits} bits"),
                };
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: sin datos");
    }

    /// <summary>
    /// Arranque y final de la voz: el primer y el último tramo de 50 ms seguidos por encima de
    /// max(pico − 35 dB, fondo + 15 dB). El fondo (percentil 20 de la frase) cuenta porque varias grabaciones de FLEURS
    /// traen ruido de sala o un clic al empezar; sin él, la «verdad» caía en el ruido y no en la voz.
    /// </summary>
    public static (double On, double Off) SpeechBounds(float[] audio)
    {
        const int Window = Rate / 100;
        int count = audio.Length / Window;
        double[] rms = new double[count];
        for (int w = 0; w < count; w++)
        {
            double sum = 0;
            for (int i = w * Window; i < (w + 1) * Window; i++)
            {
                sum += audio[i] * audio[i];
            }

            rms[w] = 10 * Math.Log10((sum / Window) + 1e-12);
        }

        double[] sorted = [.. rms.Where(d => d > -90).Order()];
        if (sorted.Length == 0)
        {
            return (0, (double)audio.Length / Rate);
        }

        double threshold = Math.Max(sorted[^1] - 35, sorted[sorted.Length / 5] + 15);
        const int Sustain = 5;
        int first = -1, last = -1;
        for (int w = 0, run = 0; w < count; w++)
        {
            run = rms[w] > threshold ? run + 1 : 0;
            if (run >= Sustain)
            {
                if (first < 0)
                {
                    first = w - Sustain + 1;
                }

                last = w;
            }
        }

        return first < 0 ? (0, (double)audio.Length / Rate) : (first * 0.01, (last + 1) * 0.01);
    }

    private static void AddSilence(List<float> samples, double seconds) => samples.AddRange(new float[(int)(seconds * Rate)]);

    /// <summary>Minúsculas, sin puntuación ni marcas de diacríticos que no cambian la palabra; separa por espacios.</summary>
    public static string[] Normalize(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text.ToLowerInvariant().Normalize(NormalizationForm.FormC))
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Alinea por distancia de edición y devuelve el WER y, para cada palabra de la referencia, el índice de la palabra
    /// de la hipótesis con la que coincide exacta (o −1).
    /// </summary>
    public static (double Wer, int[] Match) Align(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        int n = reference.Count, m = hypothesis.Count;
        int[,] d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int sub = d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1);
                d[i, j] = Math.Min(sub, Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
            }
        }

        int[] match = Enumerable.Repeat(-1, n).ToArray();
        for (int i = n, j = m; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1))
            {
                if (reference[i - 1] == hypothesis[j - 1])
                {
                    match[i - 1] = j - 1;
                }

                i--;
                j--;
            }
            else if (i > 0 && d[i, j] == d[i - 1, j] + 1)
            {
                i--;
            }
            else
            {
                j--;
            }
        }

        return (n == 0 ? 0 : (double)d[n, m] / n, match);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        double rank = p / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    public static string Ms(double seconds) => (seconds * 1000).ToString("0", CultureInfo.InvariantCulture) + " ms";
}
