using Rodaje.Audio.Metering;
using Rodaje.Audio.Processing;

namespace Rodaje.Audio.Tests.Metering;

public sealed class DynamicsMeterRingContractTests
{
    [Fact]
    public void Measures_only_blocks_intersecting_the_requested_half_open_interval()
    {
        var ring = new DynamicsMeterRing();
        ring.Write(0, 10, new DynamicsReading(0.2f, 0.1f, 2, 1));
        ring.Write(10, 10, new DynamicsReading(0.8f, 0.6f, 7, 3));

        Assert.Equal(new DynamicsReading(0.2f, 0.1f, 2, 1), ring.Measure(0, 10));
        Assert.Equal(new DynamicsReading(0.8f, 0.6f, 7, 3), ring.Measure(10, 20));
        Assert.Null(ring.Measure(20, 30));
    }

    [Fact]
    public void Combines_maxima_from_overlapping_blocks_and_rejects_empty_ranges()
    {
        var ring = new DynamicsMeterRing();
        ring.Write(0, 20, new DynamicsReading(0.3f, 0.4f, 2, 0.5f));
        ring.Write(10, 10, new DynamicsReading(0.7f, 0.6f, 5, 1));

        Assert.Equal(new DynamicsReading(0.7f, 0.6f, 5, 1), ring.Measure(5, 15));
        Assert.Null(ring.Measure(10, 10));
        Assert.Null(ring.Measure(11, 10));
    }

    [Fact]
    public void Overwritten_entries_do_not_contribute_to_old_time_ranges()
    {
        var ring = new DynamicsMeterRing(capacity: 16);
        for (int i = 0; i < 20; i++)
        {
            ring.Write(i * 10, 10, new DynamicsReading(i, i, i, i));
        }

        Assert.Null(ring.Measure(0, 10));
        Assert.Equal(new DynamicsReading(19, 19, 19, 19), ring.Measure(190, 200));
    }
}
