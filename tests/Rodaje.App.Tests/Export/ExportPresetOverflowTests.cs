using System.Text.Json.Nodes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Un <c>.rodaje-preajuste</c> con un número que desborda <see cref="double"/> (<c>1e400</c>, que se lee como ±∞) en la
/// calidad o en los fotogramas clave se importaba sin quejarse: el preajuste propio quedaba en los ajustes y el siguiente
/// guardado de <c>ajustes.json</c> lanzaba <see cref="ArgumentException"/> (tarea 267). Ahora se rechaza con la ruta.
/// </summary>
public sealed class ExportPresetOverflowTests : IDisposable
{
    private const int Seed = 267_200_000;

    private const string Marker = "@@numero@@";

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ExportSettings Full() => ExportPresets.YouTube1080p.Settings with
    {
        Video = ExportPresets.YouTube1080p.Settings.Video! with { RateControl = RateControl.ConstantQuality(23) },
        Audio = ExportPresets.YouTube1080p.Settings.Audio! with
        {
            RateControl = new RateControl { Mode = RateControlMode.Vbr, Quality = 4 },
            Loudness = new LoudnessTarget(-14, -1),
        },
    };

    [Fact]
    public void NumberThatOverflows_IsRejectedWithItsPath()
    {
        string path = _dir.File("semilla" + ExportPresetLibrary.FileExtension);
        ExportPresetLibrary.WriteFile(new ExportPreset("usuario.mio", "Mío", "", ExportPresetCategory.Web, ExportPresetOrigin.User, Full()), path);
        string seed = File.ReadAllText(path);

        var rng = new Random(Seed);
        foreach (string[] number in SettingsOverflowTests.NumberPaths)
        {
            string literal = rng.Next(2) == 0 ? "1e400" : "-1e400";
            string where = string.Join('.', number);
            JsonNode root = JsonNode.Parse(seed)!;
            JsonNode node = root["settings"]!;
            foreach (string step in number[..^1])
            {
                node = node[step]!;
            }

            node[number[^1]] = Marker;
            File.WriteAllText(path, root.ToJsonString().Replace($"\"{Marker}\"", literal, StringComparison.Ordinal));

            var preferences = new ExportPreferences();
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ExportPresetLibrary.Import(preferences, path));
            Assert.Contains(where, ex.Message, StringComparison.Ordinal);
            Assert.Empty(preferences.UserPresets);
        }
    }

    [Fact]
    public void FiniteNumbers_AreImportedAndSaved()
    {
        string path = _dir.File("bueno" + ExportPresetLibrary.FileExtension);
        ExportPresetLibrary.WriteFile(new ExportPreset("usuario.mio", "Mío", "", ExportPresetCategory.Web, ExportPresetOrigin.User, Full()), path);
        var store = new SettingsStore(_dir.File(SettingsStore.FileName));

        ExportPreset imported = ExportPresetLibrary.Import(store.Current.Export, path);

        Assert.Equal(23, imported.Settings.Video!.RateControl.Quality);
        store.Save();
        Assert.Null(new SettingsStore(store.Path).Load());
    }
}
