using Rodaje.App.Views.Library;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Buscar un subclip por id. La vista de la biblioteca busca cada subclip que tiene en pantalla al rehacer la lista (para
/// soltar los que ya no están), y <see cref="LibraryOrganization.FindSubclip"/> recorría la lista entera por cada uno:
/// cuadrático con miles de subclips. Ahora va por un índice que se mantiene al crear, renombrar, borrar, podar y cargar.
/// </summary>
public sealed class LibrarySubclipLookupTests
{
    private static LibraryOrganization WithSubclips(int count)
    {
        var org = new LibraryOrganization();
        var subclips = new List<Subclip?>(count);
        for (int i = 0; i < count; i++)
        {
            subclips.Add(new Subclip($"s{i}", $"m{i % 7}", "x", Time.Zero, Time.FromSeconds(1)));
        }

        org.ImportMarks(new LibraryMarks { Subclips = subclips });
        return org;
    }

    /// <summary>
    /// Lo que hace la vista al rehacer la lista: buscar cada subclip. Se cuentan los bytes y el tiempo sin pausas del
    /// recolector (<see cref="WorkGrowth"/>); con la búsqueda lineal, de 2 000 a 16 000 se multiplicaban por unas 64 veces.
    /// </summary>
    [Fact]
    public void FindingEverySubclip_GrowsLinearly() =>
        WorkGrowth.AssertLinear(count => (Org: WithSubclips(count), Ids: Enumerable.Range(0, count).Select(i => $"s{i}").ToArray()),
            input =>
            {
                foreach (string id in input.Ids)
                {
                    if (input.Org.FindSubclip(id) is null)
                    {
                        Assert.Fail($"Falta {id}");
                    }
                }
            }, 2_000, 16_000, "Buscar cada subclip");

    [Fact]
    public void Lookup_FollowsAddRenameRemoveAndPrune()
    {
        LibraryOrganization org = WithSubclips(3);
        Subclip added = org.AddSubclip("m1", "Nuevo", Time.Zero, Time.FromSeconds(2));

        Assert.Same(added, org.FindSubclip(added.Id));
        Assert.Null(org.FindSubclip(null));
        Assert.Null(org.FindSubclip("no-existe"));

        Assert.True(org.RenameSubclip("s1", "Otro nombre"));
        Assert.Equal("Otro nombre", org.FindSubclip("s1")!.Name);
        Assert.Same(org.FindSubclip("s1"), org.Subclips.Single(s => s.Id == "s1"));
        Assert.False(org.RenameSubclip("no-existe", "Nada"));

        Assert.True(org.RemoveSubclip("s0"));
        Assert.False(org.RemoveSubclip("s0"));
        Assert.Null(org.FindSubclip("s0"));
        Assert.DoesNotContain(org.Subclips, s => s.Id == "s0");

        // s1 es de m1 y s2 de m2: al podar sin m2 se va s2 y quedan s1 y el nuevo.
        org.Prune(["m1"]);
        Assert.Null(org.FindSubclip("s2"));
        Assert.Equal(["s1", added.Id], org.Subclips.Select(s => s.Id));
        Assert.All(org.Subclips, s => Assert.Same(s, org.FindSubclip(s.Id)));
    }

    /// <summary>Al cargar, el primero de cada id manda y los repetidos (o los ya presentes) se ignoran.</summary>
    [Fact]
    public void Import_KeepsTheFirstOfEachId()
    {
        var org = new LibraryOrganization();
        org.ImportMarks(new LibraryMarks
        {
            Subclips =
            [
                new Subclip("a", "m1", "Primero", Time.Zero, Time.FromSeconds(1)),
                new Subclip("a", "m1", "Segundo", Time.Zero, Time.FromSeconds(1)),
                null,
                new Subclip("b", "m1", "Otro", Time.Zero, Time.FromSeconds(1)),
            ],
        });
        org.ImportMarks(new LibraryMarks { Subclips = [new Subclip("b", "m1", "Tarde", Time.Zero, Time.FromSeconds(1))] });

        Assert.Equal(["a", "b"], org.Subclips.Select(s => s.Id));
        Assert.Equal("Primero", org.FindSubclip("a")!.Name);
        Assert.Equal("Otro", org.FindSubclip("b")!.Name);
    }
}
