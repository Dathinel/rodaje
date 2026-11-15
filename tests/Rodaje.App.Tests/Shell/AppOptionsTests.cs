namespace Rodaje.App.Tests.Shell;

/// <summary>Las corridas automáticas van de fondo salvo con <c>--visible</c>; una sesión normal nunca.</summary>
public sealed class AppOptionsTests
{
    [Theory]
    [InlineData(new[] { "--autoprueba" }, true)]
    [InlineData(new[] { "--medir", "5" }, true)]
    [InlineData(new[] { "--arranque" }, true)]
    [InlineData(new[] { "--autoprueba", "--visible" }, false)]
    [InlineData(new[] { "--VISIBLE", "--medir", "5" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--visible" }, false)]
    public void RunsInBackground_SoloLasAutomaticasSinVisible(string[] args, bool expected)
    {
        Assert.Equal(expected, AppOptions.Parse(args).RunsInBackground);
    }

    [Fact]
    public void Visible_NoSeTomaComoArchivo()
    {
        Assert.Empty(AppOptions.Parse(["--arranque", "--visible"]).Files);
    }
}
