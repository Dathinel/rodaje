using System.Globalization;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Hallazgo 31 de la auditoría del 24-oct: lo escrito en un cuadro numérico sin Intro se confirma al empezar a perder el foco,
/// antes de que el clic que lo mueve elija otro clip. Aquí, sin WinUI, qué se confirma.
/// </summary>
public class TypedNumberTests
{
    private static double? Parse(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    [Theory]
    [InlineData("150", 100, 150.0)]
    [InlineData("100", 100, null)]
    [InlineData("", 100, null)]
    [InlineData("abc", 100, null)]
    [InlineData("1e999", 100, null)]
    [InlineData("-5", 0, -5.0)]
    public void Pending_SoloUnNumeroDistinto(string text, double current, double? expected) =>
        Assert.Equal(expected, TypedNumber.Pending(text, current, Parse));

    [Fact]
    public void Pending_ConVariosClipsDistintos_ConfirmaLoEscrito()
    {
        Assert.Equal(150, TypedNumber.Pending("150", double.NaN, Parse));
        Assert.Null(TypedNumber.Pending("", double.NaN, Parse));
    }
}
