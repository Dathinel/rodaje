using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Tests.Analysis;

public sealed class TimelineAnalysisMenuTests
{
    [Fact]
    public void ElMenuDeClipSoloIncluyeHerramientasDelMedioYRecuperaReducirRuido()
    {
        Assert.Equal(
            ["analysis.silences", "analysis.beats", "analysis.ducking", "analysis.noise"],
            QuickTools.ContextMenuActionIds(hasAudio: true, hasVideo: false));
        Assert.Equal(
            ["analysis.stabilize", "analysis.autocolor", "analysis.whitebalance", QuickTools.RemoveBackground, QuickTools.Reframe],
            QuickTools.ContextMenuActionIds(hasAudio: false, hasVideo: true));
        Assert.Equal(
            ["analysis.silences", "analysis.beats", "analysis.ducking", "analysis.noise", "analysis.stabilize", "analysis.autocolor", "analysis.whitebalance", QuickTools.RemoveBackground, QuickTools.Reframe],
            QuickTools.ContextMenuActionIds(hasAudio: true, hasVideo: true));
        Assert.Empty(QuickTools.ContextMenuActionIds(hasAudio: false, hasVideo: false));
    }
}
