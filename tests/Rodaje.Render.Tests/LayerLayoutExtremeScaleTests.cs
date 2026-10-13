using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests;

/// <summary>
/// Tarea 91 («la app se cierra al elegir escala»): la geometría de la capa con escalas imposibles. Lo que no se puede
/// dibujar (0, negativo, NaN, infinito, ejes aplastados) da nulo en vez de lanzar o de pedir una textura de tamaño 0;
/// lo enorme da una colocación finita.
/// </summary>
public sealed class LayerLayoutExtremeScaleTests
{
    public static readonly TheoryData<double> Impossible = [0, -0.0, -1, -1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity];

    [Theory]
    [MemberData(nameof(Impossible))]
    public void Una_escala_imposible_no_se_dibuja(double scale)
    {
        var transform = new LayerTransform(0, 0, scale, 0, 1);
        Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, transform));
        Assert.Null(LayerLayout.PlaceNative(1920, 1080, 400, 100, transform));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Un_eje_aplastado_o_que_no_es_un_numero_no_se_dibuja(double axis)
    {
        foreach (LayerShape shape in new[] { LayerShape.None with { ScaleX = axis }, LayerShape.None with { ScaleY = axis } })
        {
            var transform = new LayerTransform(0, 0, 1, 0, 1) { Shape = shape };
            Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, transform));
        }
    }

    [Fact]
    public void Un_eje_negativo_es_un_volteo_y_se_dibuja()
    {
        var transform = new LayerTransform(0, 0, 1, 0, 1) { Shape = LayerShape.None with { ScaleX = -1 } };
        LayerPlacement p = LayerLayout.Place(1920, 1080, 1920, 1080, transform)!.Value;
        Assert.Equal(1f, p.MaxAxisScale, 3);
    }

    [Theory]
    [InlineData(1e-9)]
    [InlineData(1e-4)]
    [InlineData(100)]
    public void Una_escala_minima_o_maxima_del_modelo_da_una_colocacion_finita(double scale)
    {
        foreach (LayerShape shape in new[] { LayerShape.None, LayerShape.None with { ScaleX = 100, ScaleY = 100 }, LayerShape.None with { ScaleX = 1e-4 } })
        {
            var transform = new LayerTransform(0, 0, scale, 0, 1) { Shape = shape.IsNone ? null : shape };
            if (LayerLayout.Place(1920, 1080, 1920, 1080, transform) is not { } p)
            {
                continue;
            }

            Assert.True(float.IsFinite(p.Scale) && p.Scale >= 0, $"{scale}: {p.Scale}");
            Assert.True(float.IsFinite(p.TopLeft.X) && float.IsFinite(p.BottomRight.Y), $"{scale}: {p.TopLeft} {p.BottomRight}");
            Assert.True(float.IsFinite(p.MaxAxisScale) && float.IsFinite(p.EdgeScaleX) && float.IsFinite(p.EdgeScaleY));
        }
    }
}
