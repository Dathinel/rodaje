namespace Rodaje.Analysis.Dsp;

/// <summary>FFT compleja radix-2 en el sitio, con tablas de giros y de inversión de bits precalculadas.</summary>
internal sealed class Fft
{
    private readonly int[] _reverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "El tamaño de la FFT debe ser una potencia de 2.");
        }

        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        _reverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _reverse[i] = r;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / size);
        }
    }

    public int Size { get; }

    public void Forward(Span<float> re, Span<float> im)
    {
        int n = Size;
        for (int i = 0; i < n; i++)
        {
            int j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step];
                    float wi = _sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float tr = (re[b] * wr) - (im[b] * wi);
                    float ti = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
