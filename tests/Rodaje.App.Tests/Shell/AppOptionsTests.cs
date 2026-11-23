namespace Rodaje.App.Tests.Shell;

/// <summary>Las corridas automáticas van de fondo salvo con <c>--visible</c>; una sesión normal nunca.</summary>
public sealed class AppOptionsTests
{
    [Theory]
    [InlineData(new[] { "--autoprueba" }, true)]
    [InlineData(new[] { "--medir", "5" }, true)]
    [InlineData(new[] { "--arranque" }, true)]
    [InlineData(new[] { "--caos", "7", "5" }, true)]
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

    [Fact]
    public void Caos_LeeSemillaYMinutosYDejaElCorpusComoArchivos()
    {
        AppOptions options = AppOptions.Parse(["--caos", "42", "0.5", "--sin-audio", "a.mp4"]);
        Assert.Equal(42, options.ChaosSeed);
        Assert.Equal(0.5, options.ChaosMinutes);
        Assert.True(options.IsAutomated);
        Assert.Equal("a.mp4", Path.GetFileName(Assert.Single(options.Files)));
    }
}
