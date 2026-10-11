using System.Buffers.Binary;
using Rodaje.Media.Export;

namespace Rodaje.Engine.Offline;

/// <summary>
/// Escribe un WAV (<c>WAVE_FORMAT_EXTENSIBLE</c>) por bloques en un temporal junto al destino y lo renombra al terminar.
/// Reserva un trozo <c>JUNK</c> del tamaño de <c>ds64</c>: si los datos pasan de 4 GB, el archivo se cierra como RF64.
/// </summary>
internal sealed class WaveWriter : IDisposable
{
    private const int HeaderBytes = 12 + 36 + 48 + 8;
    private const int Ds64Bytes = 28;

    private static readonly Guid PcmFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly string _path;
    private readonly string _temporary;
    private readonly ExportAudioFormat _format;
    private FileStream? _stream;
    private byte[] _bytes = [];
    private long _dataBytes;
    private bool _completed;

    public WaveWriter(string path, ExportAudioFormat format)
    {
        _path = Path.GetFullPath(path);
        _temporary = _path + ".rodaje-tmp";
        _format = format;
        _stream = new FileStream(_temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, rf64: false, 0);
        _stream.Write(header);
    }

    public string TemporaryPath => _temporary;

    public long Frames => _dataBytes / (_format.Channels * _format.BytesPerSample);

    /// <summary>Convierte y escribe muestras intercaladas en float (ya con los canales de la salida), recortando a ±1 los enteros.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        FileStream stream = _stream ?? throw new ObjectDisposedException(nameof(WaveWriter));
        int size = _format.BytesPerSample;
        int bytes = interleaved.Length * size;
        if (_bytes.Length < bytes)
        {
            _bytes = new byte[bytes];
        }

        Span<byte> output = _bytes.AsSpan(0, bytes);
        for (int i = 0; i < interleaved.Length; i++)
        {
            float sample = interleaved[i];
            Span<byte> at = output.Slice(i * size, size);
            switch (_format.SampleFormat)
            {
                case ExportSampleFormat.Pcm16:
                    BinaryPrimitives.WriteInt16LittleEndian(at, (short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
                    break;
                case ExportSampleFormat.Pcm24:
                    int v = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 8_388_607.0);
                    at[0] = (byte)v;
                    at[1] = (byte)(v >> 8);
                    at[2] = (byte)(v >> 16);
                    break;
                case ExportSampleFormat.Pcm32:
                    BinaryPrimitives.WriteInt32LittleEndian(at, (int)Math.Round(Math.Clamp((double)sample, -1, 1) * int.MaxValue));
                    break;
                default:
                    BinaryPrimitives.WriteSingleLittleEndian(at, sample);
                    break;
            }
        }

        stream.Write(output);
        _dataBytes += bytes;
    }

    /// <summary>Cierra los tamaños (RIFF o RF64), baja al disco y renombra el temporal al destino.</summary>
    public void Complete()
    {
        FileStream stream = _stream ?? throw new ObjectDisposedException(nameof(WaveWriter));
        if ((_dataBytes & 1) == 1)
        {
            stream.WriteByte(0);
        }

        long riff = HeaderBytes - 8 + _dataBytes + (_dataBytes & 1);
        bool rf64 = riff > uint.MaxValue;
        Span<byte> header = stackalloc byte[HeaderBytes];
        WriteHeader(header, rf64, riff);
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
        stream.Dispose();
        _stream = null;
        File.Move(_temporary, _path, overwrite: true);
        _completed = true;
    }

    /// <summary>Sin <see cref="Complete"/>, borra el temporal.</summary>
    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_completed)
        {
            try
            {
                File.Delete(_temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void WriteHeader(Span<byte> header, bool rf64, long riff)
    {
        int channels = _format.Channels;
        int bits = _format.BytesPerSample * 8;
        int block = channels * _format.BytesPerSample;
        "RIFF"u8.CopyTo(header);
        if (rf64)
        {
            "RF64"u8.CopyTo(header);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], rf64 ? uint.MaxValue : (uint)riff);
        "WAVE"u8.CopyTo(header[8..]);

        Span<byte> chunk = header[12..];
        (rf64 ? "ds64"u8 : "JUNK"u8).CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], Ds64Bytes);
        chunk.Slice(8, Ds64Bytes).Clear();
        if (rf64)
        {
            BinaryPrimitives.WriteInt64LittleEndian(chunk[8..], riff);
            BinaryPrimitives.WriteInt64LittleEndian(chunk[16..], _dataBytes);
            BinaryPrimitives.WriteInt64LittleEndian(chunk[24..], _dataBytes / block);
        }

        Span<byte> fmt = header[48..];
        "fmt "u8.CopyTo(fmt);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[4..], 40);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[8..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[10..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[12..], (uint)_format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[16..], (uint)(_format.SampleRate * block));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[20..], (ushort)block);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[22..], (ushort)bits);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[24..], 22);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt[26..], (ushort)bits);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt[28..], ChannelMask(channels));
        (_format.SampleFormat == ExportSampleFormat.PcmFloat ? FloatFormat : PcmFormat).TryWriteBytes(fmt[32..]);

        Span<byte> data = header[96..];
        "data"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], rf64 ? uint.MaxValue : (uint)_dataBytes);
    }

    /// <summary>Altavoces en el orden de WAVE: mono al centro, estéreo L R, 5.1 L R C LFE Ls Rs.</summary>
    private static uint ChannelMask(int channels) => channels switch
    {
        1 => 0x4,
        2 => 0x3,
        6 => 0x3F,
        _ => (1u << channels) - 1,
    };
}
