namespace Rodaje.App.Tests.Support;

/// <summary>
/// La app publica con <c>InvariantGlobalization</c>: pedir una cultura por nombre lanza al crear la vista y la app no abre,
/// aunque aquí (con culturas) todo pase. Se vigila en el código fuente.
/// </summary>
public class InvariantGlobalizationTests
{
    [Fact]
    public void LaApp_NoPideCulturasPorNombre()
    {
        string root = RepositoryRoot();
        string[] forbidden = ["GetCultureInfo(", "new CultureInfo(", "CreateSpecificCulture("];
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "Rodaje.App"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            found.AddRange(forbidden.Where(f => text.Contains(f, StringComparison.Ordinal)).Select(f => $"{Path.GetFileName(file)}: {f}"));
        }

        Assert.Empty(found);
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
