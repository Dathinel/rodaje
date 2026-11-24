using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

/// <summary>
/// Revisión, hallazgo 4: el tope era por mensaje (900 KiB y 100 000 elementos en la copia inicial), así que lotes válidos
/// seguidos podían hacer crecer el proyecto compartido sin límite. Ahora el resultado de aplicar también tiene presupuesto.
/// </summary>
public sealed class PatchBudgetTests
{
    // Cada lote es un parche legítimo: muy por debajo de los 900 KiB y de los 20 000 cambios que admite el mensaje.
    private const int Batch = 2_000;

    private static string MediaContent(string id) =>
        ElementCodec.EncodeMedia(new MediaRef { Id = id, Name = id, HasVideo = true, Width = 1920, Height = 1080, Duration = Rodaje.Core.Timing.Time.FromSeconds(10) }, null);

    private static Patch BatchOfMedia(int first, int count) =>
        new($"lote-{first}", 0, "k:t", "Lote de medios",
            [.. Enumerable.Range(first, count).Select(i => new PatchOp(ElementKind.Media, $"m-{i}", null, 0, MediaContent($"m-{i}")))], [], []);

    [Fact]
    public void Successive_patches_cannot_grow_the_media_list_past_the_shared_budget()
    {
        var project = new Project { Id = "p" };
        var versions = new ElementVersions();

        // Lotes válidos de medios nuevos hasta el tope: todos entran.
        for (int first = 0; first < PatchApply.MaxMedia; first += Batch)
        {
            PatchResult result = PatchApply.TryApply(project, versions, BatchOfMedia(first, Batch));
            Assert.True(result.IsAccepted, result.Rejection?.Message);
        }

        Assert.Equal(PatchApply.MaxMedia, project.Media.Count);

        // El lote que lo cruza se rechaza entero y el proyecto queda como estaba.
        PatchResult crossing = PatchApply.TryApply(project, versions, BatchOfMedia(PatchApply.MaxMedia, 2));

        Assert.False(crossing.IsAccepted);
        Assert.Equal(PatchRejectionReason.TooLarge, crossing.Rejection?.Reason);
        Assert.Contains("medios", crossing.Rejection!.Message, StringComparison.Ordinal);
        Assert.Equal(PatchApply.MaxMedia, project.Media.Count);
        Assert.DoesNotContain(project.Media, m => m.Id == $"m-{PatchApply.MaxMedia}");
        Assert.Equal(PatchApply.MaxMedia, versions.Count);
    }

    [Fact]
    public void An_initial_copy_still_passes_the_same_budget_while_applying()
    {
        // El mismo tope cubre la copia recibida: con más medios que el presupuesto no llega a buen puerto.
        var project = new Project { Id = "p" };
        var versions = new ElementVersions();
        Assert.True(PatchApply.TryApply(project, versions, BatchOfMedia(0, PatchApply.MaxMedia + 1)).Rejection is { Reason: PatchRejectionReason.TooLarge });
        Assert.Empty(project.Media);
    }
}
