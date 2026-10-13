using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

// TEMPORAL (tarea 125): mide por fases en la CI de Windows; se quita antes de unir.
public sealed class ZzDiagnosticoIguales : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-diag125-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Diagnostico()
    {
        Directory.CreateDirectory(_dir);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"cultura «{CultureInfo.CurrentCulture.Name}», server GC {System.Runtime.GCSettings.IsServerGC}, cpus {Environment.ProcessorCount}");
        foreach (string shape in new[] { "iguales", "cadena", "iguales" })
        {
            var best = new Dictionary<(int, string), double>();
            foreach (int count in new[] { 1000, 8000, 1000, 8000, 1000, 8000, 1000, 8000, 1000, 8000 })
            {
                foreach ((string phase, double ms) in Measure(shape, count))
                {
                    best[(count, phase)] = Math.Min(best.GetValueOrDefault((count, phase), double.MaxValue), ms);
                }
            }

            foreach (string phase in new[] { "json", "load", "from", "tree", "distinct", "gc2", "gcpause" })
            {
                double a = best[(1000, phase)], b = best[(8000, phase)];
                report.AppendLine(CultureInfo.InvariantCulture, $"{shape} {phase}: 1000 → {a:F2}, 8000 → {b:F2}, ×{b / Math.Max(a, 0.001):F1}");
            }
        }

        Assert.Fail(report.ToString());
    }

    private IEnumerable<(string, double)> Measure(string shape, int count)
    {
        var w = Stopwatch.StartNew();
        var folders = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            string? parent = shape == "cadena" ? (i == 0 ? null : $"f{i - 1}") : null;
            folders.Add(new JsonObject { ["id"] = $"f{i}", ["name"] = shape == "iguales" ? "Tomas" : $"n{i}", ["parent"] = parent });
        }

        var store = new LibraryOrganizationStore(_dir);
        File.WriteAllText(store.PathFor("p"), new JsonObject { ["version"] = 2, ["folders"] = folders }.ToJsonString());
        yield return ("json", w.Elapsed.TotalMilliseconds);

        int gc2 = GC.CollectionCount(2);
        TimeSpan pause = GC.GetTotalPauseDuration();
        w.Restart();
        LibraryOrganization org = store.Load("p");
        yield return ("load", w.Elapsed.TotalMilliseconds);
        yield return ("gc2", GC.CollectionCount(2) - gc2);
        yield return ("gcpause", (GC.GetTotalPauseDuration() - pause).TotalMilliseconds);

        List<LibraryFolder> copy = [.. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = shape == "iguales" ? "Tomas" : $"n{i}", ParentId = shape == "cadena" && i > 0 ? $"f{i - 1}" : null })];
        w.Restart();
        LibraryOrganization.From(copy, null, null);
        yield return ("from", w.Elapsed.TotalMilliseconds);

        w.Restart();
        Assert.Equal(count, org.Tree().Count);
        yield return ("tree", w.Elapsed.TotalMilliseconds);

        w.Restart();
        Assert.Equal(count, org.Folders.Select(f => (f.ParentId, f.Name)).Distinct().Count());
        yield return ("distinct", w.Elapsed.TotalMilliseconds);
    }
}
