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

    [Fact]
    public void Rec2020_primaries_compose_the_matrix_and_leave_the_original()
    {
        var identity = new RawDevelopParameters
        {
            Pattern = BayerPattern.Rggb,
            White = 4095,
            Multipliers = [2f, 1f, 1.5f],
            Matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
        };
        RawDevelopParameters wide = identity.WithRec2020Primaries();
        Assert.Equal(RawDevelopParameters.Bt709ToBt2020, wide.Matrix);
        Assert.Equal([1f, 0, 0, 0, 1, 0, 0, 0, 1], identity.Matrix);

        // Una fila de la cámara que suma 1 sigue sumando 1: el blanco no cambia de primarios.
        RawDevelopParameters camera = identity with { Matrix = [1.6f, -0.4f, -0.2f, -0.1f, 1.3f, -0.2f, 0f, -0.3f, 1.3f] };
        IReadOnlyList<float> m = camera.WithRec2020Primaries().Matrix;
        for (int r = 0; r < 3; r++)
        {
            Assert.Equal(1.0, m[r * 3] + m[(r * 3) + 1] + m[(r * 3) + 2], 4);
        }
    }
}
