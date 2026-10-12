using Rodaje.Core.Effects;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Effects;

/// <summary>La geometría del reencuadre 360: hacia dónde mira cada ángulo y qué parte del equirectangular se ve.</summary>
public sealed class EquirectangularTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(0, 0, 0.5, 0.5)]
    [InlineData(90, 0, 0.75, 0.5)]
    [InlineData(-90, 0, 0.25, 0.5)]
    [InlineData(180, 0, 1.0, 0.5)]
    [InlineData(0, 45, 0.5, 0.25)]
    [InlineData(0, -90, 0.5, 1.0)]
    [InlineData(45, -30, 0.625, 0.5 + (30.0 / 180))]
    public void The_center_of_the_view_is_where_the_camera_looks(double yaw, double pitch, double u, double v)
    {
        (double su, double sv) = Equirectangular.SourceUv(0.5, 0.5, yaw, pitch, 0, 90, 16.0 / 9, stereographic: false);
        Assert.Equal(u, su, 6);
        Assert.Equal(v, sv, 6);
    }

    [Fact]
    public void The_edges_of_a_90_degree_view_are_45_degrees_to_each_side()
    {
        (double left, double middle) = Equirectangular.SourceUv(0, 0.5, 0, 0, 0, 90, 16.0 / 9, false);
        (double right, _) = Equirectangular.SourceUv(1, 0.5, 0, 0, 0, 90, 16.0 / 9, false);
        Assert.Equal(0.5 - 0.125, left, 9);
        Assert.Equal(0.5 + 0.125, right, 9);
        Assert.Equal(0.5, middle, 9);

        // Arriba en el centro: el medio alto vertical es tan(45°)·9/16.
        (_, double top) = Equirectangular.SourceUv(0.5, 0, 0, 0, 0, 90, 16.0 / 9, false);
        Assert.Equal(0.5 - (Math.Atan(9.0 / 16) / Math.PI), top, 9);
    }

    [Fact]
    public void Positive_roll_turns_the_image_clockwise()
    {
        // Con el horizonte girado 90° en sentido horario, lo que estaba a la derecha de la cámara aparece abajo.
        (double u, double v) = Equirectangular.SourceUv(0.5, 1, 0, 0, 90, 90, 1, false);
        Assert.Equal(0.625, u, 9);
        Assert.Equal(0.5, v, 9);
    }

    [Fact]
    public void The_basis_is_orthonormal_for_any_angle()
    {
        var random = new Random(360);
        for (int i = 0; i < 1000; i++)
        {
            (Equirectangular.Vector3 r, Equirectangular.Vector3 u) = Equirectangular.Basis(random.Next(-180, 181), random.Next(-90, 91), random.Next(-180, 181));
            Assert.Equal(1, Dot(r, r), 9);
            Assert.Equal(1, Dot(u, u), 9);
            Assert.True(Math.Abs(Dot(r, u)) < Tolerance);
        }
    }

    [Fact]
    public void The_little_planet_looking_down_shows_the_nadir_in_the_middle_and_the_horizon_around()
    {
        (_, double center) = Equirectangular.SourceUv(0.5, 0.5, 0, -90, 0, 270, 1, stereographic: true);
        Assert.Equal(1, center, 6);
        // Con 270° de campo, el borde horizontal ve 135° desde el nadir: 45° sobre el horizonte.
        (_, double edge) = Equirectangular.SourceUv(1, 0.5, 0, -90, 0, 270, 1, stereographic: true);
        Assert.Equal(0.25, edge, 6);
    }

    [Fact]
    public void The_rectilinear_view_never_opens_past_its_limit()
    {
        Assert.Equal(Equirectangular.HalfWidth(Equirectangular.MaxRectilinearFov, false), Equirectangular.HalfWidth(300, false));
        Assert.True(double.IsFinite(Equirectangular.HalfWidth(330, true)));
    }

    [Fact]
    public void The_effect_is_registered_behind_the_360_feature()
    {
        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Reframe360)!;
        Assert.Equal(EffectFeatures.Video360, definition.Feature);
        Assert.Equal(EffectStage.Spatial, definition.Stage);
        Assert.Equal([BuiltInEffects.Reframe360Parameters.Yaw, BuiltInEffects.Reframe360Parameters.Pitch, BuiltInEffects.Reframe360Parameters.Roll,
            BuiltInEffects.Reframe360Parameters.Fov], definition.Parameters.Select(p => p.Name));
        Assert.All(definition.Parameters, p => Assert.True(p.Animatable));
        Assert.Null(EffectRegistry.Find(BuiltInEffects.Mirror)!.Feature);
    }

    private static double Dot(Equirectangular.Vector3 a, Equirectangular.Vector3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
