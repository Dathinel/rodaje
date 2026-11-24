using Rodaje.App.Services;

namespace Rodaje.App.Tests.Shell;

public class ClosingNoticeTests
{
    [Fact]
    public void Sin_trabajos_no_se_pregunta() => Assert.Null(ClosingNotice.Describe(0));

    [Theory]
    [InlineData(1, "Hay un trabajo en curso")]
    [InlineData(3, "Hay 3 trabajos en curso")]
    public void Con_trabajos_la_pregunta_dice_cuantos_y_que_se_cancelan(int active, string expected)
    {
        string text = ClosingNotice.Describe(active)!;
        Assert.StartsWith(expected, text, StringComparison.Ordinal);
        Assert.Contains("se cancela", text, StringComparison.Ordinal);
    }
}
