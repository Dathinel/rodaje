namespace Rodaje.App.Tests.Vision;

public sealed class VisionDialogTests
{
    [Fact]
    public void ElReencuadreAdvierteElFormatoNuevoAntesDeAplicar()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Views", "Vision", "VisionDialogs.cs"));
        Assert.Contains("ReframeFormatWarning(int width, int height)", source, StringComparison.Ordinal);
        Assert.Contains("Atención: esta acción cambiará el formato del proyecto a {width}×{height}.", source, StringComparison.Ordinal);
        Assert.Contains("formatWarning.Text = ReframeFormatWarning(width, height);", source, StringComparison.Ordinal);
        int warning = source.IndexOf("UpdateFormatWarning();", StringComparison.Ordinal);
        int dialog = source.IndexOf("ContentDialog dialog = AnalysisUi.Dialog(root, ReframeTitle", StringComparison.Ordinal);
        Assert.True(warning >= 0 && warning < dialog, "La advertencia debe prepararse antes de que el diálogo permita aplicar.");
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
