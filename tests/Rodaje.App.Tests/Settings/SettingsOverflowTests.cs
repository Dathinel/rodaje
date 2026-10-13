using System.Text.Json.Nodes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Settings;

/// <summary>
/// Un número de <c>ajustes.json</c> que desborda <see cref="double"/> (<c>1e400</c>) se lee como ±∞. En los ajustes de
/// exportación recordados (calidad, segundos entre fotogramas clave, sonoridad) nada lo corregía: <see cref="SettingsStore.Load"/>
/// lanzaba <see cref="ArgumentException"/> al volver a escribirlo y la app no arrancaba nunca más (tarea 267).
/// </summary>
public class SettingsOverflowTests
{
    private const int Seed = 267_100_000;

    private const string Marker = "@@numero@@";

    /// <summary>Los números de <see cref="ExportSettings"/>, con su ruta dentro de <c>export.settings</c>.</summary>
    public static readonly string[][] NumberPaths =
    [
        ["video", "rateControl", "quality"],
        ["video", "gop", "keyframeSeconds"],
        ["audio", "rateControl", "quality"],
        ["audio", "loudness", "lufs"],
        ["audio", "loudness", "maxTruePeakDbtp"],
    ];

    private static ExportSettings Remembered() => ExportPresets.YouTube1080p.Settings with
    {
        Video = ExportPresets.YouTube1080p.Settings.Video! with { RateControl = RateControl.ConstantQuality(23) },
        Audio = ExportPresets.YouTube1080p.Settings.Audio! with
        {
            RateControl = new RateControl { Mode = RateControlMode.Vbr, Quality = 4 },
            Loudness = new LoudnessTarget(-14, -1),
        },
    };

    [Fact]
    public void ExportNumberThatOverflows_StartsAndSavesAgain()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var original = new SettingsStore(path);
        original.Current.LastMediaFolder = @"D:\Videos";
        original.Current.Export.Settings = Remembered();
        original.Current.Export.UserPresets.Add(new ExportPreset("usuario.mio", "Mío", "", ExportPresetCategory.Web, ExportPresetOrigin.User, Remembered()));
        original.Save();
        string seed = File.ReadAllText(path);

        var rng = new Random(Seed);
        foreach (string where in (string[])["settings", "userPresets"])
        {
            foreach (string[] number in NumberPaths)
            {
                string literal = rng.Next(2) == 0 ? "1e400" : "-1e400";
                string what = $"semilla {Seed}: export.{where}.{string.Join('.', number)} = {literal}";
                File.WriteAllText(path, Inject(seed, where, number, literal));
                var store = new SettingsStore(path);
                try
                {
                    Assert.Null(store.Load());
                    AppSettings s = store.Current;
                    Assert.Equal(@"D:\Videos", s.LastMediaFolder);
                    Assert.Null(ExportSettingsNumbers.NonFinite(s.Export.Settings!));
                    ExportPreset preset = Assert.Single(s.Export.UserPresets);
                    Assert.Null(ExportSettingsNumbers.NonFinite(preset.Settings));
                    store.Save();
                    Assert.Null(new SettingsStore(path).Load());
                }
                catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
                {
                    Assert.Fail($"{what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }

    [Fact]
    public void FiniteExportNumbers_AreKept()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        var store = new SettingsStore(path);
        store.Current.Export.Settings = Remembered();
        store.Save();

        var loaded = new SettingsStore(path);
        Assert.Null(loaded.Load());
        ExportSettings s = loaded.Current.Export.Settings!;
        Assert.Equal(23, s.Video!.RateControl.Quality);
        Assert.Equal(2, s.Video.Gop.KeyframeSeconds);
        Assert.Equal(4, s.Audio!.RateControl.Quality);
        Assert.Equal(new LoudnessTarget(-14, -1), s.Audio.Loudness);
    }

    private static string Inject(string json, string where, string[] number, string literal)
    {
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode node = where == "settings" ? root["export"]!["settings"]! : root["export"]!["userPresets"]![0]!["settings"]!;
        foreach (string step in number[..^1])
        {
            node = node[step]!;
        }

        node[number[^1]] = Marker;
        return root.ToJsonString().Replace($"\"{Marker}\"", literal, StringComparison.Ordinal);
    }
}
