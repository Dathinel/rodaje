using System.Numerics;
using Rodaje.Module.AudioAi.DeepFilter;

namespace Rodaje.Module.AudioAi.Tests;

public class DfStateTests
{
    [Theory]
    [InlineData(960)]
    [InlineData(64)]
    [InlineData(15)]
    [InlineData(7)]
    public void Fft_matches_the_direct_dft(int size)
    {
        var random = new Random(size);
        Complex[] x = [.. Enumerable.Range(0, size).Select(_ => new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5))];
        var fast = new Complex[size];
        new MixedRadixFft(size).Forward(x, fast);
        for (int k = 0; k < size; k++)
        {
            Complex direct = Complex.Zero;
            for (int n = 0; n < size; n++)
            {
                direct += x[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * n / size);
            }

            Assert.True((fast[k] - direct).Magnitude < 1e-9, $"bin {k}: {fast[k]} frente a {direct}");
        }
    }

    [Fact]
    public void Erb_bands_cover_the_spectrum_with_at_least_two_bins()
    {
        Assert.Equal(DfState.ErbBands, DfState.ErbWidths.Length);
        Assert.Equal(DfState.Bins, DfState.ErbWidths.Sum());
        Assert.All(DfState.ErbWidths, w => Assert.True(w >= 2));
        Assert.True(DfState.ErbWidths[^1] > DfState.ErbWidths[0], "Las bandas se ensanchan hacia los agudos.");
    }

    [Fact]
    public void Analysis_then_synthesis_returns_the_input_one_hop_later()
    {
        var random = new Random(3);
        float[] input = [.. Enumerable.Range(0, DfState.Hop * 40).Select(_ => (float)(random.NextDouble() - 0.5))];
        var output = new float[input.Length];
        var state = new DfState();
        var spectrum = new float[DfState.Bins * 2];
        var erb = new float[DfState.ErbBands];
        var df = new float[DfState.DfBins * 2];
        for (int f = 0; f < input.Length / DfState.Hop; f++)
        {
            state.Analyze(input.AsSpan(f * DfState.Hop, DfState.Hop), spectrum, erb, df);
            state.Synthesize(spectrum, output.AsSpan(f * DfState.Hop, DfState.Hop));
        }

        double worst = 0;
        for (int i = DfState.Hop; i < input.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(output[i] - input[i - DfState.Hop]));
        }

        Assert.True(worst < 1e-5, $"Error máximo {worst}");
    }

    [Fact]
    public void Features_follow_the_level_normalization_of_libdf()
    {
        // Mismo ruido a dos niveles (×4): los rasgos ERB, con la media móvil restada, terminan iguales; los complejos se
        // dividen por la raíz de la magnitud media, así que quedan ×2.
        (float[] erbQuiet, float[] dfQuiet) = FeaturesAfter(0.05f, 600);
        (float[] erbLoud, float[] dfLoud) = FeaturesAfter(0.2f, 600);
        for (int b = 0; b < DfState.ErbBands; b++)
        {
            Assert.InRange(erbLoud[b] - erbQuiet[b], -0.01, 0.01);
            Assert.InRange(erbQuiet[b], -0.5, 0.5);
        }

        for (int k = 0; k < DfState.DfBins * 2; k++)
        {
            if (Math.Abs(dfQuiet[k]) > 1e-3)
            {
                Assert.InRange(dfLoud[k] / dfQuiet[k], 1.98, 2.02);
            }
        }
    }

    private static (float[] Erb, float[] Df) FeaturesAfter(float amplitude, int frames)
    {
        var state = new DfState();
        var spectrum = new float[DfState.Bins * 2];
        var erb = new float[DfState.ErbBands];
        var df = new float[DfState.DfBins * 2];
        var random = new Random(5);
        float[] hop = new float[DfState.Hop];
        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < hop.Length; i++)
            {
                hop[i] = (float)(amplitude * (random.NextDouble() - 0.5));
            }

            state.Analyze(hop, spectrum, erb, df);
        }

        return (erb, df);
    }

    [Fact]
    public void Dual_mono_is_detected()
    {
        float[] same = [0.1f, 0.1f, -0.5f, -0.5f, 0.25f, 0.25f];
        float[] different = [0.1f, 0.1f, -0.5f, 0.5f, 0.25f, 0.25f];
        Assert.True(AudioAiFunctions.IsDualMono(same, 3));
        Assert.False(AudioAiFunctions.IsDualMono(different, 3));
    }
}
