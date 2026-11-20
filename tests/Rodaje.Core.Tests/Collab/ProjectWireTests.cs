using System.Text.Json.Nodes;
using Rodaje.Core.Collab;
using Rodaje.Core.Model;

namespace Rodaje.Core.Tests.Collab;

public sealed class ProjectWireTests
{
    [Fact]
    public void The_initial_copy_has_the_same_shared_state_without_paths_or_name()
    {
        Project project = SharedStateTests.Rich();
        project.Name = "Mi película";
        foreach (MediaRef media in project.Media)
        {
            media.Path = @"D:\Privado\rodajes\" + media.Id + ".mov";
        }

        var versions = new ElementVersions();
        versions.Set(new ElementKey(ElementKind.Clip, "clip-c"), 7);

        string json = ProjectWire.ToJson(project, versions);
        ProjectParseResult parsed = ProjectWire.TryParse(json);

        Assert.DoesNotContain("Privado", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Mi película", json, StringComparison.Ordinal);
        Assert.True(parsed.IsValid, parsed.Rejection?.Message);
        Assert.Equal(SharedState.Capture(project).Hash, SharedState.Capture(parsed.Project!).Hash);
        Assert.Equal("", parsed.Project!.Name);
        Assert.All(parsed.Project.Media, m => Assert.Equal("", m.Path));
        Assert.Equal(7, parsed.Versions!.Get(ElementKind.Clip, "clip-c"));
        Assert.Empty(ModelValidator.Validate(parsed.Project));
    }

    [Fact]
    public void An_empty_project_makes_the_trip()
    {
        var project = new Project { Id = "p-vacio" };

        ProjectParseResult parsed = ProjectWire.TryParse(ProjectWire.ToJson(project));

        Assert.True(parsed.IsValid, parsed.Rejection?.Message);
        Assert.Equal("p-vacio", parsed.Project!.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"project\":\"p\"}")]
    [InlineData("{\"project\":\"p\",\"versions\":{},\"patch\":5}")]
    [InlineData("{\"project\":\"a\\nb\",\"versions\":{},\"patch\":{}}")]
    [InlineData("{\"project\":\"p\",\"versions\":{\"nube:x\":1},\"patch\":{}}")]
    [InlineData("{\"project\":\"p\",\"versions\":{},\"patch\":{},\"extra\":1}")]
    public void Broken_copies_are_rejected_without_throwing(string json)
    {
        ProjectParseResult parsed = ProjectWire.TryParse(json);

        Assert.False(parsed.IsValid);
        Assert.NotNull(parsed.Rejection);
    }

    [Fact]
    public void A_hostile_copy_with_a_path_in_a_media_or_a_file_reference_is_rejected()
    {
        string json = ProjectWire.ToJson(SharedStateTests.Rich());

        JsonNode withPath = JsonNode.Parse(json)!;
        withPath["patch"]!["ops"]!.AsArray().First(o => (string)o!["kind"]! == "media")!["content"]!["path"] = @"\\atacante\x\a.mov";
        JsonNode withFile = JsonNode.Parse(json)!;
        JsonObject clip = withFile["patch"]!["ops"]!.AsArray().First(o => (string)o!["id"]! == "clip-c")!.AsObject();
        clip["content"]!["effects"]![0]!["options"] = new JsonObject { ["lut"] = "file:../../x.cube" };

        Assert.Equal(PatchRejectionReason.Hostile, ProjectWire.TryParse(withPath.ToJsonString()).Rejection?.Reason);
        Assert.Equal(PatchRejectionReason.Hostile, ProjectWire.TryParse(withFile.ToJsonString()).Rejection?.Reason);
    }

    [Fact]
    public void A_copy_that_would_leave_the_project_invalid_is_rejected()
    {
        JsonNode copy = JsonNode.Parse(ProjectWire.ToJson(SharedStateTests.Rich()))!;
        JsonArray ops = copy["patch"]!["ops"]!.AsArray();
        JsonNode media = ops.First(o => (string)o!["kind"]! == "media")!;
        ops.Remove(media);

        ProjectParseResult parsed = ProjectWire.TryParse(copy.ToJsonString());

        Assert.False(parsed.IsValid);
    }
}
