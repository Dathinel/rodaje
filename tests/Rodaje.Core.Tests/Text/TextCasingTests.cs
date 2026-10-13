using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Core.Tests.Text;

/// <summary>
/// Las mayúsculas de los títulos no dependen de la cultura del hilo ni piden una por nombre (la app publica con
/// <c>InvariantGlobalization</c>, donde eso lanza; lo prueba de verdad <c>Rodaje.AotSmoke</c>).
/// </summary>
public class TextCasingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("es-ES")]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    public void MayusculasYMinusculas_IgualesEnCualquierCultura(string culture) => WithCulture(culture, () =>
    {
        Assert.Equal("ÑANDÚ EN ISTMO", TextCasing.Apply("Ñandú en istmo", TextCase.Upper));
        Assert.Equal("ñandú en istmo", TextCasing.Apply("ÑANDÚ EN ISTMO", TextCase.Lower));
        Span<bool> lowered = stackalloc bool[4];
        Assert.Equal("ÁRBI", TextCasing.Apply("Árbi", TextCase.SmallCaps, lowered));
        Assert.Equal([false, true, true, true], lowered.ToArray());
    });

    private static void WithCulture(string name, Action action)
    {
        CultureInfo culture = CultureInfo.CurrentCulture, ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
