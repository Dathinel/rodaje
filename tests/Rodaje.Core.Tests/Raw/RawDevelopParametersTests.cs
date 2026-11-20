using Rodaje.Core.Raw;

namespace Rodaje.Core.Tests.Raw;

public sealed class RawDevelopParametersTests
{
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

        // Primero la cámara y luego BT.709 → BT.2020: con la cámara que cambia R por G, la fila R toma las columnas cambiadas.
        IReadOnlyList<float> b = RawDevelopParameters.Bt709ToBt2020;
        IReadOnlyList<float> swapped = (identity with { Matrix = [0, 1, 0, 1, 0, 0, 0, 0, 1] }).WithRec2020Primaries().Matrix;
        Assert.Equal([b[1], b[0], b[2], b[4], b[3], b[5], b[7], b[6], b[8]], swapped);
    }
}
