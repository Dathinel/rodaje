using Rodaje.Core.Effects;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public class MotionPresetsCazaTests
{
    [Fact]
    public void All_ContainsAllDocumentedFactoryAnimations()
    {
        var presetIds = MotionPresets.All.Select(p => p.Id).ToArray();

        Assert.Contains(MotionPresets.KenBurns, presetIds);
        Assert.Contains(MotionPresets.KenBurnsZoomIn, presetIds);
        Assert.Contains(MotionPresets.KenBurnsZoomOut, presetIds);
        Assert.Contains(MotionPresets.KenBurnsPanLeft, presetIds);
        Assert.Contains(MotionPresets.KenBurnsPanRight, presetIds);
        Assert.Contains(MotionPresets.SlowZoom, presetIds);
        Assert.Contains(MotionPresets.SlideIn, presetIds);
        Assert.Contains(MotionPresets.SlideOut, presetIds);
        Assert.Contains(MotionPresets.Shake, presetIds);
        Assert.Contains(MotionPresets.Heartbeat, presetIds);
        Assert.Contains(MotionPresets.Spin, presetIds);
        Assert.Contains(MotionPresets.PunchZoom, presetIds);
    }

    [Fact]
    public void Find_ReturnsEachDocumentedFactoryAnimation()
    {
        MotionPreset? kenBurns = MotionPresets.Find(MotionPresets.KenBurns);
        Assert.NotNull(kenBurns);
        Assert.Equal("Ken Burns", kenBurns.Name);

        MotionPreset? slowZoom = MotionPresets.Find(MotionPresets.SlowZoom);
        Assert.NotNull(slowZoom);
        Assert.Equal("Zoom lento", slowZoom.Name);

        MotionPreset? slideIn = MotionPresets.Find(MotionPresets.SlideIn);
        Assert.NotNull(slideIn);
        Assert.Equal("Deslizar entrada", slideIn.Name);

        MotionPreset? slideOut = MotionPresets.Find(MotionPresets.SlideOut);
        Assert.NotNull(slideOut);
        Assert.Equal("Deslizar salida", slideOut.Name);

        MotionPreset? shake = MotionPresets.Find(MotionPresets.Shake);
        Assert.NotNull(shake);
        Assert.Contains("Sacudida", shake.Name);

        MotionPreset? heartbeat = MotionPresets.Find(MotionPresets.Heartbeat);
        Assert.NotNull(heartbeat);
        Assert.Equal("Latido", heartbeat.Name);

        MotionPreset? spin = MotionPresets.Find(MotionPresets.Spin);
        Assert.NotNull(spin);
        Assert.Equal("Giro", spin.Name);

        MotionPreset? punchZoom = MotionPresets.Find(MotionPresets.PunchZoom);
        Assert.NotNull(punchZoom);
        Assert.Equal("Zoom de impacto", punchZoom.Name);
    }

    [Fact]
    public void Build_KenBurns_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.KenBurns, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_SlowZoom_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.SlowZoom, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_SlideIn_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.SlideIn, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_SlideOut_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.SlideOut, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_Shake_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.Shake, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_Heartbeat_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.Heartbeat, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_Spin_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.Spin, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_PunchZoom_ReturnsKeyframesInSeconds()
    {
        var result = MotionPresets.Build(MotionPresets.PunchZoom, 1920, 1080, 5.0, MotionBase.Default);
        Assert.NotEmpty(result);
        foreach (var (_, keys) in result)
        {
            Assert.NotEmpty(keys);
            Assert.All(keys, key => Assert.InRange(key.Seconds, 0.0, 5.0));
        }
    }

    [Fact]
    public void Build_AllFactoryAnimations_KeyframeTimingsAreInSecondsAndChronological()
    {
        const double duration = 4.0;
        foreach (MotionPreset preset in MotionPresets.All)
        {
            var result = MotionPresets.Build(preset.Id, 1920, 1080, duration, MotionBase.Default);
            Assert.NotEmpty(result);

            foreach (var (_, keys) in result)
            {
                Assert.NotEmpty(keys);
                double previousTime = 0.0;
                foreach (MotionKey key in keys)
                {
                    Assert.True(key.Seconds >= 0.0);
                    Assert.True(key.Seconds <= duration);
                    Assert.True(key.Seconds >= previousTime);
                    previousTime = key.Seconds;
                }
            }
        }
    }
}
