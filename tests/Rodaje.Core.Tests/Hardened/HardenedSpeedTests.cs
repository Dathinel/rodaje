using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Hardened;

/// <summary>
/// Versiones endurecidas de dos pruebas de velocidad con margen de más:
/// <c>SpeedEditsTests.SetCurve_with_a_ramp_takes_the_duration_of_its_mean_speed</c> aceptaba de 59 a 61 cuadros
/// (con dos cuadros de holgura un error de redondeo de un cuadro entero pasaba) y
/// <c>PasteAttributesTests.A_curve_is_fitted_to_each_target</c> aceptaba que el destino perdiera hasta dos cuadros de
/// material. El cálculo es entero y determinista: el resultado tiene que ser exacto.
/// </summary>
public sealed class HardenedSpeedTests
{
    [Fact]
    public void A_ramp_from_100_to_200_percent_lasts_exactly_the_material_over_its_mean_speed()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 90, 0);

        TimelineEdits.SetCurve(f.Ctx, "c", new SpeedCurve { Points = [new(0, 100), new(10_000, 200)] });

        Clip clip = f.Clip("c");
        // Velocidad media 1,5×: 90 cuadros de material duran 60 en la secuencia, y el material no cambia.
        Assert.Equal(F(60), clip.Duration);
        Assert.Equal(Time.Zero, clip.SourceIn);
        Assert.Equal(F(90), clip.SourceOut);
        Assert.Equal(Time.Zero, clip.Start);
        f.AssertValid();
    }

    [Fact]
    public void Pasting_a_curve_keeps_all_the_material_of_the_target()
    {
        var f = new TrimFixture().Add("v1", "s", "m2", 0, 100, 0).Add("v2", "t", "m1", 0, 40, 0);
        TimelineEdits.SetCurve(f.Ctx, "s", SpeedPresets.Curve(SpeedPreset.Bullet));
        Time sourceDuration = f.Clip("s").Duration;

        ClipAttributeEdits.Paste(f.Ctx, ["t"], ClipAttributes.Capture(f.Sequence, "s"), ClipAttributeKinds.Speed);

        f.AssertValid();
        Clip target = f.Clip("t");
        Assert.Equal(f.Clip("s").Speed!.Curve!.Points, target.Speed!.Curve!.Points);
        Assert.Equal(Time.Zero, target.SourceIn);
        Assert.Equal(F(40), target.SourceOut);
        Assert.Equal(F(22), target.Duration);
        // El origen no se toca al pegar.
        Assert.Equal(sourceDuration, f.Clip("s").Duration);
    }
}
