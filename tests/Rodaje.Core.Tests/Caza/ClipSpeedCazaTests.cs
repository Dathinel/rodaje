using System;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public class ClipSpeedCazaTests
{
    [Fact]
    public void Rate_AllowsMinimumConstantRateOfOneTenth()
    {
        var speed = new ClipSpeed { Rate = new Rational(1, 10) };

        Assert.Equal(new Rational(1, 10), speed.Rate);
    }

    [Fact]
    public void Rate_AllowsMaximumConstantRateOfOneHundred()
    {
        var speed = new ClipSpeed { Rate = new Rational(100, 1) };

        Assert.Equal(new Rational(100, 1), speed.Rate);
    }

    [Fact(Skip = "Defecto candidato: docs/arquitectura/nucleo.md dice «Velocidad constante (`Rate`, de 1/10× a 100×) o con curva (`Curve`), `Reverse`, `Freeze` (cuadro congelado), `PreservePitch` y …")]
    public void Rate_BelowOneTenth_ThrowsArgumentOutOfRangeException()
    {
        var speed = new ClipSpeed();

        Assert.Throws<ArgumentOutOfRangeException>(() => speed.Rate = new Rational(1, 20));
    }

    [Fact(Skip = "Defecto candidato: docs/arquitectura/nucleo.md dice «Velocidad constante (`Rate`, de 1/10× a 100×) o con curva (`Curve`), `Reverse`, `Freeze` (cuadro congelado), `PreservePitch` y …")]
    public void Rate_AboveOneHundred_ThrowsArgumentOutOfRangeException()
    {
        var speed = new ClipSpeed();

        Assert.Throws<ArgumentOutOfRangeException>(() => speed.Rate = new Rational(101, 1));
    }

    [Fact(Skip = "Defecto candidato: docs/arquitectura/nucleo.md dice «Velocidad constante (`Rate`, de 1/10× a 100×) o con curva (`Curve`), `Reverse`, `Freeze` (cuadro congelado), `PreservePitch` y …")]
    public void Rate_WhenCurveIsSet_ThrowsInvalidOperationException()
    {
        var speed = new ClipSpeed { Curve = new SpeedCurve() };

        Assert.Throws<InvalidOperationException>(() => speed.Rate = new Rational(2, 1));
    }

    [Fact]
    public void Curve_CanBeAssigned()
    {
        var curve = new SpeedCurve();
        var speed = new ClipSpeed { Curve = curve };

        Assert.Same(curve, speed.Curve);
    }

    [Fact]
    public void Reverse_CanBeConfigured()
    {
        var speed = new ClipSpeed { Reverse = true };

        Assert.True(speed.Reverse);

        speed.Reverse = false;

        Assert.False(speed.Reverse);
    }

    [Fact]
    public void Freeze_CanBeConfigured()
    {
        var speed = new ClipSpeed { Freeze = true };

        Assert.True(speed.Freeze);

        speed.Freeze = false;

        Assert.False(speed.Freeze);
    }

    [Fact]
    public void PreservePitch_CanBeConfigured()
    {
        var speed = new ClipSpeed { PreservePitch = false };

        Assert.False(speed.PreservePitch);

        speed.PreservePitch = true;

        Assert.True(speed.PreservePitch);
    }

    [Fact]
    public void FrameSampling_CanBeConfigured()
    {
        var speed = new ClipSpeed { FrameSampling = FrameSampling.Blend };

        Assert.Equal(FrameSampling.Blend, speed.FrameSampling);

        speed.FrameSampling = FrameSampling.OpticalFlow;

        Assert.Equal(FrameSampling.OpticalFlow, speed.FrameSampling);

        speed.FrameSampling = FrameSampling.Nearest;

        Assert.Equal(FrameSampling.Nearest, speed.FrameSampling);
    }
}
