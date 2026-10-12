using Rodaje.Module.Voice.Synthesis;

namespace Rodaje.Module.Voice.Tests;

public sealed class AudioOpsTests
{
    private static float[] Sine(double hz, double seconds, int rate = 24_000) =>
        [.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate)))];

    // Frecuencia por cruces por cero (señal limpia).
    private static double Frequency(float[] s, int rate)
    {
        int crossings = 0;
        for (int i = 1; i < s.Length; i++)
            if (s[i - 1] < 0 && s[i] >= 0) crossings++;
        return crossings * (double)rate / s.Length;
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(-3.0)]
    public void Resampling_shifts_pitch_by_the_semitones_and_shortens_by_the_same_factor(double semitones)
    {
        double factor = Math.Pow(2, semitones / 12);
        float[] input = Sine(220, 1);
        float[] output = AudioOps.Resample(input, factor);
        Assert.Equal(input.Length / factor, output.Length, 1.0);
        Assert.Equal(220 * factor, Frequency(output, 24_000), 220 * factor * 0.01);
        Assert.True(output.Max() is > 0.45f and < 0.55f);
    }

    [Fact]
    public void Trimming_keeps_a_small_margin()
    {
        float[] audio = [.. new float[24_000], .. Sine(300, 0.5), .. new float[24_000]];
        ReadOnlySpan<float> trimmed = AudioOps.TrimSilence(audio, 24_000, marginSeconds: 0.03);
        Assert.InRange(trimmed.Length, 12_000, 12_000 + (2 * 720) + 2);
        Assert.Equal(0, AudioOps.TrimSilence(new float[1000], 24_000).Length);
    }
}
