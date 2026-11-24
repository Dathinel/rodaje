using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

/// <summary>Defectos que destapó la revisión independiente de B10 y su arreglo.</summary>
public sealed class PatchHardeningTests
{
    private static readonly string Sha = new('B', 64);

    private static PatchOp ContentOp(Project project, ElementKind kind, string id, Action<JsonObject> change)
    {
        ElementState state = SharedState.Capture(project).Elements[new ElementKey(kind, id)];
        JsonObject content = JsonNode.Parse(state.Content)!.AsObject();
        change(content);
        return new PatchOp(kind, id, state.Parent, 0, content.ToJsonString());
    }

    private static Patch Of(params PatchOp[] ops) => new("p1", 0, "k:a", "prueba", ops, [], []);

    [Theory]
    [InlineData(ElementKind.Clip, "clip-c")]
    [InlineData(ElementKind.Track, "")]
    public void A_null_inside_a_list_is_rejected_without_throwing_and_changes_nothing(ElementKind kind, string id)
    {
        Project project = SharedStateTests.Rich();
        if (kind == ElementKind.Track)
        {
            id = project.Sequences[0].GetClip("clip-c").Track.Id;
        }

        string hash = SharedState.Capture(project).Hash;
        PatchOp op = ContentOp(project, kind, id, c => c["effects"] = new JsonArray(JsonValue.Create<string?>(null)));
        var versions = new ElementVersions();

        PatchResult result = PatchApply.TryApply(project, versions, Of(op));

        Assert.False(result.IsAccepted);
        Assert.Equal(hash, SharedState.Capture(project).Hash);
        Assert.Equal(0, versions.Count);
    }

    [Fact]
    public void The_maximum_version_is_refused_and_advancing_never_overflows()
    {
        var delete = new Patch("p", 0, "k", "d", [new PatchOp(ElementKind.Clip, "c", "t", int.MaxValue, null)], [], []);

        Assert.NotNull(WireSanitizer.Check(PatchWire.ToJson(delete)));
        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Clip, "c"), int.MaxValue);
        versions.Advance(delete);
        Assert.Equal(int.MaxValue, versions.Get(ElementKind.Clip, "c"));
    }

    [Theory]
    [InlineData("../x.cube")]
    [InlineData("carpeta/../x")]
    [InlineData("/tmp/x")]
    [InlineData("\\x")]
    [InlineData("C:x")]
    [InlineData("./x")]
    [InlineData("\\\\servidor\\recurso")]
    public void Path_like_values_are_hostile_in_any_option_even_an_unknown_one(string value)
    {
        Project project = SharedStateTests.Rich();
        PatchOp op = ContentOp(project, ElementKind.Clip, "clip-c", c => c["effects"]![0]!["options"] = new JsonObject { ["opcion-nueva"] = value });

        PatchRejection? rejection = WireSanitizer.Check(PatchWire.ToJson(Of(op)));

        Assert.Equal(PatchRejectionReason.Hostile, rejection?.Reason);
    }

    [Fact]
    public void A_validator_rejection_does_not_repeat_the_value_that_caused_it()
    {
        Project project = SharedStateTests.Rich();
        EffectInstance color = new() { Id = "fx-color", Type = BuiltInEffects.Color };
        project.Sequences[0].GetClip("clip-c").Clip.Effects.Add(color);
        PatchOp op = ContentOp(project, ElementKind.Clip, "clip-c", c => c["effects"]!.AsArray().Last()!["options"] = new JsonObject { [BuiltInEffects.ColorOptions.Lut] = "esto no es una referencia" });
        project.Sequences[0].GetClip("clip-c").Clip.Effects.Remove(color);
        var versions = new ElementVersions();
        versions.Set(op.Key, 0);

        PatchResult result = PatchApply.TryApply(project, versions, Of(op));

        Assert.Equal(PatchRejectionReason.Invalid, result.Rejection?.Reason);
        Assert.DoesNotContain("esto no es", result.Rejection!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_without_a_zone_means_the_same_on_every_machine()
    {
        Project project = SharedStateTests.Rich();
        project.Transcripts.Add(new MediaTranscript
        {
            Id = "tr-1",
            MediaId = project.Media[0].Id,
            Language = "es",
            Model = "m",
            Created = new DateTime(2026, 11, 15, 15, 0, 0, DateTimeKind.Utc),
            MediaDuration = project.Media[0].Duration,
        });
        SharedState reference = SharedState.Capture(project);
        PatchOp op = ContentOp(project, ElementKind.Transcript, project.Media[0].Id, c => c["created"] = "2026-11-15T15:00:00");
        var target = project.Clone();
        target.Transcripts[0].Created = new DateTime(2026, 11, 15, 16, 0, 0, DateTimeKind.Utc);

        PatchResult result = PatchApply.TryApply(target, new ElementVersions(), Of(op));

        Assert.True(result.IsAccepted, result.Rejection?.Message);
        Assert.Equal(reference.Hash, SharedState.Capture(target).Hash);
    }

    [Fact]
    public void A_track_that_somebody_filled_cannot_be_deleted_without_deleting_its_clips()
    {
        Project project = SharedStateTests.Rich();
        string trackId = project.Sequences[0].GetClip("clip-c").Track.Id;
        string hash = SharedState.Capture(project).Hash;
        var delete = new PatchOp(ElementKind.Track, trackId, project.Sequences[0].Id, 0, null);

        PatchResult result = PatchApply.TryApply(project, new ElementVersions(), Of(delete));

        Assert.Equal(PatchRejectionReason.VersionConflict, result.Rejection?.Reason);
        Assert.Equal(hash, SharedState.Capture(project).Hash);
    }

    [Fact]
    public void Undoing_the_creation_of_a_track_is_refused_once_somebody_put_a_clip_in_it()
    {
        Project project = SharedStateTests.Rich();
        var history = new CollabHistory();
        SharedState before = SharedState.Capture(project);
        var track = new Track { Id = "t-nueva", Kind = TrackKind.Video, Name = "Nueva" };
        project.Sequences[0].VideoTracks.Add(track);
        history.Record(before, SharedState.Capture(project), "Pista nueva");

        Clip other = project.Sequences[0].GetClip("clip-c").Clip.Clone();
        other.Id = "clip-ajeno";
        other.Effects = [];
        track.Clips.Add(other);
        HistoryOutcome outcome = history.PlanUndo(SharedState.Capture(project), new ElementVersions(), new PatchHeader("u", 0, "k", "d"));

        Assert.Equal(HistoryFailure.ChangedByOthers, outcome.Failure);
        Assert.Equal(new ElementKey(ElementKind.Clip, "clip-ajeno"), outcome.Element);
    }

    [Fact]
    public void Undoing_a_reorder_is_refused_when_somebody_reordered_the_same_list_afterwards()
    {
        Project project = SharedStateTests.Rich();
        project.Media.Add(new MediaRef { Id = "mx", Name = "x", Duration = Rodaje.Core.Timing.Time.FromSeconds(1), HasVideo = true });
        project.Media.Add(new MediaRef { Id = "my", Name = "y", Duration = Rodaje.Core.Timing.Time.FromSeconds(1), HasVideo = true });
        var history = new CollabHistory();
        SharedState before = SharedState.Capture(project);
        (project.Media[^1], project.Media[^2]) = (project.Media[^2], project.Media[^1]);
        history.Record(before, SharedState.Capture(project), "Reordenar medios");

        (project.Media[^1], project.Media[^2]) = (project.Media[^2], project.Media[^1]);
        HistoryOutcome outcome = history.PlanUndo(SharedState.Capture(project), new ElementVersions(), new PatchHeader("u", 0, "k", "d"));

        Assert.Equal(HistoryFailure.ChangedByOthers, outcome.Failure);
    }

    [Fact]
    public void The_blob_hashes_a_patch_needs_are_listed_once_and_in_order()
    {
        Project project = SharedStateTests.Rich();
        string other = new('A', 64);
        PatchOp op = ContentOp(project, ElementKind.Clip, "clip-c", c => c["effects"]![0]!["options"] = new JsonObject { ["a"] = "blob:" + Sha, ["b"] = "blob:" + other, ["c"] = "blob:" + Sha });

        Assert.Equal([other, Sha], PatchWire.BlobHashes(Of(op)));
    }

    [Fact]
    public void A_replica_that_does_not_have_the_blob_yet_keeps_its_identity_with_a_placeholder_reference()
    {
        var store = new StoreMap();
        Project a = SharedStateTests.Rich();
        EffectInstance lut = new() { Id = "fx-lut", Type = BuiltInEffects.Color, Options = { [BuiltInEffects.ColorOptions.Lut] = store.FileReferenceFor(Sha)! } };
        Clip clipOfA = a.Sequences[0].GetClip("clip-c").Clip;
        Project b = a.Clone();
        SharedState before = SharedState.Capture(b, store);
        clipOfA.Effects.Add(lut);
        SharedState after = SharedState.Capture(a, store);
        Patch patch = PatchDiff.Create(before, after, new ElementVersions(), new PatchHeader("p", 0, "k", "lut"))!;

        PatchResult result = PatchApply.TryApply(b, new ElementVersions(), PatchWire.TryParse(PatchWire.ToJson(patch)).Patch!, new PatchApplyOptions { Blobs = store });

        Assert.True(result.IsAccepted, result.Rejection?.Message);
        Assert.Equal(after.Hash, SharedState.Capture(b, store).Hash);
        Assert.Equal([Sha], PatchWire.BlobHashes(patch));
    }

    /// <summary>Un almacén de blobs que da siempre la ruta de destino, exista el archivo o no.</summary>
    private sealed class StoreMap : IBlobMap
    {
        public string? Sha256Of(string fileReference) =>
            fileReference.StartsWith("file:almacen/", StringComparison.Ordinal) && fileReference.EndsWith(".cube", StringComparison.Ordinal)
                ? fileReference["file:almacen/".Length..^".cube".Length]
                : null;

        public string? FileReferenceFor(string sha256) => $"file:almacen/{sha256}.cube";
    }
}
