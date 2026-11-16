using System.Runtime.InteropServices;
using Rodaje.Core.Raw;

namespace Rodaje.Core.Tests.Raw;

public sealed class GbrPlanesTests
{
    [Fact]
    public void Planes_go_green_blue_red_without_alpha_and_keep_values_above_one()
    {
        Half[] rgba = [(Half)4.0f, (Half)0.5f, (Half)0.25f, (Half)1f, (Half)65504f, (Half)2f, (Half)0f, (Half)1f];
        var gbr = new byte[2 * 6];
        GbrPlanes.FromRgbaHalf(MemoryMarshal.AsBytes(rgba.AsSpan()), gbr);
        Half[] planes = MemoryMarshal.Cast<byte, Half>(gbr).ToArray();
        Assert.Equal([(Half)0.5f, (Half)2f, (Half)0.25f, (Half)0f, (Half)4.0f, (Half)65504f], planes);
    }

    [Theory]
    [InlineData(15, 6)]
    [InlineData(16, 16)]
    [InlineData(16, 10)]
    public void Sizes_that_do_not_match_throw(int input, int output) =>
        Assert.Throws<ArgumentException>(() => GbrPlanes.FromRgbaHalf(new byte[input], new byte[output]));
}
