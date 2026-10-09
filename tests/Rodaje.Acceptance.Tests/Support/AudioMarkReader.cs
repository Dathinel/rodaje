using Rodaje.Media.Audio;
using Rodaje.TestMedia;

namespace Rodaje.Acceptance.Tests.Support;

/// <summary>Marca del segundo <see cref="Second"/>: muestra (a 48 kHz, desde el origen) donde empieza, o null si no se encontró.</summary>
internal readonly record struct SecondMark(int Second, long? Sample, double Correlation);

/// <summary>
/// Lee el <see cref="AudioDecoder"/> en orden, como lo haría la salida de audio, y encuentra la marca de
/// cada segundo con <see cref="AudioCode.Detect"/> en una ventana de [s − 0,25 s, s + 0,75 s): ahí solo
/// cabe la marca del segundo s, entera con sus bits.
/// </summary>
internal sealed class AudioMarkReader
{
    private const int Rate = AudioDecoder.OutputSampleRate;
    private const int Channels = AudioDecoder.OutputChannels;
    private const int Before = Rate / 4;
    private const int After = Rate * 3 / 4;
    private const int MinimumAfter = Rate * 6 / 10;

    private readonly AudioDecoder _decoder;
    private readonly float[] _buffer = new float[(Before + After + Rate) * Channels];
    private long _bufferStart;
    private int _bufferFrames;
    private bool _ended;
    private int _nextSecond;

    public AudioMarkReader(AudioDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoder = decoder;
        _decoder.Seek(Rodaje.Core.Timing.Time.Zero);
    }

    /// <summary>La marca del siguiente segundo; false cuando el audio ya no alcanza para una marca completa.</summary>
    public bool TryNext(out SecondMark mark)
    {
        int second = _nextSecond;
        long center = (long)second * Rate;
        long windowStart = Math.Max(0, center - Before);
        long windowEnd = center + After;
        Discard(windowStart);
        Fill(windowEnd);
        long available = _bufferStart + _bufferFrames;
        if (available < center + MinimumAfter)
        {
            mark = default;
            return false;
        }

        _nextSecond++;
        int offset = (int)(windowStart - _bufferStart);
        int length = (int)(Math.Min(windowEnd, available) - windowStart);
        IReadOnlyList<AudioMark> found = AudioCode.Detect(_buffer.AsSpan(offset * Channels, length * Channels), Rate, Channels);
        int expected = second % AudioCode.SecondModulus;
        AudioMark? match = found.Where(m => m.Second == expected).Cast<AudioMark?>().FirstOrDefault();
        mark = match is { } m
            ? new SecondMark(second, windowStart + m.Sample, m.Correlation)
            : new SecondMark(second, null, found.Count > 0 ? found.Max(x => x.Correlation) : 0);
        return true;
    }

    private void Discard(long until)
    {
        int drop = (int)Math.Clamp(until - _bufferStart, 0, _bufferFrames);
        if (drop == 0)
        {
            return;
        }

        _buffer.AsSpan(drop * Channels, (_bufferFrames - drop) * Channels).CopyTo(_buffer);
        _bufferFrames -= drop;
        _bufferStart += drop;
    }

    private void Fill(long until)
    {
        while (!_ended && _bufferStart + _bufferFrames < until)
        {
            int want = (int)Math.Min(until - (_bufferStart + _bufferFrames), (_buffer.Length / Channels) - _bufferFrames);
            int read = _decoder.Read(_buffer.AsSpan(_bufferFrames * Channels, want * Channels));
            _bufferFrames += read;
            _ended = read < want;
        }
    }
}
