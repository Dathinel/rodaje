using Rodaje.App.Views.Library;
using Rodaje.Tests.Fuzz;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Desde la tarea 94, de cada ciclo de carpetas de un archivo dañado solo sube a la raíz la primera (en el orden del
/// archivo). Aquí se comprueba con árboles al azar (ciclos de varios tamaños, carpetas que se apuntan a sí mismas,
/// padres que no existen, ids repetidos y ramas que cuelgan de un ciclo) que ninguna carpeta queda fuera del árbol: todas
/// salen en <see cref="LibraryOrganization.Tree"/> y se llega a todas bajando desde la raíz con
/// <see cref="LibraryOrganization.Children"/>; y que solo cambia de padre lo imprescindible (tarea 102).
/// </summary>
public sealed class LibraryFolderCycleTests
{
    private const int Seed = 102_200_000;

    [Fact]
    public void RandomFolderGraphs_KeepEveryFolderReachable()
    {
        FuzzRunner.Run("ciclos de carpetas", Seed, cases: 3000, TimeSpan.FromSeconds(20), minimumCases: 500, (seed, rng) =>
        {
            int pool = 1 + rng.Next(rng.Next(2) == 0 ? 8 : 40);
            int count = rng.Next(pool + 6);
            var folders = new List<LibraryFolder>();
            for (int i = 0; i < count; i++)
            {
                string id = "f" + rng.Next(pool);
                string? parent = rng.Next(10) switch
                {
                    0 => null,
                    1 => id,
                    2 => "falta" + rng.Next(3),
                    3 => "",
                    _ => "f" + rng.Next(pool),
                };
                folders.Add(new LibraryFolder { Id = rng.Next(40) == 0 ? " " : id, Name = rng.Next(4) == 0 ? "" : "n" + rng.Next(3), ParentId = parent });
            }

            Check(folders, $"semilla {seed}");
        });
    }

    /// <summary>Dos ciclos que se tocan por ramas, un ciclo de uno y una cadena larga que acaba en un ciclo.</summary>
    [Fact]
    public void BranchesHangingFromCycles_StayUnderTheCutFolder()
    {
        LibraryFolder[] folders =
        [
            new() { Id = "rama", Name = "Rama", ParentId = "c2" },
            new() { Id = "c1", Name = "C1", ParentId = "c3" },
            new() { Id = "c2", Name = "C2", ParentId = "c1" },
            new() { Id = "c3", Name = "C3", ParentId = "c2" },
            new() { Id = "d1", Name = "D1", ParentId = "d2" },
            new() { Id = "d2", Name = "D2", ParentId = "d1" },
            new() { Id = "sola", Name = "Sola", ParentId = "sola" },
            new() { Id = "hoja", Name = "Hoja", ParentId = "rama" },
            new() { Id = "puente", Name = "Puente", ParentId = "d2" },
        ];

        LibraryOrganization org = Check(folders, "fijo");

        Assert.Equal(["c1", "d1", "sola"], org.Children(null).Select(f => f.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["c1", "c2", "rama", "hoja"], org.PathTo("hoja").Select(f => f.Id));
        Assert.Equal(["d1", "d2", "puente"], org.PathTo("puente").Select(f => f.Id));
    }

    /// <summary>Carga y comprueba; devuelve la organización para lo que quiera mirar cada prueba.</summary>
    private static LibraryOrganization Check(IReadOnlyList<LibraryFolder> input, string what)
    {
        // Lo que se espera: las carpetas válidas (id no vacío, la primera de cada id) con su padre si existe.
        var expected = new List<(string Id, string? Parent)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryFolder folder in input)
        {
            if (!string.IsNullOrWhiteSpace(folder.Id) && ids.Add(folder.Id))
            {
                expected.Add((folder.Id, folder.ParentId));
            }
        }

        expected = [.. expected.Select(e => (e.Id, e.Parent is { } p && ids.Contains(p) ? p : null))];
        Dictionary<string, string?> parentOf = expected.ToDictionary(e => e.Id, e => e.Parent, StringComparer.Ordinal);
        Dictionary<string, int> order = expected.Select((e, i) => (e.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        // Las carpetas de cada ciclo y la primera de cada uno, la única que debe subir a la raíz.
        var cut = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string start, string? _) in expected)
        {
            var path = new List<string>();
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            for (string? at = start; at is not null && !done.Contains(at); at = parentOf[at])
            {
                if (!onPath.Add(at))
                {
                    List<string> cycle = path[path.IndexOf(at)..];
                    cut.Add(cycle.MinBy(id => order[id])!);
                    break;
                }

                path.Add(at);
            }

            done.UnionWith(path);
        }

        // Copias: From no debe tocar lo que se le pasa, pero así la comprobación no depende de ello.
        LibraryOrganization org = LibraryOrganization.From(
            input.Select(f => new LibraryFolder { Id = f.Id, Name = f.Name, ParentId = f.ParentId }).ToList(), null, null);

        Assert.Equal(expected.Select(e => e.Id), org.Folders.Select(f => f.Id));
        foreach (LibraryFolder folder in org.Folders)
        {
            string? want = cut.Contains(folder.Id) ? null : parentOf[folder.Id];
            Assert.True(folder.ParentId == want, $"{what}: «{folder.Id}» quedó en «{folder.ParentId}» y debía quedar en «{want}».");
        }

        // Todas en el árbol del menú «Mover a», una vez cada una, y con la profundidad de su camino desde la raíz.
        IReadOnlyList<(LibraryFolder Folder, int Depth)> tree = org.Tree();
        Assert.True(tree.Count == org.Folders.Count, $"{what}: el árbol tiene {tree.Count} de {org.Folders.Count} carpetas.");
        Assert.Equal(org.Folders.Count, tree.Select(t => t.Folder.Id).Distinct(StringComparer.Ordinal).Count());
        foreach ((LibraryFolder folder, int depth) in tree)
        {
            IReadOnlyList<LibraryFolder> path = org.PathTo(folder.Id);
            Assert.True(path.Count == depth + 1 && path[0].ParentId is null, $"{what}: «{folder.Id}» no cuelga de la raíz.");
        }

        // Y bajando por el panel desde la raíz se llega a todas.
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string?>();
        pending.Push(null);
        while (pending.TryPop(out string? parent))
        {
            foreach (LibraryFolder child in org.Children(parent))
            {
                Assert.True(reached.Add(child.Id), $"{what}: «{child.Id}» sale dos veces.");
                pending.Push(child.Id);
            }
        }

        Assert.True(reached.Count == org.Folders.Count,
            $"{what}: desde la raíz no se llega a {string.Join(", ", org.Folders.Select(f => f.Id).Where(id => !reached.Contains(id)))}.");
        return org;
    }
}
