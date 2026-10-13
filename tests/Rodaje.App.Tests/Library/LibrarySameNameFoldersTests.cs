using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Carpetas con el mismo nombre al lado (también con otras mayúsculas o tildes, que cuentan como el mismo): buscarles un
/// nombre libre es lineal. Se cuentan los nombres plegados (<see cref="LibraryOrganization.NameFolds"/>), no segundos, así
/// que la prueba no depende de la carga de la máquina.
/// </summary>
public sealed class LibrarySameNameFoldersTests
{
    /// <summary>Plegar un nombre por carpeta y por cada número probado: con un recorrido lineal sobran 8 por carpeta.</summary>
    private const int FoldsPerFolder = 8;

    [Theory]
    [InlineData(1_000)]
    [InlineData(8_000)]
    public void IdenticalNames_LoadWithLinearFolds(int count)
    {
        LibraryFolder[] folders = [.. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = "Tomas" })];

        LibraryOrganization org = CountFolds(() => LibraryOrganization.From(folders, null, null), out long folds);

        AssertDistinctNames(org, count);
        Assert.True(folds <= FoldsPerFolder * count, $"{folds} nombres plegados para {count} carpetas");
    }

    /// <summary>
    /// «Tomas», «tomas», «TOMÁS»… son el mismo nombre al comparar, pero el número siguiente se recordaba por la forma
    /// exacta: cada variante volvía a probar desde el 2 todos los números ya dados (cuadrático).
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(8_000)]
    public void NamesThatDifferOnlyInCaseOrAccents_LoadWithLinearFolds(int count)
    {
        LibraryFolder[] folders = [.. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = Variant("tomas de playas", i) })];

        LibraryOrganization org = CountFolds(() => LibraryOrganization.From(folders, null, null), out long folds);

        AssertDistinctNames(org, count);
        Assert.True(folds <= FoldsPerFolder * count, $"{folds} nombres plegados para {count} carpetas");
    }

    /// <summary>Crear una carpeta más junto a miles de «Carpeta nueva N» recorría todas las carpetas por cada número probado.</summary>
    [Fact]
    public void CreatingNextToThousandsOfDefaultNames_IsLinear()
    {
        const int count = 4_000;
        LibraryFolder[] folders = [.. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = i == 0 ? LibraryOrganization.DefaultFolderName : $"{LibraryOrganization.DefaultFolderName} {i + 1}" })];
        LibraryOrganization org = LibraryOrganization.From(folders, null, null);

        LibraryFolder created = CountFolds(() => org.CreateFolder(null), out long folds);

        Assert.Equal($"{LibraryOrganization.DefaultFolderName} {count + 1}", created.Name);
        Assert.True(folds <= FoldsPerFolder * count, $"{folds} nombres plegados para {count} carpetas");
    }

    /// <summary>Borrar una carpeta con miles de subcarpetas iguales: cada una que subía buscaba su nombre libre recorriéndolo todo.</summary>
    [Fact]
    public void DeletingAFolderWithThousandsOfSameNamedChildren_IsLinear()
    {
        const int count = 2_000;
        LibraryFolder[] folders =
        [
            new() { Id = "padre", Name = "Tomas" },
            .. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = i == 0 ? "Tomas" : $"Tomas {i + 1}", ParentId = "padre" }),
        ];
        LibraryOrganization org = LibraryOrganization.From(folders, null, null);

        CountFolds(() => org.DeleteFolder("padre"), out long folds);

        AssertDistinctNames(org, count);
        Assert.All(org.Folders, f => Assert.Null(f.ParentId));
        Assert.True(folds <= FoldsPerFolder * count, $"{folds} nombres plegados para {count} carpetas");
    }

    /// <summary>Igual que antes del arreglo, en orden: la primera conserva el nombre y las demás reciben el siguiente número libre.</summary>
    [Fact]
    public void Variants_GetTheNextFreeNumberInOrder()
    {
        LibraryFolder[] folders =
        [
            new() { Id = "a", Name = "Tomas" },
            new() { Id = "b", Name = "tomas" },
            new() { Id = "c", Name = "TOMÁS" },
            new() { Id = "d", Name = "Tomas" },
            new() { Id = "e", Name = "Tomas 3" },
        ];

        LibraryOrganization org = LibraryOrganization.From(folders, null, null);

        Assert.Equal(["Tomas 2", "tomas 4", "TOMÁS 5", "Tomas", "Tomas 3"], org.Folders.Select(f => f.Name));
    }

    [Fact]
    public void DeletingAFolder_NumbersTheChildrenThatClashWithItsSiblings()
    {
        LibraryFolder[] folders =
        [
            new() { Id = "otra", Name = "Tomas" },
            new() { Id = "padre", Name = "Padre" },
            new() { Id = "x", Name = "tomas", ParentId = "padre" },
            new() { Id = "y", Name = "Padre", ParentId = "padre" },
            new() { Id = "z", Name = "Tomas 2", ParentId = "padre" },
        ];
        LibraryOrganization org = LibraryOrganization.From(folders, null, null);

        Assert.True(org.DeleteFolder("padre"));

        // La borrada aún cuenta mientras suben sus hijas, como antes: «Padre» pasa a «Padre 2».
        Assert.Equal(["Tomas", "tomas 2", "Padre 2", "Tomas 2 2"], org.Folders.Select(f => f.Name));
    }

    private static T CountFolds<T>(Func<T> action, out long folds)
    {
        long before = LibraryOrganization.NameFolds;
        T result = action();
        folds = LibraryOrganization.NameFolds - before;
        return result;
    }

    private static void AssertDistinctNames(LibraryOrganization org, int count) =>
        Assert.Equal(count, org.Folders.Select(f => (f.ParentId, SearchText.Fold(f.Name))).Distinct().Count());

    /// <summary>La variante <paramref name="i"/> del nombre: cada bit pone en mayúscula una letra (13 letras: 8192 variantes).</summary>
    private static string Variant(string name, int i)
    {
        char[] chars = name.ToCharArray();
        int bit = 0;
        for (int c = 0; c < chars.Length; c++)
        {
            if (!char.IsLetter(chars[c]))
            {
                continue;
            }

            if ((i >> bit & 1) == 1)
            {
                chars[c] = char.ToUpperInvariant(chars[c]);
            }

            bit++;
        }

        return new string(chars);
    }
}
