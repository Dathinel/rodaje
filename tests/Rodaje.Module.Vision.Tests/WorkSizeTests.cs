namespace Rodaje.Module.Vision.Tests;

/// <summary>El «lado» de MODNet que pide el perfil de IA (1.1): área lado², lados múltiplos de 32 y la forma del cuadro.</summary>
public sealed class WorkSizeTests
{
    [Theory]
    [InlineData(1920, 1080, 512, 672, 384)]
    [InlineData(1920, 1080, 384, 512, 288)]
    [InlineData(1920, 1080, 640, 864, 480)]
    [InlineData(1080, 1920, 512, 384, 672)]
    public void El_lado_fija_el_area_de_trabajo(int width, int height, int side, int w, int h)
    {
        Assert.Equal((w, h), Matting.ModnetSize(width, height, side));
        Assert.Equal(0, w % 32);
        Assert.Equal(0, h % 32);
    }

    [Fact]
    public void Sin_lado_es_el_de_siempre() => Assert.Equal(Matting.ModnetSize(1920, 1080, Matting.DefaultSide), Matting.ModnetSize(1920, 1080));
}
