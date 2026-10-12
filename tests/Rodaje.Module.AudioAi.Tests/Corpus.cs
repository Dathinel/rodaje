using System.Buffers.Binary;

namespace Rodaje.Module.AudioAi.Tests;

/// <summary>
/// Lo que las pruebas leen de <c>.entorno</c> (nunca del repo): los modelos de <c>herramientas\preparar-modelos.ps1</c> y,
/// con <c>-Corpus</c>, voz real (LibriVox de dominio público a 48 kHz y FLEURS CC-BY-4.0 a 16 kHz). Sin ellos las pruebas
/// que los necesitan no comprueban nada y lo dicen en la salida.
/// </summary>
internal static class Corpus
{
    public static string? Environment { get; } = FindEnvironment();

    public static ModelFiles Models { get; } = new(Environment is null ? ModelFiles.DefaultFolders() : [Path.Combine(Environment, "modelos")]);

    public static bool HasModels => Has(ModelFiles.DfnEncoder) && Has(ModelFiles.DfnErbDecoder) && Has(ModelFiles.DfnDfDecoder) && Has(ModelFiles.SileroVad);

    public static string? Voice48k => Existing(Environment is null ? null : Path.Combine(Environment, "corpus", "audio-ia", "voz-48k.wav"));

    public static string? FleursFolder => Environment is { } e && Directory.Exists(Path.Combine(e, "corpus", "fleurs-es", "dev")) ? Path.Combine(e, "corpus", "fleurs-es", "dev") : null;

    public const string Missing = "Sin modelos o corpus en .entorno: ejecuta herramientas\\preparar-modelos.ps1 -Corpus.";

    /// <summary>Mono f32 de un WAV PCM de 16 bits o IEEE float (mezcla los canales).</summary>
    public static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int format = 0, channels = 0, rate = 0, bits = 0;
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 8));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 12));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 22));
                if (format == 0xFFFE)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 32));
                }
            }
            else if (id == "data")
            {
                int length = Math.Min(size, bytes.Length - at - 8);
                int frameBytes = channels * bits / 8;
                int frames = length / frameBytes;
                var mono = new float[frames];
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int p = at + 8 + (i * frameBytes) + (c * bits / 8);
                        sum += format == 3 ? BitConverter.ToSingle(bytes, p) : BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(p)) / 32768f;
                    }

                    mono[i] = sum / channels;
                }

                return (mono, rate);
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path} no tiene datos de audio.");
    }

    /// <summary>Las primeras <paramref name="count"/> frases de FLEURS de la lista (16 kHz mono), o <c>null</c> sin corpus.</summary>
    public static List<float[]>? Fleurs(int count)
    {
        if (FleursFolder is not { } folder)
        {
            return null;
        }

        return [.. Directory.GetFiles(folder, "*.wav").Order(StringComparer.Ordinal).Take(count).Select(f =>
        {
            (float[] samples, int rate) = ReadWav(f);
            return rate == 16_000 ? samples : throw new InvalidDataException($"{f}: {rate} Hz");
        })];
    }

    /// <summary>16 → 48 kHz con un sinc enventanado (Hann, 48 coeficientes por fase).</summary>
    public static float[] Upsample3(float[] x)
    {
        const int Half = 24;
        var y = new float[x.Length * 3];
        for (int n = 0; n < y.Length; n++)
        {
            double center = n / 3.0;
            int first = (int)Math.Floor(center) - Half + 1;
            double sum = 0;
            for (int k = first; k < first + (2 * Half); k++)
            {
                if (k < 0 || k >= x.Length)
                {
                    continue;
                }

                double d = center - k;
                double sinc = Math.Abs(d) < 1e-12 ? 1 : Math.Sin(Math.PI * d) / (Math.PI * d);
                double window = 0.5 + (0.5 * Math.Cos(Math.PI * d / Half));
                sum += x[k] * sinc * window;
            }

            y[n] = (float)sum;
        }

        return y;
    }

    private static bool Has(ModelFile model)
    {
        try
        {
            Models.Path(model);
            return true;
        }
        catch (ModelMissingException)
        {
            return false;
        }
    }

    private static string? Existing(string? path) => path is not null && File.Exists(path) ? path : null;

    private static string? FindEnvironment()
    {
        if (System.Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } custom && Directory.Exists(custom))
        {
            return custom;
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
