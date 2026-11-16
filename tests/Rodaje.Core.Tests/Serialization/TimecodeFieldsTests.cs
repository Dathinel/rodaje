using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rational = Rodaje.Core.Timing.Rational;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Campos del formato 16 que añade el intercambio (D-062): TC y carrete del medio, TC de inicio de la secuencia.</summary>
public sealed class TimecodeFieldsTests
{
    private static Project Plain() => ProjectSerializer.Deserialize(Fuzz.ProjectFileMutationFuzzTests.RichProject());

    private static JsonNode Written(Project project) => JsonNode.Parse(ProjectSerializer.Serialize(project))!;

    [Fact]
    public void Without_timecodes_nothing_changes_and_the_file_stays_below_16()
    {
        JsonNode root = Written(Plain());

        Assert.True((int)root["schemaVersion"]! < 16);
        Assert.DoesNotContain("timecode", root.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"reel\"", root.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("media-timecode")]
    [InlineData("media-reel")]
    [InlineData("sequence-start")]
    public void Each_field_alone_writes_16_and_comes_back(string field)
    {
        Project project = Plain();
        MediaRef media = project.Media[0];
        media.FrameRate = Rational.Ntsc30;
        switch (field)
        {
            case "media-timecode": media.Timecode = "01:00:00;00"; break;
            case "media-reel": media.Reel = "A001C003"; break;
            default: project.Sequences[0].StartTimecode = "01:00:00:00"; break;
        }

        string json = ProjectSerializer.Serialize(project);
        Project back = ProjectSerializer.Deserialize(json);

        Assert.Equal(16, (int)JsonNode.Parse(json)!["schemaVersion"]!);
        Assert.Empty(ModelValidator.Validate(back));
        Assert.Equal(media.Timecode, back.Media[0].Timecode);
        Assert.Equal(media.Reel, back.Media[0].Reel);
        Assert.Equal(project.Sequences[0].StartTimecode, back.Sequences[0].StartTimecode);
        Assert.Equal(json, ProjectSerializer.Serialize(back));
    }

    [Theory]
    [InlineData("01:00:00;00", 30000, 1001, true)]
    [InlineData("00:00:10:24", 25, 1, true)]
    [InlineData("00:01:00;00", 30000, 1001, false)] // número saltado en drop frame
    [InlineData("00:00:00;00", 25, 1, false)]       // drop frame a 25 fps
    [InlineData("99:99:99:99", 25, 1, false)]
    [InlineData("1:00:00:00", 25, 1, false)]
    [InlineData("01:00:00:00 ", 25, 1, false)]
    [InlineData("00:00:00:14", 15, 1, true)]        // tasa sin TC SMPTE: solo se mira la forma
    public void The_validator_checks_the_media_timecode_at_its_rate(string timecode, long num, long den, bool valid)
    {
        Project project = Plain();
        project.Media[0].FrameRate = new Rational(num, den);
        project.Media[0].Timecode = timecode;

        Assert.Equal(valid, !ModelValidator.Validate(project).Any(e => e.Contains("código de tiempo", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_bad_start_timecode_or_reel_is_rejected()
    {
        Project project = Plain();
        project.Sequences[0].StartTimecode = "25:00:00:00";
        project.Media[0].Reel = "A\u0001";

        IReadOnlyList<string> errors = ModelValidator.Validate(project);

        Assert.Contains(errors, e => e.Contains("código de tiempo de inicio", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("carrete", StringComparison.Ordinal));
    }

    [Fact]
    public void Undo_restores_the_start_timecode()
    {
        Sequence sequence = Plain().Sequences[0];
        var snapshot = Rodaje.Core.Commands.SequenceSnapshot.Capture(sequence);
        sequence.StartTimecode = "10:00:00:00";

        Assert.False(snapshot.Matches(sequence));
        snapshot.Restore(sequence);
        Assert.Null(sequence.StartTimecode);
    }
}
