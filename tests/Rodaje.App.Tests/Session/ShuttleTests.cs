using Rodaje.App.Services;

namespace Rodaje.App.Tests.Session;

public sealed class ShuttleTests
{
    [Fact]
    public void L_plays_then_doubles_up_to_eight()
    {
        int rate = 0;
        int[] seen = new int[5];
        for (int i = 0; i < seen.Length; i++)
        {
            rate = Shuttle.Next(rate, ShuttleKey.Forward);
            seen[i] = rate;
        }

        Assert.Equal([1, 2, 4, 8, 8], seen);
    }

    [Fact]
    public void J_reverses_from_any_forward_speed_and_doubles_backwards()
    {
        Assert.Equal(-1, Shuttle.Next(0, ShuttleKey.Reverse));
        Assert.Equal(-1, Shuttle.Next(4, ShuttleKey.Reverse));
        Assert.Equal(-2, Shuttle.Next(-1, ShuttleKey.Reverse));
        Assert.Equal(-8, Shuttle.Next(-8, ShuttleKey.Reverse));
        Assert.Equal(1, Shuttle.Next(-4, ShuttleKey.Forward));
    }

    [Fact]
    public void K_stops_and_labels_only_show_for_scrubbing()
    {
        Assert.Equal(0, Shuttle.Next(-4, ShuttleKey.Stop));
        Assert.Equal(0, Shuttle.Next(1, ShuttleKey.Stop));
        Assert.Equal("", Shuttle.Label(0));
        Assert.Equal("", Shuttle.Label(1));
        Assert.Equal("▶ ×2", Shuttle.Label(2));
        Assert.Equal("◀ ×1", Shuttle.Label(-1));
    }
}
