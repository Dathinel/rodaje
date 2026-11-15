using System.Diagnostics;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;
using Timecode = Rodaje.Core.Timing.Timecode;

namespace Rodaje.Core.Tests.Timing;

public sealed class TimecodeTests
{
    [Theory]
    [InlineData(24000, 1001, 24, false)]
    [InlineData(24, 1, 24, false)]
    [InlineData(25, 1, 25, false)]
    [InlineData(30000, 1001, 30, true)]
    [InlineData(30, 1, 30, false)]
    [InlineData(48000, 1001, 48, false)]
    [InlineData(48, 1, 48, false)]
    [InlineData(50, 1, 50, false)]
    [InlineData(60000, 1001, 60, true)]
    [InlineData(60, 1, 60, false)]
    [InlineData(2997, 100, 30, false)]
    [InlineData(5994, 100, 60, false)]
    [InlineData(47, 2, 24, false)]
    [InlineData(49, 2, 25, false)]
    [InlineData(long.MaxValue, long.MaxValue / 30, 30, false)]
    public void RatesUseTheNearestSupportedBase(long numerator, long denominator, int nominalBase, bool dropFrame)
    {
        Rational rate = new(numerator, denominator);
        Assert.Equal(nominalBase, Timecode.NominalBase(rate));
        Assert.Equal(dropFrame, Timecode.CanDropFrame(rate));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(26, 1)]
    [InlineData(120, 1)]
    [InlineData(long.MaxValue, 1)]
    [InlineData(1, long.MaxValue)]
    public void UnsupportedRatesAreRejected(long numerator, long denominator)
    {
        Rational rate = new(numerator, denominator);
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.NominalBase(rate));
        Assert.False(Timecode.TryParse("00:00:00:00", rate, out Timecode result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(30000, "00:00:59;29", 1799)]
    [InlineData(30000, "00:01:00;02", 1800)]
    [InlineData(30000, "00:01:59;29", 3597)]
    [InlineData(30000, "00:02:00;02", 3598)]
    [InlineData(30000, "00:09:59;29", 17981)]
    [InlineData(30000, "00:10:00;00", 17982)]
    [InlineData(30000, "00:10:00;01", 17983)]
    [InlineData(30000, "00:10:59;29", 19781)]
    [InlineData(30000, "00:11:00;02", 19782)]
    [InlineData(30000, "00:59:59;29", 107891)]
    [InlineData(30000, "01:00:00;00", 107892)]
    [InlineData(30000, "23:59:59;29", 2589407)]
    [InlineData(60000, "00:00:59;59", 3599)]
    [InlineData(60000, "00:01:00;04", 3600)]
    [InlineData(60000, "00:01:59;59", 7195)]
    [InlineData(60000, "00:02:00;04", 7196)]
    [InlineData(60000, "00:09:59;59", 35963)]
    [InlineData(60000, "00:10:00;00", 35964)]
    [InlineData(60000, "00:10:00;03", 35967)]
    [InlineData(60000, "00:10:59;59", 39563)]
    [InlineData(60000, "00:11:00;04", 39564)]
    [InlineData(60000, "23:59:59;59", 5178815)]
    public void DropFrameLabelsMatchRealFrames(int numerator, string text, long expected)
    {
        Rational rate = new(numerator, 1001);
        Timecode parsed = Timecode.Parse(text, rate);
        Assert.Equal(expected, parsed.Frame);
        Assert.Equal(numerator / 1000, parsed.Base);
        Assert.True(parsed.DropFrame);
        Assert.Equal(text, parsed.Format());
        Assert.Equal(text, parsed.ToString());
        Assert.Equal(parsed, Timecode.FromFrame(expected, rate, true));
    }

    [Theory]
    [InlineData(24000, 1001, "00:01:00:00", 1440)]
    [InlineData(24000, 1001, "23:59:59:23", 2073599)]
    [InlineData(24, 1, "01:02:03:04", 89356)]
    [InlineData(25, 1, "01:02:03:04", 93079)]
    [InlineData(30, 1, "00:01:00:00", 1800)]
    [InlineData(30000, 1001, "00:10:00:00", 18000)]
    [InlineData(48, 1, "00:01:00:47", 2927)]
    [InlineData(50, 1, "00:01:00:49", 3049)]
    [InlineData(60, 1, "00:01:00:00", 3600)]
    [InlineData(60000, 1001, "00:10:00:00", 36000)]
    public void NonDropLabelsUseNominalBase(int numerator, int denominator, string text, long expected)
    {
        Rational rate = new(numerator, denominator);
        Timecode parsed = Timecode.Parse(text, rate);
        Assert.Equal(expected, parsed.Frame);
        Assert.False(parsed.DropFrame);
        Assert.Equal(text, parsed.Format());
        Assert.Equal(parsed, Timecode.FromFrame(expected, rate, false));
        Assert.Equal(Time.FromFrames(expected, rate), parsed.ToTime(rate));
    }

    [Theory]
    [InlineData("00:01:00;02")]
    [InlineData("00:01:00.02")]
    [InlineData("00:01:00,02")]
    public void DropFrameSeparatorsAreNormalized(string text)
    {
        Assert.True(Timecode.TryParse(text, Rational.Ntsc30, out Timecode result));
        Assert.Equal(1800, result.Frame);
        Assert.Equal("00:01:00;02", result.Format());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("99:99:99:99")]
    [InlineData("24:00:00:00")]
    [InlineData("00:60:00:00")]
    [InlineData("00:00:60:00")]
    [InlineData("00:00:00:30")]
    [InlineData("00:01:00;00")]
    [InlineData("00:01:00;01")]
    [InlineData("00:11:00.01")]
    [InlineData("aa:bb:cc:dd")]
    [InlineData("000:00:00:00")]
    [InlineData("00:000:00:00")]
    [InlineData("00:00:000:00")]
    [InlineData("00:00:00:000")]
    [InlineData("-1:00:00:00")]
    [InlineData("+1:00:00:00")]
    [InlineData("00:00:00:-1")]
    [InlineData("00:00:00: 1")]
    [InlineData("00:00:00:0a")]
    [InlineData("00:00:00/00")]
    [InlineData("00;00:00:00")]
    [InlineData("00:00;00:00")]
    [InlineData(" 00:00:00:00")]
    [InlineData("00:00:00:00 ")]
    [InlineData("0:00:00:00")]
    [InlineData("００:00:00:00")]
    public void InvalidTextFailsWithoutOtherExceptions(string? text)
    {
        Assert.Throws<FormatException>(() => Timecode.Parse(text!, Rational.Ntsc30));
        Assert.False(Timecode.TryParse(text, Rational.Ntsc30, out Timecode result));
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData("00:01:00;00")]
    [InlineData("00:01:00;01")]
    [InlineData("00:01:00;02")]
    [InlineData("00:01:00;03")]
    public void FourLabelsAreSkippedAtNtsc60(string text)
    {
        Assert.Throws<FormatException>(() => Timecode.Parse(text, Rational.Ntsc60));
        Assert.False(Timecode.TryParse(text, Rational.Ntsc60, out _));
    }

    [Theory]
    [InlineData(24000, 1001)]
    [InlineData(24, 1)]
    [InlineData(25, 1)]
    [InlineData(30, 1)]
    [InlineData(48, 1)]
    [InlineData(50, 1)]
    [InlineData(60, 1)]
    [InlineData(2997, 100)]
    public void UnsupportedDropFrameThrowsArgumentException(int numerator, int denominator)
    {
        Rational rate = new(numerator, denominator);
        foreach (char separator in new[] { ';', '.', ',' })
        {
            string text = $"00:00:00{separator}00";
            Assert.Throws<ArgumentException>(() => Timecode.Parse(text, rate));
            Assert.False(Timecode.TryParse(text, rate, out _));
        }
        Assert.Throws<ArgumentException>(() => Timecode.FromFrame(0, rate, true));
        Assert.Throws<ArgumentException>(() => Timecode.FromTime(Time.Zero, rate, true));
    }

    [Theory]
    [InlineData(24, 1, false, 2073600)]
    [InlineData(24000, 1001, false, 2073600)]
    [InlineData(25, 1, false, 2160000)]
    [InlineData(30, 1, false, 2592000)]
    [InlineData(48, 1, false, 4147200)]
    [InlineData(50, 1, false, 4320000)]
    [InlineData(60, 1, false, 5184000)]
    [InlineData(30000, 1001, true, 2589408)]
    [InlineData(60000, 1001, true, 5178816)]
    public void FrameFactoriesWrapAtTwentyFourHours(int numerator, int denominator, bool dropFrame, long perDay)
    {
        Rational rate = new(numerator, denominator);
        Assert.Equal(0, Timecode.FromFrame(perDay, rate, dropFrame).Frame);
        Assert.Equal(1, Timecode.FromFrame(perDay * 3 + 1, rate, dropFrame).Frame);
        Assert.Equal(long.MaxValue % perDay, Timecode.FromFrame(long.MaxValue, rate, dropFrame).Frame);
        Timecode last = Timecode.FromFrame(perDay - 1, rate, dropFrame);
        Assert.StartsWith("23:59:59", last.Format(), StringComparison.Ordinal);
        Assert.Equal(last, Timecode.Parse(last.Format(), rate));
        Assert.Equal(0, Timecode.FromTime(Time.FromFrames(perDay, rate), rate, dropFrame).Frame);
    }

    [Theory]
    [InlineData(24000, 1001, false)]
    [InlineData(25, 1, false)]
    [InlineData(30000, 1001, true)]
    [InlineData(60000, 1001, true)]
    public void FromTimeUsesFloorRounding(int numerator, int denominator, bool dropFrame)
    {
        Rational rate = new(numerator, denominator);
        Time start = Time.FromFrames(1800, rate);
        Assert.Equal(1800, Timecode.FromTime(start, rate, dropFrame).Frame);
        Assert.Equal(1799, Timecode.FromTime(new Time(start.Ticks - 1), rate, dropFrame).Frame);
        Assert.Equal(start, Timecode.FromTime(start, rate, dropFrame).ToTime(rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.FromTime(new Time(-1), rate, dropFrame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(26)]
    [InlineData(120)]
    public void ConstructorRejectsInvalidBases(int nominalBase)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timecode(0, nominalBase, false));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(48)]
    [InlineData(50)]
    public void ConstructorRejectsDropFrameForOtherBases(int nominalBase)
    {
        Assert.Throws<ArgumentException>(() => new Timecode(0, nominalBase, true));
    }

    [Fact]
    public void ConstructorAndAdditionKeepAbsoluteFramesAndCheckBounds()
    {
        Timecode original = new(2589407, 30, true);
        Timecode next = original.AddFrames(1);
        Assert.Equal(2589408, next.Frame);
        Assert.Equal(30, next.Base);
        Assert.True(next.DropFrame);
        Assert.Equal("00:00:00;00", next.Format());
        Assert.Equal(original, next.AddFrames(-1));
        Assert.Equal(original, original.AddFrames(0));
        Assert.Equal("00:01:00;02", new Timecode(1799, 30, true).AddFrames(1).Format());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timecode(-1, 30, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.FromFrame(-1, Rational.Ntsc30, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.AddFrames(-2589408));
        Assert.Throws<OverflowException>(() => new Timecode(long.MaxValue, 30, false).AddFrames(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => original.AddFrames(long.MinValue));
    }

    [Fact]
    public void DefaultRateIsRejectedWithoutDivisionByZero()
    {
        Assert.False(Timecode.CanDropFrame(default));
        Assert.False(Timecode.TryParse("00:00:00:00", default, out Timecode result));
        Assert.Equal(default, result);
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.NominalBase(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.FromFrame(0, default, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => Timecode.Parse("00:00:00:00", default));
    }

    [Fact]
    public void EveryNtsc30DropFrameInTwentyFourHoursRoundTrips()
    {
        for (long frame = 0; frame < 2589408; frame++)
        {
            Timecode parsed = Timecode.Parse(Timecode.FromFrame(frame, Rational.Ntsc30, true).Format(), Rational.Ntsc30);
            if (parsed.Frame != frame)
            {
                Assert.Fail($"El cuadro {frame} volvió como {parsed.Frame}.");
            }
        }
    }

    // Mide contra el reloj (en Release; en Debug o con cobertura tarda varias veces más): solo la corre nocturna.yml.
    [Fact]
    [Trait("Categoria", "Rendimiento")]
    public void EveryNtsc30DropFrameInTwentyFourHoursRoundTripsInUnderOneSecond()
    {
        // Calienta la misma ruta antes de medir, incluida la compilación diferida del formato.
        for (int frame = 0; frame < 10000; frame++)
            _ = Timecode.Parse(Timecode.FromFrame(frame, Rational.Ntsc30, true).Format(), Rational.Ntsc30);

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (long frame = 0; frame < 2589408; frame++)
        {
            Timecode parsed = Timecode.Parse(Timecode.FromFrame(frame, Rational.Ntsc30, true).Format(), Rational.Ntsc30);
            if (parsed.Frame != frame)
                Assert.Fail($"El cuadro {frame} volvió como {parsed.Frame}.");
        }
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"El recorrido tardó {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }
}
