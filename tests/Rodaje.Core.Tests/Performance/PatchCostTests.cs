using System.Diagnostics;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.Core.Tests.Performance;

/// <summary>Cuánto cuesta sacar y aplicar un parche en un proyecto grande (colaboracion.md §9: «comparar el proyecto entero en cada paso»).</summary>
public class PatchCostTests(ITestOutputHelper output)
{
    private static Project Big(int clips)
    {
        var project = new Project { Id = "grande" };
        project.Media.Add(new MediaRef { Id = "m1", Name = "m", Duration = Time.FromSeconds(10_000), HasVideo = true });
        var track = new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" };
        for (int i = 0; i < clips; i++)
        {
            track.Clips.Add(new Clip { Id = "c" + i, MediaId = "m1", SourceIn = Time.FromSeconds(i), SourceOut = Time.FromSeconds(i + 1), Start = Time.FromSeconds(i) });
        }

        project.Sequences.Add(new Sequence { Id = "s1", Name = "S", VideoTracks = [track] });
        Assert.Empty(ModelValidator.Validate(project));
        return project;
    }

    // Mide tiempos: va con las pruebas de Rendimiento (nocturna), no en cada PR.
    [Trait("Categoria", "Rendimiento")]
    [Theory]
    [InlineData(500)]
    [InlineData(5000)]
    public void A_step_costs_a_full_capture_a_diff_and_an_apply(int clips)
    {
        Project a = Big(clips);
        Project b = a.Clone();
        var versions = new ElementVersions();
        SharedState.Capture(a);

        var sw = Stopwatch.StartNew();
        SharedState before = SharedState.Capture(a);
        double capture = sw.Elapsed.TotalMilliseconds;

        a.Sequences[0].VideoTracks[0].Clips[clips / 2].Enabled = false;
        sw.Restart();
        SharedState after = SharedState.Capture(a);
        Patch patch = PatchDiff.Create(before, after, versions, new PatchHeader("p", 0, "k", "paso"))!;
        double step = sw.Elapsed.TotalMilliseconds;

        string json = PatchWire.ToJson(patch);
        sw.Restart();
        PatchParseResult parsed = PatchWire.TryParse(json);
        double parse = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        PatchResult result = PatchApply.TryApply(b, new ElementVersions(), parsed.Patch!);
        double apply = sw.Elapsed.TotalMilliseconds;

        output.WriteLine($"{clips} clips: captura completa {capture:F1} ms; capturar + comparar un paso {step:F1} ms; parche de {json.Length} bytes, leerlo {parse:F1} ms, aplicarlo {apply:F1} ms.");
        Assert.True(result.IsAccepted);
        Assert.Equal(after.Hash, SharedState.Capture(b).Hash);
    }
}
