using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

public sealed class PatchReservationsTests
{
    private static PatchOp Clip(string id, string? parent) => new(ElementKind.Clip, id, parent, 1, "{}");

    private static Patch Of(params PatchOp[] ops) => new("p", 0, "k", "t", ops, [], []);

    [Fact]
    public void A_clip_that_changes_track_needs_both_tracks()
    {
        Project project = SharedStateTests.Rich();
        (Track from, _) = project.Sequences[0].GetClip("clip-c");
        Track to = project.Sequences[0].AllTracks.First(t => t.Id != from.Id && t.Kind == from.Kind);

        IReadOnlySet<string> keys = PatchReservations.Required(Of(Clip("clip-c", to.Id)), project);

        Assert.Equal([PatchReservations.Track(from.Id), PatchReservations.Track(to.Id)], keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Editing_a_clip_in_place_needs_only_its_track()
    {
        Project project = SharedStateTests.Rich();
        (Track track, _) = project.Sequences[0].GetClip("clip-c");

        Assert.Equal([PatchReservations.Track(track.Id)], PatchReservations.Required(Of(Clip("clip-c", track.Id)), project));
    }

    [Fact]
    public void Global_things_need_the_project_and_a_new_media_needs_nothing()
    {
        Project project = SharedStateTests.Rich();

        Assert.Equal([PatchReservations.Project], PatchReservations.Required(Of(new PatchOp(ElementKind.Marker, "mk", project.Sequences[0].Id, 0, "{}")), project));
        Assert.Equal([PatchReservations.Project], PatchReservations.Required(Of(new PatchOp(ElementKind.Media, project.Media[0].Id, null, 1, null)), project));
        Assert.Empty(PatchReservations.Required(Of(new PatchOp(ElementKind.Media, "m-nuevo", null, 0, "{}")), project));
        Assert.Equal([PatchReservations.Project], PatchReservations.Required(new Patch("p", 0, "k", "t", [], [new PatchOrder(PatchContainers.Media, [])], []), project));
    }

    [Fact]
    public void A_track_created_by_the_patch_is_covered_by_the_project_key()
    {
        Project project = SharedStateTests.Rich();
        string sequence = project.Sequences[0].Id;

        IReadOnlySet<string> keys = PatchReservations.Required(
            Of(new PatchOp(ElementKind.Track, "t-nueva", sequence, 0, "{}"), Clip("c-nuevo", "t-nueva")), project);

        Assert.Equal([PatchReservations.Project], keys);
    }
}
