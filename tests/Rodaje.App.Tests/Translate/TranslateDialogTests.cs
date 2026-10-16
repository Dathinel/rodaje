namespace Rodaje.App.Tests.Translate;

public sealed class TranslateDialogTests
{
    [Fact]
    public void ElTextoDeEsperaAclaraQueCerrarCancelaLaTraduccion()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Views", "Translate", "TranslateDialog.cs"));
        Assert.Contains("ClosingNotice = \"Cerrar cancela la traducción.\"", source, StringComparison.Ordinal);
        Assert.Contains("({ClosingNotice})", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sigue en Trabajos si cierras", source, StringComparison.Ordinal);
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
