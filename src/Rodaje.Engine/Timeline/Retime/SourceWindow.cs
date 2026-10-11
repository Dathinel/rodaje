using System.Numerics;
using Rodaje.Core.Timing;

namespace Rodaje.Engine.Timeline.Retime;

/// <summary>
/// Lo decodificado alrededor de lo que se está leyendo de un medio, en planos L y R, para leerlo a saltos y en
/// cualquier sentido. Hacia adelante decodifica seguido mientras el salto sea corto y busca si es largo; hacia atrás
/// carga bloques de <see cref="BackBlock"/> muestras. Antes del principio y después del final del medio hay silencio.
/// </summary>
internal sealed class SourceWindow
{
    /// <summary>Un salto hacia adelante más largo que esto se hace con un seek en lugar de decodificar lo saltado.</summary>
    public const int SeekGap = TimelinePlan.AudioSampleRate / 4;

    /// <summary>Medio segundo: lo que se carga de una vez al recorrer el medio hacia atrás.</summary>
    public const int BackBlock = TimelinePlan.AudioSampleRate / 2;

    // Lo que se conserva por detrás de lo pedido: las búsquedas de WSOLA vuelven unos cientos de muestras.
    private const int KeepBehind = 8192;

    private readonly IPcmSource _pcm;
    private float[] _left = new float[1 << 16];
    private float[] _right = new float[1 << 16];
    private float[] _interleaved = new float[4096 * 2];
    private float[] _fractionalL = [];
    private float[] _fractionalR = [];
    private long _start;
    private int _count;
    private long _end = long.MaxValue;

    public SourceWindow(IPcmSource pcm, long decoderNext)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        _pcm = pcm;
        DecoderNext = decoderNext;
    }

    /// <summary>Muestra que devolverá la próxima lectura del decodificador; -1 si no se sabe.</summary>
    public long DecoderNext { get; set; }

    public long Seeks { get; private set; }

    /// <summary>
    /// Copia <c>left.Length</c> muestras desde <paramref name="u"/> en el sentido de reproducción: hacia adelante, del
    /// origen <c>u, u + 1, …</c>; en reversa, <c>−u − 1, −u − 2, …</c>.
    /// </summary>
    public void Read(long u, Span<float> left, Span<float> right, bool reverse)
    {
        int count = left.Length;
        long first = reverse ? -u - count : u;
        Ensure(first, first + count, reverse);
        left.Clear();
        right.Clear();
        long from = Math.Max(first, Math.Max(_start, 0));
        long to = Math.Min(first + count, Math.Min(_start + _count, _end));
        if (to <= from)
        {
            return;
        }

        int length = (int)(to - from);
        int at = (int)(from - _start);
        if (!reverse)
        {
            int offset = (int)(from - first);
            _left.AsSpan(at, length).CopyTo(left[offset..]);
            _right.AsSpan(at, length).CopyTo(right[offset..]);
            return;
        }

        // En reversa la salida i es el origen −u − 1 − i: el último del tramo va primero.
        int last = (int)(-u - to);
        for (int i = 0; i < length; i++)
        {
            left[last + i] = _left[at + length - 1 - i];
            right[last + i] = _right[at + length - 1 - i];
        }
    }

    /// <summary>
    /// Como <see cref="Read"/> desde una posición con fracción: interpola con el sinc de
    /// <see cref="VarispeedResampler.Interpolator"/>, que en una posición entera copia tal cual.
    /// </summary>
    public void ReadAt(double u, Span<float> left, Span<float> right, bool reverse)
    {
        long whole = (long)Math.Floor(u);
        int phase = (int)Math.Round((u - whole) * VarispeedResampler.Phases);
        if (phase == VarispeedResampler.Phases)
        {
            phase = 0;
            whole++;
        }

        if (phase == 0)
        {
            Read(whole, left, right, reverse);
            return;
        }

        VarispeedResampler.Kernel kernel = VarispeedResampler.Interpolator;
        int taps = kernel.Taps;
        int count = left.Length;
        int length = count + taps;
        if (_fractionalL.Length < length)
        {
            _fractionalL = new float[length];
            _fractionalR = new float[length];
        }

        Span<float> l = _fractionalL.AsSpan(0, length);
        Span<float> r = _fractionalR.AsSpan(0, length);
        Read(whole - (taps / 2) + 1, l, r, reverse);
        ReadOnlySpan<float> row = kernel.Row(phase);
        VectorMath.Convolve(row, l, left);
        VectorMath.Convolve(row, r, right);
    }

    private void Ensure(long from, long to, bool backward)
    {
        from = Math.Max(0, from);
        to = Math.Min(to, _end);
        if (to <= from || (from >= _start && to <= _start + _count))
        {
            return;
        }

        long bufferEnd = _start + _count;
        if (_count > 0 && from >= _start && from <= bufferEnd + SeekGap)
        {
            Fill(to, from - KeepBehind);
            return;
        }

        long start = backward ? Math.Max(0, Math.Min(from, to - BackBlock)) : from;
        _start = start;
        _count = 0;
        Fill(to, start);
    }

    private void Drop(long keepFrom)
    {
        int drop = (int)(Math.Min(keepFrom, _start + _count) - _start);
        if (drop <= 0)
        {
            return;
        }

        _count -= drop;
        Array.Copy(_left, drop, _left, 0, _count);
        Array.Copy(_right, drop, _right, 0, _count);
        _start = keepFrom;
    }

    /// <summary>Decodifica hasta <paramref name="to"/>; si no cabe, descarta lo anterior a <paramref name="keepFrom"/> (solo entonces mueve memoria).</summary>
    private void Fill(long to, long keepFrom)
    {
        if (to - _start > _left.Length)
        {
            Drop(Math.Max(_start, keepFrom));
        }

        long need = to - _start;
        if (need > _left.Length)
        {
            int size = (int)Math.Min(int.MaxValue / 2, (long)BitOperations.RoundUpToPowerOf2((ulong)need));
            Array.Resize(ref _left, size);
            Array.Resize(ref _right, size);
        }

        long position = _start + _count;
        if (DecoderNext != position)
        {
            _pcm.Seek(Time.FromSamples(position, TimelinePlan.AudioSampleRate));
            DecoderNext = position;
            Seeks++;
        }

        while (_start + _count < to)
        {
            int frames = (int)Math.Min(_interleaved.Length / 2, to - (_start + _count));
            int read = _pcm.Read(_interleaved.AsSpan(0, frames * 2));
            if (read <= 0)
            {
                _end = _start + _count;
                return;
            }

            for (int i = 0; i < read; i++)
            {
                _left[_count + i] = _interleaved[i * 2];
                _right[_count + i] = _interleaved[(i * 2) + 1];
            }

            _count += read;
            DecoderNext += read;
        }
    }
}
