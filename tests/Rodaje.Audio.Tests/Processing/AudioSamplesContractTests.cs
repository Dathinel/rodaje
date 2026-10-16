using Rodaje.Audio.Processing;

namespace Rodaje.Audio.Tests.Processing;

public sealed class AudioSamplesContractTests
{
    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 0)]
    [InlineData(float.NegativeInfinity, 0)]
    [InlineData(1200, AudioSamples.MaxMagnitude)]
    [InlineData(-1200, -AudioSamples.MaxMagnitude)]
    [InlineData(0.5, 0.5)]
    public void Sanitize_maps_invalid_and_out_of_range_samples_to_the_supported_domain(float input, float expected)
    {
        Assert.Equal(expected, AudioSamples.Sanitize(input));
    }

    [Fact]
    public void Sanitize_span_reports_only_changed_samples()
    {
        float[] samples = [0.25f, float.NaN, 1500, -AudioSamples.MaxMagnitude, float.NegativeInfinity];

        int changed = AudioSamples.Sanitize(samples);

        Assert.Equal(3, changed);
        Assert.Equal([0.25f, 0, AudioSamples.MaxMagnitude, -AudioSamples.MaxMagnitude, 0], samples);
    }
}
