using System.Reflection;
using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// <c>LibraryFolder.IdReads</c> y <c>LibraryOrganization.NameFolds</c> eran campos <c>[ThreadStatic]</c> que la app sumaba
/// en cada lectura de un id y en cada nombre plegado solo para que las pruebas contaran operaciones. Ahora la app solo
/// declara métodos parciales sin cuerpo (el compilador quita sus llamadas) y los contadores viven en las pruebas
/// (<c>LibraryOperationCounters.cs</c>).
/// </summary>
public sealed class LibraryCountersOutOfProductionTests
{
    /// <summary>
    /// Los únicos campos <c>[ThreadStatic]</c> o contadores estáticos (<c>int</c> o <c>long</c> que se pueden escribir) de la
    /// biblioteca son los tres de <c>LibraryOperationCounters.cs</c>.
    /// Este proyecto compila los mismos fuentes que la app, así que un contador que volviera a la app aparece aquí como un
    /// campo más, esté en el archivo que esté (antes se leía el texto de dos archivos). Que la app declare los métodos
    /// parciales (y no los campos con estos nombres) lo garantiza el compilador: sin la declaración, el cuerpo de
    /// <c>LibraryOperationCounters.cs</c> no compila (CS0759), y un campo de la app con el mismo nombre choca con el de aquí.
    /// </summary>
    [Fact]
    public void LosUnicosContadoresDeLaBiblioteca_SonLosDeLasPruebas()
    {
        string[] fields = [.. typeof(LibraryFolder).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(LibraryFolder).Namespace)
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.Name.StartsWith('<'))
            .Where(f => f.IsDefined(typeof(ThreadStaticAttribute), inherit: false)
                || (!f.IsInitOnly && !f.IsLiteral && (f.FieldType == typeof(long) || f.FieldType == typeof(int))))
            .Select(f => $"{f.DeclaringType!.Name}.{f.Name}")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(["LibraryFolder.IdReads", "LibraryOrganization.NameFolds", "Subclip.KeyReads"], fields);
    }

    [Fact]
    public void LaCosturaSigueContando()
    {
        LibraryFolder.IdReads = 0;
        _ = new LibraryFolder { Id = "a" }.Id;
        Assert.Equal(1, LibraryFolder.IdReads);

        long before = LibraryOrganization.NameFolds;
        LibraryOrganization.From([new LibraryFolder { Id = "a", Name = "X" }, new LibraryFolder { Id = "b", Name = "x" }], null, null);
        Assert.True(LibraryOrganization.NameFolds > before);

        var subclip = new Subclip("s", "m", "x", Rodaje.Core.Timing.Time.Zero, Rodaje.Core.Timing.Time.FromSeconds(1));
        Subclip.KeyReads = 0;
        _ = subclip.Id;
        _ = subclip.MediaId;
        Assert.Equal(2, Subclip.KeyReads);
    }
}
