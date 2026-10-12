using System.Buffers.Binary;

namespace Rodaje.Packages.Content;

/// <summary>Lo que dice la cabecera de un WAV de un elemento <c>sound</c>.</summary>
/// <param name="SampleRate">Muestras por segundo, de 8 a 192 kHz.</param>
/// <param name="Channels">De 1 a 8.</param>
/// <param name="BitsPerSample">16, 24 o 32.</param>
/// <param name="IsFloat">Muestras en coma flotante de 32 bits (si no, PCM entero).</param>
/// <param name="Frames">Muestras por canal.</param>
public sealed record SoundInfo(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, long Frames)
{
    public const long MaxFileBytes = 64L * 1024 * 1024;

    public double DurationSeconds => (double)Frames / SampleRate;
}

/// <summary>
/// Lector de los WAV de los elementos <c>sound</c>: RIFF/WAVE con PCM de 16, 24 o 32 bits o float de 32, de 8 a 192 kHz y
/// de 1 a 8 canales (también <c>WAVE_FORMAT_EXTENSIBLE</c> con esos subformatos). Como los otros lectores, no confía en el
/// archivo y lanza <see cref="PackageContentException"/> con el motivo.
/// </summary>
internal static class WaveReader
{
    private const ushort Pcm = 1;
    private const ushort IeeeFloat = 3;
    private const ushort Extensible = 0xFFFE;

    public static SoundInfo ReadInfo(Stream stream) => Read(stream, samples: false).Info;

    public static (SoundInfo Info, float[] Samples) ReadSamples(Stream stream) => Read(stream, samples: true);

    private static (SoundInfo Info, float[] Samples) Read(Stream stream, bool samples)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length > SoundInfo.MaxFileBytes)
        {
            throw Fail("El sonido pesa más de 64 MB.");
        }

        Span<byte> header = stackalloc byte[12];
        if (!ReadExactly(stream, header) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw Fail("No es un WAV (falta la cabecera RIFF/WAVE).");
        }

        (int rate, int channels, int bits, bool isFloat)? format = null;
        Span<byte> chunk = stackalloc byte[8];
        long consumed = 12;
        while (ReadExactly(stream, chunk))
        {
            consumed += 8;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (consumed + size > SoundInfo.MaxFileBytes)
            {
                throw Fail("El sonido pesa más de 64 MB o tiene un bloque con un tamaño imposible.");
            }

            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (size is < 16 or > 64)
                {
                    throw Fail("El bloque fmt del WAV tiene un tamaño imposible.");
                }

                byte[] fmt = new byte[size];
                if (!ReadExactly(stream, fmt))
                {
                    throw Fail("El WAV está cortado en el bloque fmt.");
                }

                format = Format(fmt);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (format is not { } f)
                {
                    throw Fail("El WAV trae los datos antes del bloque fmt.");
                }

                int frameBytes = f.channels * (f.bits / 8);
                long frames = size / frameBytes;
                if (frames == 0)
                {
                    throw Fail("El WAV no trae muestras.");
                }

                var info = new SoundInfo(f.rate, f.channels, f.bits, f.isFloat, frames);
                if (!samples)
                {
                    return (info, []);
                }

                byte[] data = new byte[frames * frameBytes];
                if (!ReadExactly(stream, data))
                {
                    throw Fail("El WAV está cortado: trae menos muestras de las que declara.");
                }

                return (info, Decode(data, f.bits, f.isFloat));
            }
            else
            {
                Skip(stream, size + (size & 1));
            }

            consumed += size + (size & 1);
        }

        throw Fail("El WAV no trae el bloque de datos.");
    }

    private static (int Rate, int Channels, int Bits, bool Float) Format(byte[] fmt)
    {
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
        int rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
        if (tag == Extensible)
        {
            if (fmt.Length < 40)
            {
                throw Fail("El bloque fmt extensible del WAV está incompleto.");
            }

            tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
        }

        bool isFloat = tag == IeeeFloat;
        if (!(tag == Pcm && bits is 16 or 24 or 32) && !(isFloat && bits == 32))
        {
            throw Fail("El WAV tiene que ser PCM de 16, 24 o 32 bits o float de 32.");
        }

        if (rate is < 8_000 or > 192_000)
        {
            throw Fail("La frecuencia del WAV tiene que ir de 8 a 192 kHz.");
        }

        if (channels is < 1 or > 8)
        {
            throw Fail("El WAV tiene que tener de 1 a 8 canales.");
        }

        return (rate, channels, bits, isFloat);
    }

    private static float[] Decode(byte[] data, int bits, bool isFloat)
    {
        int bytes = bits / 8;
        float[] samples = new float[data.Length / bytes];
        for (int i = 0, at = 0; i < samples.Length; i++, at += bytes)
        {
            samples[i] = isFloat ? BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(at))
                : bits switch
                {
                    16 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at)) / 32768f,
                    24 => ((data[at] << 8 | data[at + 1] << 16 | data[at + 2] << 24) >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)) / 2147483648f,
                };
            if (!float.IsFinite(samples[i]))
            {
                samples[i] = 0;
            }
        }

        return samples;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        byte[] buffer = new byte[8192];
        while (count > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    private static PackageContentException Fail(string message) => new(message);
}
