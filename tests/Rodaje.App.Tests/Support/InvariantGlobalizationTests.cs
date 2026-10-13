namespace Rodaje.App.Tests.Support;

/// <summary>
/// La app publica con <c>InvariantGlobalization</c>: pedir una cultura por nombre lanza al crear la vista y la app no abre,
/// aunque aquí (con culturas) todo pase. Se vigila en el código fuente de todo lo que corre dentro de la app (<c>src</c>)
/// y de los módulos, que publican igual: la tarea 228 encontró dos casos fuera de <c>Rodaje.App</c> (las mayúsculas de los
/// títulos y los «GB» de los avisos de IA).
/// </summary>
public class InvariantGlobalizationTests
{
    [Fact]
    public void NadaDeLoQueCorreEnLaApp_PideCulturasPorNombre()
    {
        string root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "src", "Rodaje.Core", "Text", "TextCasing.cs")), "la vigilancia no lee el núcleo");
        string[] forbidden = ["GetCultureInfo(", "new CultureInfo(", "CreateSpecificCulture("];
        var found = new List<string>();
        IEnumerable<string> files = new[] { "src", "modules" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories));
        foreach (string file in files)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            found.AddRange(forbidden.Where(f => text.Contains(f, StringComparison.Ordinal)).Select(f => $"{Path.GetRelativePath(root, file)}: {f}"));
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
