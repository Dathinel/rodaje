using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;

namespace Rodaje.App.Tests.Settings;

/// <summary>
/// <c>ajustes.json</c>, <c>atajos.json</c> y la cola de exportación dañados, enormes o sin permiso de lectura: la app sigue
/// con los valores de fábrica y un aviso, nunca se cierra. Mutaciones con semilla fija (el fallo dice cuál repetir).
/// </summary>
public class BrokenAppFilesTests
{
    private const int Seed = 94_400_000;

    private static ShortcutRegistry Catalog()
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        return registry;
    }

    [Fact]
    public void MutatedSettings_LoadWithDefaultsOrAWarning()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var original = new SettingsStore(path);
        original.Current.LastMediaFolder = @"D:\Videos";
        original.Current.AddRecentProject(@"C:\a.rodaje");
        original.Current.InspectorSections["color"] = true;
        original.Save();
        string seed = File.ReadAllText(path);

        for (int n = Seed; n < Seed + 400; n++)
        {
            File.WriteAllText(path, BrokenJson.Mutate(seed, new Random(n)));
            var store = new SettingsStore(path);
            try
            {
                _ = store.Load();
                AppSettings s = store.Current;
                Assert.InRange(s.LibraryWidth, 220, 900);
                Assert.InRange(s.InspectorWidth, 240, 900);
                Assert.True(Enum.IsDefined(s.ShortcutPreset));
                store.Save();
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"Semilla {n}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    [Fact]
    public void MutatedShortcuts_LoadWithDefaultsOrAWarning()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        ShortcutRegistry registry = Catalog();
        registry.Rebind(ActionIds.Split, KeyChord.Parse("X"), replacing: KeyChord.Parse("S"));
        new ShortcutStore(path).Save(registry);
        string seed = File.ReadAllText(path);

        for (int n = Seed; n < Seed + 400; n++)
        {
            File.WriteAllText(path, BrokenJson.Mutate(seed, new Random(n)));
            try
            {
                _ = new ShortcutStore(path).Load(Catalog());
                _ = ShortcutStore.Import(Catalog(), path);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Semilla {n}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    /// <summary>
    /// Sin permiso de lectura, <see cref="SettingsStore.Load"/> y <see cref="ShortcutStore.Load"/> dejaban salir
    /// <see cref="UnauthorizedAccessException"/>: la app se cerraba al arrancar (defecto hallado en la tarea 94).
    /// </summary>
    [Fact]
    public void UnreadableFiles_GiveAWarningInsteadOfClosing()
    {
        using var dir = new TempDirectory();
        string settingsPath = dir.File(SettingsStore.FileName);
        string shortcutsPath = dir.File(ShortcutStore.FileName);
        File.WriteAllText(settingsPath, "{}");
        File.WriteAllText(shortcutsPath, "{}");
        DenyRead(settingsPath);
        DenyRead(shortcutsPath);
        try
        {
            var settings = new SettingsStore(settingsPath);
            string? warning = settings.Load();
            Assert.NotNull(warning);
            Assert.Contains("ajustes", warning, StringComparison.Ordinal);
            Assert.Equal(AppSettings.DefaultLibraryWidth, settings.Current.LibraryWidth);

            string? shortcutsWarning = new ShortcutStore(shortcutsPath).Load(Catalog());
            Assert.NotNull(shortcutsWarning);
            Assert.Contains("atajos", shortcutsWarning, StringComparison.Ordinal);
        }
        finally
        {
            AllowRead(settingsPath);
            AllowRead(shortcutsPath);
        }
    }

    /// <summary>
    /// Una entrada nula, o sin proyecto, secuencia, salida o ajustes, lanzaba NullReference o ArgumentNull fuera del
    /// <c>catch</c> de <see cref="ExportQueueStore.Load"/> (defecto hallado en la tarea 94). Ahora se salta.
    /// </summary>
    [Theory]
    [InlineData("""{"version":1,"exports":[null]}""")]
    [InlineData("""{"version":1,"exports":[{"projectJson":null,"sequenceId":"s","outputPath":"a.mp4"}]}""")]
    [InlineData("""{"version":1,"exports":[{"sequenceId":null,"outputPath":"a.mp4"}]}""")]
    [InlineData("""{"version":1,"exports":[{"outputPath":null}]}""")]
    [InlineData("""{"version":1,"exports":[{"settings":null}]}""")]
    [InlineData("""{"version":1,"exports":null}""")]
    [InlineData("""{"version":1,"exports":[{"projectJson":"{\"schemaVersion\":99}","sequenceId":"s","outputPath":"a.mp4"}]}""")]
    public void BrokenExportQueue_IsEmptyInsteadOfThrowing(string json)
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(dir.File(ExportQueueStore.FileName));
        File.WriteAllText(store.Path, json);

        Assert.Empty(store.Load());
    }

    private static void DenyRead(string path) => ChangeRead(path, add: true);

    private static void AllowRead(string path) => ChangeRead(path, add: false);

    private static void ChangeRead(string path, bool add)
    {
        var info = new FileInfo(path);
        FileSecurity security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        if (add)
        {
            security.AddAccessRule(rule);
        }
        else
        {
            security.RemoveAccessRule(rule);
        }

        info.SetAccessControl(security);
    }
}

/// <summary>Mutaciones de JSON para los archivos de la app: tipos cambiados, números enormes, cadenas largas y bytes rotos.</summary>
internal static class BrokenJson
{
    private static readonly JsonNode?[] Swaps = [null, true, 0, -1, 1e308, -1e308, "", "x", new JsonArray(), new JsonObject(), new JsonArray(null, 1, "a"), 99_999];

    private static readonly string[] Huge = ["1e400", "-1e400", "99999999999999999999999", "-9223372036854775809", "2147483648"];

    public static string Mutate(string seed, Random rng)
    {
        JsonNode root = JsonNode.Parse(seed)!;
        var hugeUsed = new List<string>();
        for (int e = 0; e < 1 + rng.Next(3); e++)
        {
            var slots = new List<(JsonNode Parent, string? Key, int Index)>();
            Collect(root, slots);
            if (slots.Count == 0)
            {
                break;
            }

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

            if (key is not null)
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

    private static void Collect(JsonNode? node, List<(JsonNode Parent, string? Key, int Index)> slots)
    {
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj.ToList())
            {
                slots.Add((obj, pair.Key, -1));
                Collect(pair.Value, slots);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                slots.Add((array, null, i));
                Collect(array[i], slots);
            }
        }
    }
}
