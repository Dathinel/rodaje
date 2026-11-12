using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Formato 15: <c>colorSpace</c> de la secuencia (HDR, fase C).</summary>
public sealed class SequenceColorSpaceTests
{
    [Theory]
    [InlineData(SequenceColorSpace.HdrPq, "hdr-pq")]
    [InlineData(SequenceColorSpace.HdrHlg, "hdr-hlg")]
    public void An_hdr_sequence_writes_format_15_and_round_trips(SequenceColorSpace colorSpace, string name)
    {
        Project project = SerializationSamples.Rich();
        project.Sequences[0].ColorSpace = colorSpace;

        string json = ProjectSerializer.Serialize(project);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(15, (int)root["schemaVersion"]!);
        Assert.Equal(name, (string)root["sequences"]![0]!["colorSpace"]!);

        Project again = ProjectSerializer.Deserialize(json);
        Assert.Equal(colorSpace, again.Sequences[0].ColorSpace);
        Assert.Equal(json, ProjectSerializer.Serialize(again));
    }

    [Fact]
    public void An_sdr_project_does_not_write_the_field_nor_change_its_version()
    {
        string json = ProjectSerializer.Serialize(SerializationSamples.Rich());
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        Assert.DoesNotContain("colorSpace", json, StringComparison.Ordinal);
        Assert.Equal(13, (int)root["schemaVersion"]!);
        Assert.Equal(SequenceColorSpace.Sdr709, ProjectSerializer.Deserialize(json).Sequences[0].ColorSpace);
    }

    [Fact]
    public void Migration_14_to_15_only_bumps_the_version_and_a_sequence_without_the_field_is_sdr()
    {
        JsonObject root = JsonNode.Parse("""{"schemaVersion": 14, "id": "p", "sequences": [{"id": "s", "name": "S"}]}""")!.AsObject();

        ProjectMigrator.AllowHdrSequences(root);

        Assert.Equal(15, (int)root["schemaVersion"]!);
        Assert.Null(root["sequences"]![0]!["colorSpace"]);
        Assert.Equal(SequenceColorSpace.Sdr709, ProjectSerializer.Deserialize(root.ToJsonString()).Sequences[0].ColorSpace);
    }

    [Fact]
    public void An_unknown_color_space_is_rejected()
    {
        const string json = """{"schemaVersion": 15, "id": "p", "sequences": [{"id": "s", "name": "S", "colorSpace": "hdr-dolby"}]}""";

        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Deserialize(json));
    }

    [Fact]
    public void Clone_keeps_the_color_space()
    {
        var sequence = new Sequence { Id = "s", ColorSpace = SequenceColorSpace.HdrHlg };
        Assert.Equal(SequenceColorSpace.HdrHlg, sequence.Clone().ColorSpace);
    }
}
