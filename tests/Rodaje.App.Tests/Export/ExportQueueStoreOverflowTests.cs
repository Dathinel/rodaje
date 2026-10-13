using System.Text.Json.Nodes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Un número de los ajustes de una entrada de <c>cola-exportaciones.json</c> que desborda <see cref="double"/>
/// (<c>1e400</c>) se lee como ±∞. <see cref="ExportQueueStore.Load"/> no lo revisaba: la entrada volvía a la cola y el
/// siguiente <see cref="ExportQueueStore.Save"/> lanzaba <see cref="ArgumentException"/> al escribirla (tarea 288). Ahora
/// se salta como una entrada rota y queda en la copia «.dañado».
/// </summary>
public sealed class ExportQueueStoreOverflowTests
{
    private const int Seed = 288_100_000;

    private const string Marker = "@@numero@@";

    /// <summary>Los números de <see cref="ExportSettings"/>, con su ruta dentro de <c>settings</c>.</summary>
    private static readonly string[][] NumberPaths =
    [
        ["video", "rateControl", "quality"],
        ["video", "gop", "keyframeSeconds"],
        ["audio", "rateControl", "quality"],
        ["audio", "loudness", "lufs"],
        ["audio", "loudness", "maxTruePeakDbtp"],
    ];

    private static ExportSettings Settings() => ExportPresets.YouTube1080p.Settings with
    {
        Video = ExportPresets.YouTube1080p.Settings.Video! with { RateControl = RateControl.ConstantQuality(23) },
        Audio = ExportPresets.YouTube1080p.Settings.Audio! with
        {
            RateControl = new RateControl { Mode = RateControlMode.Vbr, Quality = 4 },
            Loudness = new LoudnessTarget(-14, -1),
        },
    };

    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), Settings());
    }

    [Fact]
    public void NumeroQueDesborda_SeSaltaLaEntradaYElSiguienteSaveNoLanza()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        store.Save([(Request("primera.mp4"), false), (Request("infinita.mp4"), false), (Request("tercera.mp4"), true)]);
        string seed = File.ReadAllText(store.Path);

        var rng = new Random(Seed);
        foreach (string[] number in NumberPaths)
        {
            string literal = rng.Next(2) == 0 ? "1e400" : "-1e400";
            string what = $"semilla {Seed}: exports[1].settings.{string.Join('.', number)} = {literal}";
            foreach (string copy in Directory.GetFiles(dir.Path, "*.dañado*"))
            {
                File.Delete(copy);
            }

            File.WriteAllText(store.Path, Inject(seed, number, literal));
            try
            {
                IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> loaded = store.Load();

                Assert.Equal(["primera.mp4", "tercera.mp4"], loaded.Select(l => Path.GetFileName(l.Request.OutputPath)));
                Assert.All(loaded, l => Assert.Null(ExportSettingsNumbers.NonFinite(l.Request.Settings)));
                Assert.Equal(1, store.Skipped);
                Assert.Equal(store.DamagedCopyPath, store.DamagedPath);
                Assert.Contains(literal, File.ReadAllText(store.DamagedCopyPath), StringComparison.Ordinal);

                // Lo que lanzaba: volver a escribir la cola con lo leído.
                store.Save([.. loaded]);
                Assert.Equal(2, new ExportQueueStore(store.Path).Load().Count);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"{what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    [Fact]
    public void SoloEntradasQueDesbordan_SeAparta()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        store.Save([(Request("infinita.mp4"), false)]);
        File.WriteAllText(store.Path, Inject(File.ReadAllText(store.Path), NumberPaths[0], "1e400", entry: 0));

        Assert.Empty(store.Load());
        Assert.Equal(1, store.Skipped);
        Assert.False(File.Exists(store.Path));
        Assert.True(File.Exists(store.DamagedCopyPath));
    }

    [Fact]
    public void NumerosFinitos_SeConservan()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        store.Save([(Request("buena.mp4"), false)]);

        ExportSettings s = Assert.Single(store.Load()).Request.Settings;
        Assert.Equal(0, store.Skipped);
        Assert.Equal(23, s.Video!.RateControl.Quality);
        Assert.Equal(4, s.Audio!.RateControl.Quality);
        Assert.Equal(new LoudnessTarget(-14, -1), s.Audio.Loudness);
    }

    private static string Inject(string json, string[] number, string literal, int entry = 1)
    {
        JsonNode root = JsonNode.Parse(json)!;
        JsonNode node = root["exports"]![entry]!["settings"]!;
        foreach (string step in number[..^1])
        {
            node = node[step]!;
        }

        node[number[^1]] = Marker;
        return root.ToJsonString().Replace($"\"{Marker}\"", literal, StringComparison.Ordinal);
    }
}
