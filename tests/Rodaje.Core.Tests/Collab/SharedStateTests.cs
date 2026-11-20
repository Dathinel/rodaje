using Rodaje.Core.Collab;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Fuzz;

namespace Rodaje.Core.Tests.Collab;

public sealed class SharedStateTests
{
    internal static Project Rich() => ProjectSerializer.Deserialize(ProjectFileMutationFuzzTests.RichProject());

    [Fact]
    public void Capturing_twice_or_a_clone_gives_the_same_hash()
    {
        Project project = Rich();

        string hash = SharedState.Capture(project).Hash;

        Assert.Equal(hash, SharedState.Capture(project).Hash);
        Assert.Equal(hash, SharedState.Capture(project.Clone()).Hash);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void The_project_name_and_the_media_paths_are_not_shared()
    {
        Project project = Rich();
        string hash = SharedState.Capture(project).Hash;

        Project other = project.Clone();
        other.Name = "Otro nombre";
        foreach (MediaRef media in other.Media)
        {
            media.Path = @"E:\otra\carpeta\" + media.Id + ".mp4";
        }

        Assert.Equal(hash, SharedState.Capture(other).Hash);
        Assert.DoesNotContain(@"E:\otra", string.Join('\n', SharedState.Capture(other).Elements.Values.Select(e => e.Content)), StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", SharedState.Capture(other).Elements.Where(e => e.Key.Kind == ElementKind.Media).First().Value.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_shared_change_moves_the_hash()
    {
        Project project = Rich();
        string hash = SharedState.Capture(project).Hash;
        Clip clip = project.Sequences[0].GetClip("clip-c").Clip;

        Project moved = project.Clone();
        moved.Sequences[0].GetClip("clip-c").Clip.Enabled = !clip.Enabled;
        Project marker = project.Clone();
        marker.Sequences[0].Markers.Add(new Marker { Id = "mk-test", Name = "x" });
        Project reordered = project.Clone();
        reordered.Sequences[0].VideoTracks.Reverse();

        Assert.NotEqual(hash, SharedState.Capture(moved).Hash);
        Assert.NotEqual(hash, SharedState.Capture(marker).Hash);
        if (project.Sequences[0].VideoTracks.Count > 1)
        {
            Assert.NotEqual(hash, SharedState.Capture(reordered).Hash);
        }
    }

    [Fact]
    public void Clips_hang_from_their_track_and_tracks_from_their_sequence()
    {
        Project project = Rich();
        SharedState state = SharedState.Capture(project);
        (Track track, Clip clip) = project.Sequences[0].GetClip("clip-c");

        Assert.Equal(track.Id, state.Elements[new ElementKey(ElementKind.Clip, clip.Id)].Parent);
        Assert.Equal(project.Sequences[0].Id, state.Elements[new ElementKey(ElementKind.Track, track.Id)].Parent);
        Assert.DoesNotContain("\"clips\":[{", state.Elements[new ElementKey(ElementKind.Track, track.Id)].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Dictionary_insertion_order_does_not_change_the_content()
    {
        Project a = Rich();
        Project b = a.Clone();
        EffectInstance effect = a.Sequences[0].GetClip("clip-c").Clip.Effects[0];
        EffectInstance same = b.Sequences[0].GetClip("clip-c").Clip.Effects[0];
        same.Parameters = new Dictionary<string, Parameter>(effect.Parameters.Reverse());

        Assert.Equal(SharedState.Capture(a).Hash, SharedState.Capture(b).Hash);
    }

    [Fact]
    public void A_file_reference_travels_empty_without_a_blob_map_and_as_a_blob_with_one()
    {
        Project project = Rich();
        EffectInstance lut = new()
        {
            Id = "fx-lut",
            Type = BuiltInEffects.Color,
            Options = { [BuiltInEffects.ColorOptions.Lut] = "file:C:/luts/mi.cube" },
        };
        project.Sequences[0].GetClip("clip-c").Clip.Effects.Add(lut);
        string sha = new('A', 64);

        string without = SharedState.Capture(project).Elements[new ElementKey(ElementKind.Clip, "clip-c")].Content;
        string with = SharedState.Capture(project, new FixedMap(sha)).Elements[new ElementKey(ElementKind.Clip, "clip-c")].Content;

        Assert.DoesNotContain("luts", without, StringComparison.Ordinal);
        Assert.DoesNotContain("file:", without, StringComparison.Ordinal);
        Assert.Contains("blob:" + sha, with, StringComparison.Ordinal);
        Assert.DoesNotContain("luts", with, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_the_file_cannot_hold_is_reported_not_swallowed()
    {
        Project project = Rich();
        project.Sequences[0].GetClip("clip-c").Clip.Effects[0].Parameters.Values.First().Value = double.NaN;

        Assert.Throws<ProjectFormatException>(() => SharedState.Capture(project));
    }

    private sealed class FixedMap(string sha) : IBlobMap
    {
        public string? Sha256Of(string fileReference) => sha;

        public string? FileReferenceFor(string sha256) => null;
    }
}
