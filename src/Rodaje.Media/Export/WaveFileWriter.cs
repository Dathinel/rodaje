using System.Buffers.Binary;

namespace Rodaje.Media.Export;

/// <summary>
/// Escribe el WAV temporal de la mezcla de corrido: RIFF mientras cabe en 4 GB y RF64 (EBU Tech 3306) si pasa. La
/// cabecera reserva un trozo <c>JUNK</c> del tamaño de <c>ds64</c> para convertirlo al cerrar sin mover los datos.
/// </summary>
/// <remarks>
/// Recibe float intercalado (la mezcla de Rodaje) y lo guarda en el <see cref="ExportSampleFormat"/> pedido, con
/// saturación en los enteros. Sin <see cref="Complete"/>, <see cref="Dispose"/> borra el archivo.
/// </remarks>
public sealed class WaveFileWriter : IDisposable
{
    private const int Ds64Size = 28;
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    private readonly FileStream _stream;
    private readonly long _dataStart;
    private readonly long _rf64Threshold;
    private readonly byte[] _scratch = new byte[64 * 1024];
    private long _dataBytes;
    private bool _completed;
    private bool _disposed;

    public WaveFileWriter(string path, ExportAudioFormat format)
        : this(path, format, uint.MaxValue)
    {
    }

    /// <param name="path">Ruta del WAV; se crea o se reemplaza.</param>
    /// <param name="format">Frecuencia, canales y muestras.</param>
    /// <param name="rf64Threshold">Tamaño del RIFF a partir del cual se escribe RF64; menor solo en las pruebas.</param>
    internal WaveFileWriter(string path, ExportAudioFormat format, long rf64Threshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        Path = System.IO.Path.GetFullPath(path);
        Format = format;
        _rf64Threshold = rf64Threshold;
        _stream = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        try
        {
            WriteHeader();
            _dataStart = _stream.Position;
        }
        catch
        {
            _stream.Dispose();
            File.Delete(Path);
            throw;
        }
    }

    public string Path { get; }

    public ExportAudioFormat Format { get; }

    /// <summary>Muestras por canal escritas.</summary>
    public long SamplesPerChannel => _dataBytes / BlockAlign;

    /// <summary>Si al cerrar quedó en RF64.</summary>
    public bool IsRf64 { get; private set; }

    private int BlockAlign => Format.BytesPerSample * Format.Channels;

    /// <summary>Añade muestras intercaladas (un múltiplo de los canales).</summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.DiskFull"/> si el disco se llenó.</exception>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("El WAV ya se cerró.");
        }

        if (interleaved.Length % Format.Channels != 0)
        {
            throw new ArgumentException($"Llegaron {interleaved.Length} muestras, que no es múltiplo de {Format.Channels} canales.", nameof(interleaved));
        }

        int bytesPerSample = Format.BytesPerSample;
        int perChunk = _scratch.Length / bytesPerSample;
        while (!interleaved.IsEmpty)
        {
            int count = Math.Min(perChunk, interleaved.Length);
            int bytes = Encode(interleaved[..count], _scratch);
            WriteChecked(_scratch.AsSpan(0, bytes));
            _dataBytes += bytes;
            interleaved = interleaved[count..];
        }
    }

    /// <summary>Escribe los tamaños (o la cabecera RF64) y cierra el archivo.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        if ((_dataBytes & 1) == 1)
        {
            WriteChecked([0]);
        }

        long riffSize = _stream.Length - 8;
        Span<byte> field = stackalloc byte[8];
        if (riffSize > _rf64Threshold)
        {
            IsRf64 = true;
            _stream.Position = 0;
            WriteAscii("RF64");
            WriteUInt32(uint.MaxValue);
            _stream.Position = 12;
            WriteAscii("ds64");
            WriteUInt32(Ds64Size);
            BinaryPrimitives.WriteInt64LittleEndian(field, riffSize);
            _stream.Write(field);
            BinaryPrimitives.WriteInt64LittleEndian(field, _dataBytes);
            _stream.Write(field);
            BinaryPrimitives.WriteInt64LittleEndian(field, SamplesPerChannel);
            _stream.Write(field);
            WriteUInt32(0);
            _stream.Position = _dataStart - 4;
            WriteUInt32(uint.MaxValue);
        }
        else
        {
            _stream.Position = 4;
            WriteUInt32((uint)riffSize);
            _stream.Position = _dataStart - 4;
            WriteUInt32((uint)_dataBytes);
        }

        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        _completed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
        if (!_completed)
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    private void WriteHeader()
    {
        bool extensible = Format.Channels > 2;
        ushort tag = Format.SampleFormat == ExportSampleFormat.PcmFloat ? FormatFloat : FormatPcm;
        WriteAscii("RIFF");
        WriteUInt32(0);
        WriteAscii("WAVE");
        WriteAscii("JUNK");
        WriteUInt32(Ds64Size);
        _stream.Write(new byte[Ds64Size]);
        WriteAscii("fmt ");
        WriteUInt32(extensible ? 40u : tag == FormatFloat ? 18u : 16u);
        WriteUInt16(extensible ? FormatExtensible : tag);
        WriteUInt16((ushort)Format.Channels);
        WriteUInt32((uint)Format.SampleRate);
        WriteUInt32((uint)(Format.SampleRate * BlockAlign));
        WriteUInt16((ushort)BlockAlign);
        WriteUInt16((ushort)(Format.BytesPerSample * 8));
        if (extensible)
        {
            WriteUInt16(22);
            WriteUInt16((ushort)(Format.BytesPerSample * 8));
            WriteUInt32(ChannelMask(Format.Channels));
            // GUID del subformato: los 2 primeros bytes son la etiqueta, el resto es fijo.
            WriteUInt16(tag);
            _stream.Write([0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71]);
        }
        else if (tag == FormatFloat)
        {
            WriteUInt16(0);
        }

        WriteAscii("data");
        WriteUInt32(0);
    }

    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        6 => 0x3F,
        8 => 0x63F,
        _ => (1u << channels) - 1,
    };

    private int Encode(ReadOnlySpan<float> samples, Span<byte> destination)
    {
        switch (Format.SampleFormat)
        {
            case ExportSampleFormat.PcmFloat:
                for (int i = 0; i < samples.Length; i++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(destination[(i * 4)..], samples[i]);
                }

                return samples.Length * 4;
            case ExportSampleFormat.Pcm16:
                for (int i = 0; i < samples.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(i * 2)..], (short)Quantize(samples[i], short.MaxValue));
                }

                return samples.Length * 2;
            case ExportSampleFormat.Pcm24:
                for (int i = 0; i < samples.Length; i++)
                {
                    int value = (int)Quantize(samples[i], 8_388_607);
                    destination[i * 3] = (byte)value;
                    destination[(i * 3) + 1] = (byte)(value >> 8);
                    destination[(i * 3) + 2] = (byte)(value >> 16);
                }

                return samples.Length * 3;
            default:
                for (int i = 0; i < samples.Length; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], (int)Quantize(samples[i], int.MaxValue));
                }

                return samples.Length * 4;
        }
    }

    private static long Quantize(float sample, long max)
    {
        double value = float.IsNaN(sample) ? 0 : Math.Clamp((double)sample, -1.0, 1.0);
        return Math.Clamp((long)Math.Round(value * max), -max - 1, max);
    }

    private void WriteChecked(ReadOnlySpan<byte> data)
    {
        try
        {
            _stream.Write(data);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new ExportException(ExportErrorCode.DiskFull, $"El disco se llenó al escribir el audio temporal ({Path}).", ex);
        }
    }

    // ERROR_DISK_FULL (112) y ERROR_HANDLE_DISK_FULL (39).
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;

    private void WriteAscii(string text)
    {
        Span<byte> bytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
        {
            bytes[i] = (byte)text[i];
        }

        _stream.Write(bytes);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        _stream.Write(bytes);
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _stream.Write(bytes);
    }
}
