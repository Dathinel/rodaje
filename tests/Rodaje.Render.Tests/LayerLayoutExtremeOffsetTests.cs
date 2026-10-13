using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests;

/// <summary>
/// Tarea 92: un desplazamiento, un ancla o una corrección de estabilizar finitos en double pero que no caben en un float
/// (la GPU coloca en float) no dan vértices infinitos: la capa no se dibuja. Lo que cabe se coloca igual que antes.
/// </summary>
public sealed class LayerLayoutExtremeOffsetTests
{
    public static readonly TheoryData<double> Huge = [1e39, -1e39, 1e300, -1e300, double.MaxValue, double.MinValue];

    [Theory]
    [MemberData(nameof(Huge))]
    public void Un_valor_que_no_cabe_en_float_no_se_dibuja(double value)
    {
        LayerTransform[] transforms =
        [
            new(value, 0, 1, 0, 1),
            new(0, value, 1, 0, 1),
            new(0, 0, 1e30, 0, 1) { Shape = LayerShape.None with { ScaleX = 1e30 } },
        ];
        foreach (LayerTransform transform in transforms)
        {
            Assert.True(LayerLayout.Place(1920, 1080, 1920, 1080, transform) is null, $"{transform}");
            Assert.True(LayerLayout.PlaceNative(1920, 1080, 400, 100, transform) is null, $"nativa {transform}");
        }

        // Mover el ancla no mueve la imagen: se cancela y queda finita, o no se dibuja.
        foreach (LayerShape shape in new[] { LayerShape.None with { AnchorX = value }, LayerShape.None with { AnchorY = value, FlipY = true } })
        {
            if (LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity with { Shape = shape }) is { } p)
            {
                Assert.True(float.IsFinite(p.TopLeft.X) && float.IsFinite(p.TopLeft.Y) && float.IsFinite(p.BottomRight.X)
                    && float.IsFinite(p.BottomRight.Y), $"{shape}: {p.LocalToSequence}");
            }
        }

        // Estabilizar solo corrige el video (un título se coloca sin ella).
        Assert.Null(LayerLayout.Place(1920, 1080, 1920, 1080, LayerTransform.Identity with { Stabilization = new LayerStabilization(1, 0, value, 0) }));
    }

    [Fact]
    public void Los_limites_del_modelo_se_colocan_con_numeros_finitos()
    {
        double offset = Rodaje.Core.Effects.EffectRegistry.MaxOffset;
        double rotation = Rodaje.Core.Effects.EffectRegistry.MaxRotation;
        LayerTransform transform = new(-offset, offset, 100, rotation, 1)
        {
            Shape = new LayerShape(offset, -offset, 100, 100, 85, -85, true, true),
        };
        LayerPlacement p = LayerLayout.Place(1920, 1080, 1920, 1080, transform)!.Value;
        Assert.True(float.IsFinite(p.TopLeft.X) && float.IsFinite(p.BottomRight.Y) && float.IsFinite(p.Scale));
    }
}
