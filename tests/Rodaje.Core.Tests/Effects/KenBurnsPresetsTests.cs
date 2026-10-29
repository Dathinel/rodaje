using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Xunit;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Effects;

public sealed class KenBurnsPresetsTests
{
    [Theory]
    [InlineData(MotionPresets.KenBurnsZoomIn)]
    [InlineData(MotionPresets.KenBurnsZoomOut)]
    [InlineData(MotionPresets.KenBurnsPanLeft)]
    [InlineData(MotionPresets.KenBurnsPanRight)]
    public void Presets_ExistAndHaveNames(string presetId)
    {
        MotionPreset? preset = MotionPresets.Find(presetId);
        Assert.NotNull(preset);
        Assert.False(string.IsNullOrWhiteSpace(preset.Name));
        Assert.False(string.IsNullOrWhiteSpace(preset.Description));
    }

    [Fact]
    public void ZoomIn_CreatesScaleAndPositionKeyframes()
    {
        var from = new MotionBase(0, 0, 1.0, 0);
        var keys = MotionPresets.Build(MotionPresets.KenBurnsZoomIn, 1920, 1080, 5.0, from);

        Assert.True(keys.ContainsKey(T.Scale));
        Assert.True(keys.ContainsKey(T.X));
        Assert.True(keys.ContainsKey(T.Y));

        var scaleKeys = keys[T.Scale];
        Assert.Equal(2, scaleKeys.Count);
        Assert.Equal(0.0, scaleKeys[0].Seconds);
        Assert.Equal(1.0, scaleKeys[0].Value);
        Assert.Equal(5.0, scaleKeys[1].Seconds);
        Assert.Equal(1.2, scaleKeys[1].Value, precision: 2);
    }

    [Fact]
    public void ZoomOut_CreatesScaleAndPositionKeyframes()
    {
        var from = new MotionBase(0, 0, 1.0, 0);
        var keys = MotionPresets.Build(MotionPresets.KenBurnsZoomOut, 1920, 1080, 5.0, from);

        Assert.True(keys.ContainsKey(T.Scale));
        Assert.True(keys.ContainsKey(T.X));
        Assert.True(keys.ContainsKey(T.Y));

        var scaleKeys = keys[T.Scale];
        Assert.Equal(2, scaleKeys.Count);
        Assert.Equal(0.0, scaleKeys[0].Seconds);
        Assert.Equal(1.2, scaleKeys[0].Value, precision: 2);
        Assert.Equal(5.0, scaleKeys[1].Seconds);
        Assert.Equal(1.0, scaleKeys[1].Value, precision: 2);
    }

    [Fact]
    public void PanLeft_MovesXToTheLeftWithSlightZoom()
    {
        var from = new MotionBase(0, 0, 1.0, 0);
        var keys = MotionPresets.Build(MotionPresets.KenBurnsPanLeft, 1000, 500, 4.0, from);

        var xKeys = keys[T.X];
        Assert.Equal(2, xKeys.Count);
        Assert.Equal(0.0, xKeys[0].Seconds);
        Assert.True(xKeys[0].Value > xKeys[1].Value); // Starts right, moves left
        Assert.Equal(4.0, xKeys[1].Seconds);

        var scaleKeys = keys[T.Scale];
        Assert.True(scaleKeys[0].Value > 1.0); // Scaled so edges don't show black bars
    }

    [Fact]
    public void PanRight_MovesXToTheRightWithSlightZoom()
    {
        var from = new MotionBase(0, 0, 1.0, 0);
        var keys = MotionPresets.Build(MotionPresets.KenBurnsPanRight, 1000, 500, 4.0, from);

        var xKeys = keys[T.X];
        Assert.Equal(2, xKeys.Count);
        Assert.Equal(0.0, xKeys[0].Seconds);
        Assert.True(xKeys[0].Value < xKeys[1].Value); // Starts left, moves right
        Assert.Equal(4.0, xKeys[1].Seconds);

        var scaleKeys = keys[T.Scale];
        Assert.True(scaleKeys[0].Value > 1.0);
    }
}
