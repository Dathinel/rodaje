using System.Globalization;
using Rodaje.Media.Ai;

namespace Rodaje.Media.Tests.Ai;

/// <summary>
/// Los «GB» de los avisos de IA llevan coma decimal en cualquier cultura del hilo, sin pedir «es» por nombre (la app publica
/// con <c>InvariantGlobalization</c> y eso lanzaba al avisar de poca memoria).
/// </summary>
public class AiMachineGbTests
{
    [Theory]
    [InlineData("")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void ComaDecimalEnCualquierCultura(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture, previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            Assert.Equal("1,5 GB", AiMachine.Gb(1536));
            Assert.Equal("16 GB", AiMachine.Gb(16_384));
            Assert.Equal("1000 MB", AiMachine.Gb(1000));
            Assert.Equal("512 MB", AiMachine.Gb(512));
            Assert.Contains("1,5 GB de VRAM", new AiMachine(true, false, Rodaje.Media.Gpu.GpuVendor.Nvidia, "GPU", 1536, 0, 8192, 4096, 8).Describe(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
