using System.Text.RegularExpressions;
using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// <c>LibraryFolder.IdReads</c> y <c>LibraryOrganization.NameFolds</c> eran campos <c>[ThreadStatic]</c> que la app sumaba
/// en cada lectura de un id y en cada nombre plegado solo para que las pruebas contaran operaciones. Ahora la app solo
/// declara métodos parciales sin cuerpo (el compilador quita sus llamadas) y los contadores viven en las pruebas
/// (<c>LibraryOperationCounters.cs</c>).
/// </summary>
public sealed partial class LibraryCountersOutOfProductionTests
{
    [Fact]
    public void ElFuenteDeLaApp_NoTieneContadoresDePrueba()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Rodaje.App", "Views", "Library", "Logic", "LibraryOrganization.cs"));

        Assert.DoesNotContain("ThreadStatic", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(CounterField(), source);
        Assert.Contains("static partial void CountIdRead();", source, StringComparison.Ordinal);
        Assert.Contains("static partial void CountNameFold();", source, StringComparison.Ordinal);

        string marks = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Rodaje.App", "Views", "Library", "Logic", "LibraryOrganization.Marks.cs"));
        Assert.DoesNotContain("ThreadStatic", marks, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyReads", marks, StringComparison.Ordinal);
        Assert.Contains("static partial void CountKeyRead();", marks, StringComparison.Ordinal);
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

    [GeneratedRegex(@"static\s+long\s+(IdReads|NameFolds)")]
    private static partial Regex CounterField();

    private static string RepoRoot()
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
