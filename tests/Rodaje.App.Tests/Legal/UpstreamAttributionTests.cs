using Rodaje.App.Services;

namespace Rodaje.App.Tests.Legal;

/// <summary>El crédito de la interfaz es una obligación de LICENSE (término adicional 1): sus cadenas no cambian sin querer.</summary>
public class UpstreamAttributionTests
{
    [Fact]
    public void Cadenas_y_url_fijadas()
    {
        Assert.Equal("https://github.com/Dathinel/rodaje", UpstreamAttribution.RepositoryUrl);
        Assert.Equal("Código abierto upstream: Rodaje", UpstreamAttribution.ShortCredit);
        Assert.Equal(
            "Basado en el editor de video de código abierto de Dathinel / Rodaje (https://github.com/Dathinel/rodaje)",
            UpstreamAttribution.Notice);
        Assert.Equal(new Uri("https://github.com/Dathinel/rodaje"), UpstreamAttribution.RepositoryUri);
        Assert.Equal(Uri.UriSchemeHttps, UpstreamAttribution.RepositoryUri.Scheme);
    }

    [Fact]
    public void El_aviso_es_el_que_exige_LICENSE()
    {
        string license = File.ReadAllText(Path.Combine(RepositoryRoot(), "LICENSE"));
        Assert.Contains($"\"{UpstreamAttribution.Notice}\"", license, StringComparison.Ordinal);
        Assert.Contains(UpstreamAttribution.RepositoryUrl, license, StringComparison.Ordinal);
        Assert.StartsWith(UpstreamAttribution.Copyright, license, StringComparison.Ordinal);
    }

    [Fact]
    public void Acerca_de_lleva_los_avisos_legales_de_la_GPLv3()
    {
        Assert.Contains("GNU GPLv3", UpstreamAttribution.LicenseLine, StringComparison.Ordinal);
        Assert.Contains("SIN NINGUNA GARANTÍA", UpstreamAttribution.LicenseLine, StringComparison.Ordinal);
        Assert.Contains("LICENSE.txt", UpstreamAttribution.LicenseLine, StringComparison.Ordinal);
        Assert.Contains("TRADEMARK.md", UpstreamAttribution.LicenseLine, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
