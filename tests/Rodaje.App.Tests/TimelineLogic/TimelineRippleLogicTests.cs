using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.Core.Editing;

namespace Rodaje.App.Tests.TimelineLogic;

public class TimelineRippleLogicTests
{
    [Theory]
    [InlineData(TimelineGapBehavior.LeaveGaps, false, false)]
    [InlineData(TimelineGapBehavior.MagneticMainTrack, true, false)]
    [InlineData(TimelineGapBehavior.GlobalRipple, false, true)]
    public void ElSelectorUnico_ActivaUnSoloModo(TimelineGapBehavior behavior, bool magnetic, bool ripple)
    {
        var state = new TimelineState();

        state.GapBehavior = behavior;

        Assert.Equal(behavior, state.GapBehavior);
        Assert.Equal(magnetic, state.MagneticMainTrack);
        Assert.Equal(ripple, state.AutoRipple);
    }

    [Fact]
    public void ElModoDeHuecos_NoCambiaElAmbitoDeRipple()
    {
        var state = new TimelineState { RippleMode = RippleMode.AllTracksAndMarkers };

        state.GapBehavior = TimelineGapBehavior.GlobalRipple;

        Assert.Equal(RippleScope.AllTracksAndMarkers, state.RippleScope);
    }

    [Fact]
    public void PreferenciasAntiguasConAmbosInterruptores_SeNormalizanAlRippleGlobal()
    {
        var state = new TimelineState();
        var preferences = new TimelinePreferences { MagneticMainTrack = true, AutoRipple = true };

        preferences.ApplyTo(state);

        Assert.Equal(TimelineGapBehavior.GlobalRipple, state.GapBehavior);
        Assert.False(state.MagneticMainTrack);
    }

    [Fact]
    public void AtajosConservanCtrlL_YElRecorteRippleTieneNombreDistinto()
    {
        ShortcutAction gapMode = Assert.Single(EditorShortcuts.Catalog, action => action.Id == ActionIds.ToggleAutoRipple);
        ShortcutAction rippleTrim = Assert.Single(EditorShortcuts.Catalog, action => action.Id == ActionIds.RippleTool);

        Assert.Equal(KeyChord.Parse("Ctrl+L"), Assert.Single(gapMode.DefaultKeys(ShortcutPreset.Rodaje)));
        Assert.Equal("Cambiar control de huecos", gapMode.Label);
        Assert.Equal("Recorte Ripple", rippleTrim.Label);
        Assert.NotEqual(gapMode.ShortLabel, rippleTrim.ShortLabel);
    }
}
