namespace Rodaje.App.Tests.Support;

/// <summary>
/// La app corre con <c>InvariantGlobalization</c>: un número formateado con <c>CultureInfo.CurrentCulture</c> sale con
/// punto decimal («0.5»). Los textos para el usuario usan <c>SpanishNumbers.Format</c>; se vigila en el código fuente.
/// </summary>
public class CurrentCultureNumbersTests
{
    // Usos de la cultura del hilo que no formatean números: pasar texto a minúsculas o mayúsculas.
    private static readonly string[] AllowedUses = [".ToLower(CultureInfo.CurrentCulture)", ".ToUpper(CultureInfo.CurrentCulture)"];

    // Pendientes conocidos fuera de los caminos de la tarea 42: enteros pequeños (cantidad de pistas, «Video 2»), que salen
    // igual en la invariante. Se listan línea por línea para que cualquier uso nuevo, aunque sea en el mismo archivo, falle.
    private static readonly (string File, string Line)[] KnownPending =
    [
        ("TitleBar.xaml.cs", "Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture)"),
        ("TitleBar.xaml.cs", "Text = string.Create(CultureInfo.CurrentCulture, $\"Video {number}\")"),
    ];

    [Fact]
    public void LaApp_NoFormateaNumerosConLaCulturaDelHilo()
    {
        string source = Path.Combine(RepositoryRoot(), "src", "Rodaje.App");
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string name = Path.GetFileName(file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsOffending(name, lines[i]))
                {
                    found.Add($"{Path.GetRelativePath(source, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(found);
    }

    [Fact]
    public void LaVigilanciaDetectaLosFormatosConLaCulturaDelHilo()
    {
        Assert.True(IsOffending("X.cs", "string s = string.Create(CultureInfo.CurrentCulture, $\"{v:0.0} s\");"));
        Assert.True(IsOffending("X.cs", "    v.ToString(\"0.0\", System.Globalization.CultureInfo.CurrentCulture)"));
        Assert.True(IsOffending("X.cs", "private static string Fmt(FormattableString s) => s.ToString(CultureInfo.CurrentCulture);"));
        Assert.True(IsOffending("Otro.cs", "Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture)"));
        Assert.False(IsOffending("X.cs", "string.Create(SpanishNumbers.Format, $\"{v:0} dB\")"));
        Assert.False(IsOffending("X.cs", "$\"Elegir {label.ToLower(CultureInfo.CurrentCulture)}\""));
        Assert.False(IsOffending("X.cs", "new HashSet<string>(names, StringComparer.CurrentCultureIgnoreCase)"));
        Assert.False(IsOffending("X.cs", "// CultureInfo.CurrentCulture daría «0.5»"));
    }

    [Fact]
    public void LaVigilanciaLeeLosFuentesDeLaApp()
    {
        // Si la ruta cambiara y no leyera nada, la vigilancia pasaría siempre.
        string analysis = Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Views", "Analysis", "AnalysisUi.cs");
        Assert.True(File.Exists(analysis));
        Assert.Contains("SpanishNumbers.Format", File.ReadAllText(analysis), StringComparison.Ordinal);
    }

    private static bool IsOffending(string fileName, string rawLine)
    {
        string line = rawLine.Trim();
        if (line.StartsWith("//", StringComparison.Ordinal)
            || !line.Contains("CultureInfo.CurrentCulture", StringComparison.Ordinal))
        {
            return false;
        }

        return !AllowedUses.Any(u => line.Contains(u, StringComparison.Ordinal))
            && !KnownPending.Any(p => p.File == fileName && line.Contains(p.Line, StringComparison.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
