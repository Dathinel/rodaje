using Rodaje.Playback;
using Xunit;

namespace Rodaje.Playback.Tests.Caza;

public class PlaybackTypesCazaTests
{
    [Fact]
    public void PrefetchFrames_DefaultsToEight()
    {
        var options = new PlaybackOptions();

        Assert.Equal(8, options.PrefetchFrames);
    }
}
