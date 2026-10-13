namespace Rodaje.App.Tests.Support;

/// <summary>
/// La app publica con <c>InvariantGlobalization</c>: pedir una cultura por nombre lanza al crear la vista y la app no abre,
/// aunque aquí (con culturas) todo pase. Se vigila en el código fuente de todo lo que corre dentro de la app (<c>src</c>)
/// y de los módulos, que publican igual: la tarea 228 encontró dos casos fuera de <c>Rodaje.App</c> (las mayúsculas de los
/// títulos y los «GB» de los avisos de IA). La tarea 231 añadió los otros dos fallos silenciosos del modo invariante:
/// <c>Normalize</c> no separa las tildes («Música» daba el identificador «m-sica») y la cultura actual ordena por código
/// («Árbol» y «Ñandú» detrás de «Zeta»); para eso están <c>SpanishText.Fold</c> y <c>SpanishText.Comparer</c>.
/// </summary>
public class InvariantGlobalizationTests
{
    [Fact]
    public void NadaDeLoQueCorreEnLaApp_PideCulturasPorNombre()
    {
        string root = RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "src", "Rodaje.Core", "Text", "TextCasing.cs")), "la vigilancia no lee el núcleo");
        Assert.Empty(Find(root, "*.cs", ["GetCultureInfo(", "new CultureInfo(", "CreateSpecificCulture("]));
    }

    /// <summary>
    /// Sin ICU, <c>Normalize(NormalizationForm.FormD)</c> devuelve el texto igual y las comparaciones con la cultura actual
    /// son por código: no lanzan, pero los identificadores pierden las letras con tilde y las listas ponen «Árbol» al final.
    /// </summary>
    [Fact]
    public void NadaDeLoQueCorreEnLaApp_DependeDeIcuParaTildesUOrden()
    {
        Assert.Empty(Find(RepositoryRoot(), "*.cs", ["NormalizationForm.", "StringComparer.CurrentCulture", "StringComparison.CurrentCulture"]));
    }

    /// <summary>
    /// <c>DisplayMemberPath</c> y <c>{Binding}</c> enlazan por reflexión (<c>ICustomPropertyProvider</c>), que con Native AOT
    /// no ve las propiedades de un tipo de C#: el desplegable se llena de elementos en blanco. La app usa <c>x:Bind</c> o
    /// listas de textos.
    /// </summary>
    [Fact]
    public void LaApp_NoEnlazaPorReflexion()
    {
        string root = RepositoryRoot();
        Assert.Empty(Find(root, "*.cs", ["DisplayMemberPath"]).Concat(Find(root, "*.xaml", ["DisplayMemberPath", "{Binding"])));
    }

    private static List<string> Find(string root, string pattern, string[] forbidden)
    {
        var found = new List<string>();
        IEnumerable<string> files = new[] { "src", "modules" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), pattern, SearchOption.AllDirectories));
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

        return found;
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
