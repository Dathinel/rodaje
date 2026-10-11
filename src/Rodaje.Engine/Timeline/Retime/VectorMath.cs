using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rodaje.Engine.Timeline.Retime;

internal static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        ref float pa = ref MemoryMarshal.GetReference(a);
        ref float pb = ref MemoryMarshal.GetReference(b);
        int i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            Vector<float> acc = Vector<float>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                acc += Vector.LoadUnsafe(ref pa, (nuint)i) * Vector.LoadUnsafe(ref pb, (nuint)i);
            }

            sum = Vector.Sum(acc);
        }

        for (; i < n; i++)
        {
            sum += Unsafe.Add(ref pa, i) * Unsafe.Add(ref pb, i);
        }

        return sum;
    }

    /// <summary>Los dos productos de <paramref name="kernel"/> con <paramref name="left"/> y <paramref name="right"/> en una pasada.</summary>
    public static (float Left, float Right) Dot2(ReadOnlySpan<float> kernel, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        int n = kernel.Length;
        ref float pk = ref MemoryMarshal.GetReference(kernel);
        ref float pl = ref MemoryMarshal.GetReference(left);
        ref float pr = ref MemoryMarshal.GetReference(right);
        int i = 0;
        float sl = 0;
        float sr = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            Vector<float> al = Vector<float>.Zero;
            Vector<float> ar = Vector<float>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                Vector<float> k = Vector.LoadUnsafe(ref pk, (nuint)i);
                al += k * Vector.LoadUnsafe(ref pl, (nuint)i);
                ar += k * Vector.LoadUnsafe(ref pr, (nuint)i);
            }

            sl = Vector.Sum(al);
            sr = Vector.Sum(ar);
        }

        for (; i < n; i++)
        {
            float k = Unsafe.Add(ref pk, i);
            sl += k * Unsafe.Add(ref pl, i);
            sr += k * Unsafe.Add(ref pr, i);
        }

        return (sl, sr);
    }

    /// <summary>
    /// <c>output[i] = Σ kernel[j] · input[i + j]</c>, vectorizado a lo largo de la salida: sin sumas horizontales, que
    /// es lo que cuesta en un filtro corto. <paramref name="input"/> mide al menos <c>output.Length + kernel.Length − 1</c>.
    /// </summary>
    public static void Convolve(ReadOnlySpan<float> kernel, ReadOnlySpan<float> input, Span<float> output)
    {
        int n = output.Length;
        if (kernel.IsEmpty || input.Length < n + kernel.Length - 1)
        {
            throw new ArgumentException("La entrada no alcanza para la salida pedida.", nameof(input));
        }

        ref float pi = ref MemoryMarshal.GetReference(input);
        ref float po = ref MemoryMarshal.GetReference(output);
        int width = Vector<float>.Count;
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; i <= n - width; i += width)
            {
                Vector<float> acc = Vector<float>.Zero;
                for (int j = 0; j < kernel.Length; j++)
                {
                    acc += new Vector<float>(kernel[j]) * Vector.LoadUnsafe(ref pi, (nuint)(i + j));
                }

                acc.StoreUnsafe(ref po, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            float sum = 0;
            for (int j = 0; j < kernel.Length; j++)
            {
                sum += kernel[j] * Unsafe.Add(ref pi, i + j);
            }

            Unsafe.Add(ref po, i) = sum;
        }
    }
}
