using Rodaje.App.Views.Timeline;

namespace Rodaje.App.Tests.TimelineLogic;

public sealed class MarkerLabelLayoutTests
{
    [Fact]
    public void Truncate_WhenTextFitsWithinAvailableWidth_ReturnsOriginalText()
    {
        string name = "Intro";
        // 5 caracteres * 6px = 30px <= 50px
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 50, charWidth: 6.0);

        Assert.Equal("Intro", result);
    }

    [Fact]
    public void Truncate_WhenTwoMarkersWouldOverlap_ShortensLeftWithEllipsis()
    {
        string name = "Entrada principal";
        // 17 caracteres * 6px = 102px > 30px
        // Con disponible = 30px, caben (30 - 6)/6 = 4 caracteres + ellipsis ("…") -> "Entr…"
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 30, charWidth: 6.0);

        Assert.Equal("Entr…", result);
    }

    [Fact]
    public void Truncate_WhenNotEvenFirstCharFits_ReturnsNull()
    {
        string name = "Entrada";
        // Primer carácter (6px) + ellipsis (6px) = 12px > 11px
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 11, charWidth: 6.0);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Truncate_WhenAvailableWidthIsZeroOrNegative_ReturnsNull(double available)
    {
        string? result = MarkerLabelLayout.Truncate("Marcador", available);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Truncate_WhenNameIsNullOrEmpty_ReturnsNull(string? name)
    {
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 100);

        Assert.Null(result);
    }

    [Fact]
    public void Truncate_WithCustomEllipsis_UsesSpecifiedEllipsis()
    {
        string name = "Sección destacada";
        // Con "..." de 3 chars (18px) y disponible de 36px: (36 - 18)/6 = 3 chars -> "Sec..."
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 36, charWidth: 6.0, ellipsis: "...");

        Assert.Equal("Sec...", result);
    }

    [Fact]
    public void Truncate_WithMeasureFunction_RespectsMeasureDelegate()
    {
        string name = "abcdefgh";
        // Medida no uniforme: cada carácter mide 10px, ellipsis mide 15px
        double Measure(string s) => s.EndsWith('…') ? ((s.Length - 1) * 10.0) + 15.0 : s.Length * 10.0;

        // Disponible 35px: "a…" = 10+15 = 25px <= 35px; "ab…" = 20+15 = 35px <= 35px; "abc…" = 30+15 = 45px > 35px
        string? result = MarkerLabelLayout.Truncate(name, availableWidth: 35, measure: Measure);

        Assert.Equal("ab…", result);
    }

    [Fact]
    public void AvailableWidth_WithoutNextMarker_ReturnsPositiveInfinity()
    {
        double width = MarkerLabelLayout.AvailableWidth(x: 100, nextMarkerX: null);

        Assert.True(double.IsPositiveInfinity(width));
    }

    [Fact]
    public void AvailableWidth_WithNextMarker_SubtractsCurrentOffsetFromNext()
    {
        // current at 100, label starts at 100 + 8 = 108. Next marker at 150. Available = 42.
        double width = MarkerLabelLayout.AvailableWidth(x: 100, nextMarkerX: 150, labelOffset: 8);

        Assert.Equal(42, width);
    }

    [Fact]
    public void AvailableWidth_WhenNextMarkerIsEqualOrBefore_ReturnsZero()
    {
        double widthSame = MarkerLabelLayout.AvailableWidth(x: 100, nextMarkerX: 100, labelOffset: 8);
        double widthBehind = MarkerLabelLayout.AvailableWidth(x: 100, nextMarkerX: 90, labelOffset: 8);

        Assert.Equal(0, widthSame);
        Assert.Equal(0, widthBehind);
    }
}
