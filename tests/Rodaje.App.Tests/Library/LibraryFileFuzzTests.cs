using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Library;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Archivos de organización de la biblioteca dañados o enormes: <see cref="LibraryOrganizationStore.Load(string)"/> deja una
/// organización usable (o vacía) y rápida, nunca lanza ni se cuelga. Mutaciones con semilla fija: el mensaje de fallo dice
/// cuál repetir.
/// </summary>
public sealed class LibraryFileFuzzTests : IDisposable
{
    private const int Seed = 94_300_000;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-fuzz94-biblioteca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SeedFile()
    {
        var store = new LibraryOrganizationStore(_dir);
        var org = new LibraryOrganization();
        LibraryFolder takes = org.CreateFolder(null, "Tomas");
        LibraryFolder good = org.CreateFolder(takes.Id, "Buenas");
        org.CreateFolder(good.Id, "Mejores");
        org.MoveMedia(["m1", "m2"], good.Id);
        org.SetMark("m3", MediaMark.Favorite);
        org.SetMark("m4", MediaMark.Rejected);
        org.SetRating("m1", 4);
        org.SetTags("m1", ["exterior", "noche"]);
        org.AddSubclip("m1", "Saludo", Time.FromSeconds(1), Time.FromSeconds(3));
        org.SaveCollection(new SmartBin("", "Buenas", [new SmartRule(SmartField.MinRating, "3"), new SmartRule(SmartField.FrameRate, "29.97")]));
        org.AddMulticam("Cámaras", [new MulticamAngleEntry("m1", Time.Zero), new MulticamAngleEntry("m2", Time.FromSeconds(0.5))]);
        store.Save("semilla", org);
        return File.ReadAllText(store.PathFor("semilla"));
    }

    /// <summary>
    /// Mil carpetas con el mismo nombre, o encadenadas una dentro de otra, tardaban decenas de segundos en abrir (buscar la
    /// carpeta y el nombre libre recorría la lista entera por cada una: cúbico) y una cadena de miles desbordaba la pila al
    /// armar el menú «Mover a». Defecto hallado por el fuzz de la tarea 94. En la CI se mira que la carga crezca como lo
    /// lineal (<see cref="Growth"/>), en memoria: lo mismo que <see cref="LibraryOrganizationStore.Load(string)"/> sin
    /// el archivo. El tope de 10 s de reloj con 20 000 carpetas desde el disco (33,7 s en Windows con el runner cargado,
    /// 0,3 s en Linux) queda en la gemela TiempoReal.
    /// </summary>
    [Theory]
    [InlineData("iguales")]
    [InlineData("cadena")]
    [InlineData("ciclo")]
    public void ThousandsOfFolders_LoadInLinearTime(string shape) =>
        Growth.AssertLinear(count => (Json: FoldersJson(shape, count), Count: count),
            input => CheckFolders(Parse(input.Json), shape, input.Count), 1_000, 8_000, $"Carpetas «{shape}»");

    /// <summary>Lo mismo desde el archivo, sin medir el tiempo.</summary>
    [Theory]
    [InlineData("iguales")]
    [InlineData("cadena")]
    [InlineData("ciclo")]
    public void ThousandsOfFolders_LoadFromTheFile(string shape) => CheckFolders(LoadFile(FoldersJson(shape, 2_000)), shape, 2_000);

    [Theory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData("iguales")]
    [InlineData("cadena")]
    [InlineData("ciclo")]
    public void ThousandsOfFolders_LoadQuickly(string shape)
    {
        string json = FoldersJson(shape, 20_000);
        var watch = Stopwatch.StartNew();
        CheckFolders(LoadFile(json), shape, 20_000);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary><paramref name="count"/> carpetas con la forma dada, como las guarda el almacén.</summary>
    private static string FoldersJson(string shape, int count)
    {
        var folders = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            string? parent = shape switch
            {
                "cadena" => i == 0 ? null : $"f{i - 1}",
                "ciclo" => $"f{(i + 1) % count}",
                _ => null,
            };
            folders.Add(new JsonObject { ["id"] = $"f{i}", ["name"] = shape == "iguales" ? "Tomas" : $"n{i}", ["parent"] = parent });
        }

        return new JsonObject { ["version"] = 2, ["folders"] = folders }.ToJsonString();
    }

    /// <summary>Lo que hace <see cref="LibraryOrganizationStore.Load(string)"/> con el texto ya leído.</summary>
    private static LibraryOrganization Parse(string json)
    {
        LibraryFile file = JsonSerializer.Deserialize(json, LibraryJsonContext.Default.LibraryFile)!;
        LibraryOrganization organization = LibraryOrganization.From(
            file.Folders?.Select(f => new LibraryFolder { Id = f?.Id ?? "", Name = f?.Name ?? "", ParentId = f?.Parent }),
            file.Media,
            file.Favorites);
        organization.ImportMarks(file.Marks);
        return organization;
    }

    private LibraryOrganization LoadFile(string json)
    {
        var store = new LibraryOrganizationStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("p"), json);
        return store.Load("p");
    }

    private static void CheckFolders(LibraryOrganization org, string shape, int count)
    {
        IReadOnlyList<(LibraryFolder Folder, int Depth)> tree = org.Tree();
        Assert.Equal(count, org.Folders.Count);
        Assert.Equal(count, tree.Count);
        Assert.Equal(count, org.Folders.Select(f => (f.ParentId, f.Name)).Distinct().Count());
        if (shape == "ciclo")
        {
            Assert.Single(org.Folders, f => f.ParentId is null);
        }
    }

    /// <summary>Un ciclo: la primera de sus carpetas sube a la raíz y las que cuelgan de él se quedan donde estaban.</summary>
    [Fact]
    public void Cycle_OnlyTheFirstFolderMovesToTheRoot()
    {
        LibraryFolder[] folders =
        [
            new() { Id = "x", Name = "Cuelga", ParentId = "b" },
            new() { Id = "a", Name = "Uno", ParentId = "b" },
            new() { Id = "b", Name = "Dos", ParentId = "c" },
            new() { Id = "c", Name = "Tres", ParentId = "a" },
        ];

        LibraryOrganization org = LibraryOrganization.From(folders, null, null);

        Assert.Equal(["b", null, "c", "a"], org.Folders.Select(f => f.ParentId));
        Assert.Equal(4, org.Tree().Count);
    }

    /// <summary>Una regla de colección con <c>"value": null</c> rompía al describirla; ahora se descarta al leer.</summary>
    [Fact]
    public void CollectionRuleWithNullValue_IsDropped()
    {
        var store = new LibraryOrganizationStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("p"), """
            {"version":2,"folders":[],"media":{},"favorites":[],"marks":{"collections":[
              {"id":"c","name":"Rotas","rules":[{"field":"frameRate","value":null},{"field":"tag","value":"noche"},null],"matchAll":true}]}}
            """);

        LibraryOrganization org = store.Load("p");

        SmartBin collection = Assert.Single(org.Collections);
        SmartRule rule = Assert.Single(collection.Rules);
        Assert.Equal("Etiqueta: noche", rule.Describe());
    }

    /// <summary>
    /// Subclips con ids repetidos: se queda uno por id, en memoria. Se cuentan los bytes reservados y el tiempo sin las
    /// pausas del recolector (<see cref="WorkGrowth"/>): por reloj, en la CI de Windows dio ×174 y ×230 de 4 000 a 32 000 con
    /// una carga lineal, porque solo la corrida grande despierta al recolector.
    /// </summary>
    [Fact]
    public void ManyRepeatedEntries_LoadInLinearTime() =>
        WorkGrowth.AssertLinear(count => (Json: SubclipsJson(count), Count: count),
            input => Assert.Equal(input.Count / 2, Parse(input.Json).Subclips.Count), 4_000, 32_000, "Subclips repetidos");

    // 75,9 s desde el disco en la CI de Windows con el runner cargado, contra 1 s en Linux: el tope de reloj es TiempoReal.
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void ManyRepeatedEntries_LoadQuickly()
    {
        string json = SubclipsJson(100_000);
        var watch = Stopwatch.StartNew();
        LibraryOrganization org = LoadFile(json);
        watch.Stop();
        Assert.Equal(50_000, org.Subclips.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Tardó {watch.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary><paramref name="count"/> subclips con la mitad de ids distintos.</summary>
    private static string SubclipsJson(int count)
    {
        var subclips = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            subclips.Add(new JsonObject { ["id"] = $"s{i % (count / 2)}", ["mediaId"] = "m1", ["name"] = "x", ["in"] = 0L, ["out"] = 705_600_000L });
        }

        return new JsonObject { ["version"] = 2, ["marks"] = new JsonObject { ["subclips"] = subclips } }.ToJsonString();
    }

    [Fact]
    public void MutatedFiles_NeverThrow()
    {
        string seed = SeedFile();
        var watch = Stopwatch.StartNew();
        int cases = 0;
        for (int n = Seed; n < Seed + 3000 && (watch.Elapsed < Budget || cases < 300); n++, cases++)
        {
            var rng = new Random(n);
            string text = Mutate(seed, rng);
            var store = new LibraryOrganizationStore(_dir);
            File.WriteAllText(store.PathFor("k"), text);
            try
            {
                LibraryOrganization org = store.Load("k");
                _ = org.Tree();
                foreach (LibraryFolder folder in org.Folders)
                {
                    _ = org.Children(folder.Id);
                }

                foreach (SmartRule rule in org.Collections.SelectMany(c => c.Rules))
                {
                    _ = rule.Describe();
                }

                _ = org.AllTags();
                store.Save("k2", org);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Semilla {n}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n{(text.Length > 400 ? text[..400] : text)}");
            }
        }
    }

    private static readonly JsonNode?[] Swaps = [null, true, 0, -1, 1e308, "", "x", new JsonArray(), new JsonObject(), new JsonArray(null, 1, "a"), 99_999];

    private static readonly string[] Huge = ["1e400", "99999999999999999999999", "-9223372036854775809", "2147483648"];

    private static string Mutate(string seed, Random rng)
    {
        JsonNode root = JsonNode.Parse(seed)!;
        var hugeUsed = new List<string>();
        for (int e = 0; e < 1 + rng.Next(3); e++)
        {
            var slots = new List<(JsonNode Parent, string? Key, int Index)>();
            void Walk(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (KeyValuePair<string, JsonNode?> pair in obj.ToList())
                    {
                        slots.Add((obj, pair.Key, -1));
                        Walk(pair.Value);
                    }
                }
                else if (node is JsonArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        slots.Add((array, null, i));
                        Walk(array[i]);
                    }
                }
            }

            Walk(root);
            (JsonNode parent, string? key, int index) = slots[rng.Next(slots.Count)];
            JsonNode? value;
            switch (rng.Next(5))
            {
                case 0:
                    value = $"§{hugeUsed.Count}§";
                    hugeUsed.Add(Huge[rng.Next(Huge.Length)]);
                    break;
                case 1:
                    value = new string('ñ', rng.Next(20) == 0 ? 10 * 1024 * 1024 : 5000);
                    break;
                default:
                    value = Swaps[rng.Next(Swaps.Length)]?.DeepClone();
                    break;
            }

            if (rng.Next(6) == 0 && parent is JsonArray list)
            {
                for (int r = 0; r < 2000; r++)
                {
                    list.Add(list[index]?.DeepClone());
                }
            }
            else if (key is not null)
            {
                parent[key] = value;
            }
            else
            {
                parent[index] = value;
            }
        }

        string text = root.ToJsonString();
        for (int h = 0; h < hugeUsed.Count; h++)
        {
            text = text.Replace($"\"§{h}§\"", hugeUsed[h], StringComparison.Ordinal);
        }

        if (rng.Next(5) == 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int at = rng.Next(bytes.Length);
            bytes = rng.Next(2) == 0 ? bytes[..at] : [.. bytes[..at], (byte)rng.Next(256), .. bytes[(at + 1)..]];
            text = Encoding.UTF8.GetString(bytes);
        }

        return text;
    }
}
