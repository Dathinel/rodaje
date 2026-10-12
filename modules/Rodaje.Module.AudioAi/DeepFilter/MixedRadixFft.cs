using System.Numerics;

namespace Rodaje.Module.AudioAi.DeepFilter;

/// <summary>
/// FFT compleja de largo cualquiera (Cooley-Tukey de base mixta con mariposa genérica): DeepFilterNet usa 960 puntos
/// (2⁶·3·5), que no es potencia de 2. Sin normalizar en ningún sentido, como realfft.
/// </summary>
internal sealed class MixedRadixFft
{
    private readonly Complex[] _twiddles;
    private readonly int[] _factors;
    private readonly Complex[] _scratch;

    public MixedRadixFft(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2);
        Size = size;
        _twiddles = new Complex[size];
        for (int k = 0; k < size; k++)
        {
            double phase = -2 * Math.PI * k / size;
            _twiddles[k] = new Complex(Math.Cos(phase), Math.Sin(phase));
        }

        var factors = new List<int>();
        int n = size;
        int p = 4;
        while (n > 1)
        {
            while (n % p != 0)
            {
                p = p switch { 4 => 2, 2 => 3, _ => p + 2 };
                if (p * p > n)
                {
                    p = n;
                }
            }

            n /= p;
            factors.Add(p);
            factors.Add(n);
        }

        _factors = [.. factors];
        _scratch = new Complex[factors.Where((_, i) => i % 2 == 0).Max()];
    }

    public int Size { get; }

    /// <summary>X[k] = Σ x[n]·e^(−2πikn/N).</summary>
    public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        if (input.Length != Size || output.Length != Size)
        {
            throw new ArgumentException($"La FFT es de {Size} puntos.");
        }

        Work(output, 0, input, 0, 1, 0);
    }

    private void Work(Span<Complex> output, int outAt, ReadOnlySpan<Complex> input, int inAt, int stride, int factorIndex)
    {
        int p = _factors[factorIndex];
        int m = _factors[factorIndex + 1];
        if (m == 1)
        {
            for (int i = 0; i < p; i++)
            {
                output[outAt + i] = input[inAt + (i * stride)];
            }
        }
        else
        {
            for (int i = 0; i < p; i++)
            {
                Work(output, outAt + (i * m), input, inAt + (i * stride), stride * p, factorIndex + 2);
            }
        }

        Butterfly(output.Slice(outAt, p * m), stride, m, p);
    }

    private void Butterfly(Span<Complex> data, int stride, int m, int p)
    {
        Span<Complex> scratch = _scratch.AsSpan(0, p);
        for (int u = 0; u < m; u++)
        {
            for (int q = 0, k = u; q < p; q++, k += m)
            {
                scratch[q] = data[k];
            }

            for (int q1 = 0, k = u; q1 < p; q1++, k += m)
            {
                int index = 0;
                Complex sum = scratch[0];
                for (int q = 1; q < p; q++)
                {
                    index += stride * k;
                    if (index >= Size)
                    {
                        index %= Size;
                    }

                    sum += scratch[q] * _twiddles[index];
                }

                data[k] = sum;
            }
        }
    }
}
