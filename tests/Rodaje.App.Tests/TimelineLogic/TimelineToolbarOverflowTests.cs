using Rodaje.App.Views.Timeline.Logic;

namespace Rodaje.App.Tests.TimelineLogic;

public sealed class TimelineToolbarOverflowTests
{
    [Theory]
    [InlineData(1366)]
    [InlineData(1280)]
    [InlineData(1024)]
    public void Calculate_EnModoPortatil_PliegaMarcadoresYDesplegableVisible(double width)
    {
        ToolbarLayoutState state = TimelineToolbarOverflow.Calculate(width);

        Assert.False(state.ShowDirectMarkers);
        Assert.True(state.ShowMoreButton);
        Assert.Equal(TimelineToolbarOverflow.CompactGapBoxWidth, state.GapBoxWidth);
        Assert.Equal(TimelineToolbarOverflow.CompactRippleBoxWidth, state.RippleBoxWidth);
        Assert.False(state.ShowDirectSecondaryToggles);
        Assert.True(state.ShowFlyoutSecondaryToggles);
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(1440)]
    [InlineData(2560)]
    public void Calculate_EnPantallaGrande_MuestraMarcadoresDirectosYSinDesplegable(double width)
    {
        ToolbarLayoutState state = TimelineToolbarOverflow.Calculate(width);

        Assert.True(state.ShowDirectMarkers);
        Assert.False(state.ShowMoreButton);
        Assert.Equal(TimelineToolbarOverflow.DefaultGapBoxWidth, state.GapBoxWidth);
        Assert.Equal(TimelineToolbarOverflow.DefaultRippleBoxWidth, state.RippleBoxWidth);
        Assert.True(state.ShowDirectSecondaryToggles);
        Assert.False(state.ShowFlyoutSecondaryToggles);
    }

    [Fact]
    public void Calculate_EnAnchoIntermedio_PliegaMarcadoresPeroMantieneTogglesDirectos()
    {
        // Entre 1370 y 1440 px: pliega marcadores para dar espacio pero conserva los 3 toggles directos
        ToolbarLayoutState state = TimelineToolbarOverflow.Calculate(1400);

        Assert.False(state.ShowDirectMarkers);
        Assert.True(state.ShowMoreButton);
        Assert.True(state.ShowDirectSecondaryToggles);
        Assert.False(state.ShowFlyoutSecondaryToggles);
        Assert.Equal(TimelineToolbarOverflow.CompactGapBoxWidth, state.GapBoxWidth);
        Assert.Equal(TimelineToolbarOverflow.CompactRippleBoxWidth, state.RippleBoxWidth);
    }
}
