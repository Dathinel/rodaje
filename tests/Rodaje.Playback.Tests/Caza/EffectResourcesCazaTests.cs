using System;
using Rodaje.Playback.Composition;
using Xunit;

namespace Rodaje.Playback.Tests.Caza;

public class EffectResourcesCazaTests
{
    [Fact]
    public void LutData_Preserves3dLutSizeAndRgbData()
    {
        int size = 2;
        float[] rgb = new float[size * size * size * 3];
        rgb[0] = 0.5f;
        var lut = new LutData(size, rgb);

        Assert.Equal(size, lut.Size);
        Assert.Equal(rgb.Length, lut.Rgb.Length);
        Assert.Equal(0.5f, lut.Rgb.Span[0]);
    }

    [Fact]
    public void TryGetLut_WhenAvailable_DeliversLutDataWithoutBlocking()
    {
        var resources = new TestEffectResources();
        int size = 2;
        float[] rgb = new float[size * size * size * 3];
        var expectedLut = new LutData(size, rgb);
        resources.SetLut(expectedLut);

        bool found = resources.TryGetLut(default, out LutData? actualLut);

        Assert.True(found);
        Assert.Same(expectedLut, actualLut);
    }

    [Fact]
    public void TryGetLut_WhenMissing_ReturnsFalseWithoutBlocking()
    {
        var resources = new TestEffectResources();

        bool found = resources.TryGetLut(default, out LutData? actualLut);

        Assert.False(found);
        Assert.Null(actualLut);
    }

    [Fact]
    public void Changed_WhenMissingResourceBecomesAvailable_RaisesEvent()
    {
        var resources = new TestEffectResources();
        bool notified = false;
        resources.Changed += (_, _) => notified = true;

        int size = 2;
        float[] rgb = new float[size * size * size * 3];
        resources.SetLut(new LutData(size, rgb));

        Assert.True(notified);
    }

    private sealed class TestEffectResources : IEffectResources
    {
        private LutData? _lut;

        public event EventHandler? Changed;

        public void SetLut(LutData lut)
        {
            _lut = lut;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            _ = key;
            lut = _lut;
            return _lut is not null;
        }
    }
}
