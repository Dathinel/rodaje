using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

/// <summary>Lo que proponen los diálogos de «Guardar como…» de las hojas de atajos.</summary>
public sealed class SheetFilesTests
{
    [Theory]
    [InlineData(SheetFormat.Html, "Atajos de Rodaje.html", ".html")]
    [InlineData(SheetFormat.Png, "Atajos de Rodaje.png", ".png")]
    public void CadaFormatoProponeSuNombreYSuExtension(SheetFormat format, string name, string extension)
    {
        Assert.Equal(name, SheetFiles.QuickSheetName(format));
        Assert.Equal(extension, SheetFiles.Extension(format));
        Assert.EndsWith(extension, SheetFiles.QuickSheetName(format), StringComparison.Ordinal);
    }

    [Fact]
    public void ElFiltroYElTituloSiguenAlFormato()
    {
        Assert.Equal(SheetFiles.HtmlFilter, SheetFiles.Filter(SheetFormat.Html));
        Assert.Equal(SheetFiles.PngFilter, SheetFiles.Filter(SheetFormat.Png));
        Assert.Contains("*.html", SheetFiles.HtmlFilter.Spec, StringComparison.Ordinal);
        Assert.Equal("*.png", SheetFiles.PngFilter.Spec);
        Assert.NotEqual(SheetFiles.DialogTitle(SheetFormat.Html), SheetFiles.DialogTitle(SheetFormat.Png));
    }

    [Fact]
    public void LaHojaCompletaConservaSuNombreDeSiempre()
    {
        Assert.Equal("Atajos de Rodaje (completa).html", SheetFiles.FullSheetName);
        Assert.StartsWith(SheetFiles.QuickSheetStem, SheetFiles.FullSheetName, StringComparison.Ordinal);
    }
}
