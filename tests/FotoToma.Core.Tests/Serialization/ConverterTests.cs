using System.Text;
using System.Text.Json;
using CsCheck;
using FotoToma.Core.Serialization;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Tests.Serialization;

public class ConverterTests
{
    private static Rational ReadRational(string json) =>
        JsonSerializer.Deserialize(json, ProjectJsonContext.Default.Rational);

    private static Time ReadTime(string json) =>
        JsonSerializer.Deserialize(json, ProjectJsonContext.Default.Time);

    [Fact]
    public void Time_RoundTripsAnyTickCount()
    {
        Gen.Long.Sample(ticks =>
        {
            Time time = Time.FromTicks(ticks);
            string json = JsonSerializer.Serialize(time, ProjectJsonContext.Default.Time);
            Assert.Equal(ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), json);
            Assert.Equal(time, ReadTime(json));
        });
    }

    [Theory]
    [InlineData("\"10\"")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Time_RejectsNonIntegers(string json)
    {
        var ex = Assert.Throws<JsonException>(() => ReadTime(json));
        Assert.Contains("ticks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rational_RoundTripsAnyPositiveFraction()
    {
        Gen.Select(Gen.Long[1, long.MaxValue], Gen.Long[1, long.MaxValue]).Sample((num, den) =>
        {
            var rate = new Rational(num, den);
            string json = JsonSerializer.Serialize(rate, ProjectJsonContext.Default.Rational);
            Assert.Equal(rate, ReadRational(json));
        });
    }

    [Fact]
    public void Rational_WritesNumDenObject()
    {
        string json = JsonSerializer.Serialize(Rational.Ntsc30, ProjectJsonContext.Default.Rational);
        Assert.Equal("{\n  \"num\": 30000,\n  \"den\": 1001\n}", json);
    }

    [Fact]
    public void Rational_NormalizesIgnoresUnknownFieldsAndAcceptsAnyOrder()
    {
        Assert.Equal(Rational.Fps30, ReadRational("""{"den": 2, "extra": {"a": [1, 2]}, "num": 60}"""));
    }

    [Theory]
    [InlineData("30")]
    [InlineData("\"30000/1001\"")]
    [InlineData("null")]
    [InlineData("[30000, 1001]")]
    [InlineData("{}")]
    [InlineData("""{"num": 30}""")]
    [InlineData("""{"den": 1}""")]
    [InlineData("""{"num": 0, "den": 1}""")]
    [InlineData("""{"num": 30, "den": 0}""")]
    [InlineData("""{"num": -30, "den": 1}""")]
    [InlineData("""{"num": 30, "den": -1}""")]
    [InlineData("""{"num": "30", "den": 1}""")]
    [InlineData("""{"num": 30, "den": 1.5}""")]
    [InlineData("""{"num": 30, "den": null}""")]
    public void Rational_RejectsInvalidValues(string json)
    {
        var ex = Assert.Throws<JsonException>(() => ReadRational(json));
        Assert.Contains("Una tasa debe ser", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Converters_RejectNullWriter()
    {
        var options = ProjectJsonContext.Default.Options;
        Assert.Throws<ArgumentNullException>(() => new TimeJsonConverter().Write(null!, Time.Zero, options));
        Assert.Throws<ArgumentNullException>(() => new RationalJsonConverter().Write(null!, Rational.Fps30, options));
    }

    [Fact]
    public void Converters_ReadDirectly()
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("""{"num": 25, "den": 1}"""));
        reader.Read();
        Assert.Equal(Rational.Fps25, new RationalJsonConverter().Read(ref reader, typeof(Rational), ProjectJsonContext.Default.Options));

        var timeReader = new Utf8JsonReader(Encoding.UTF8.GetBytes("42"));
        timeReader.Read();
        Assert.Equal(Time.FromTicks(42), new TimeJsonConverter().Read(ref timeReader, typeof(Time), ProjectJsonContext.Default.Options));
    }
}
