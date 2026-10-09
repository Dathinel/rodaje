using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

public class ProjectFactoryTests
{
    [Fact]
    public void CreateDefault_BuildsOneSequenceWithTwoVideoAndTwoAudioTracks()
    {
        Project project = ProjectFactory.CreateDefault("Mi proyecto", new SequentialIdGenerator());

        Assert.Empty(ModelValidator.Validate(project));
        Assert.Equal(Project.CurrentSchemaVersion, project.SchemaVersion);
        Assert.Equal("Mi proyecto", project.Name);
        Assert.Empty(project.Media);

        Sequence sequence = Assert.Single(project.Sequences);
        Assert.Equal(("Secuencia 1", 1920, 1080, 48000), (sequence.Name, sequence.Width, sequence.Height, sequence.SampleRate));
        Assert.Equal(Rational.Fps30, sequence.FrameRate);
        Assert.Equal(["Video 1", "Video 2"], sequence.VideoTracks.Select(t => t.Name));
        Assert.Equal(["Audio 1", "Audio 2"], sequence.AudioTracks.Select(t => t.Name));
        Assert.All(sequence.VideoTracks, t => Assert.Equal(TrackKind.Video, t.Kind));
        Assert.All(sequence.AudioTracks, t => Assert.Equal(TrackKind.Audio, t.Kind));
        Assert.All(sequence.AllTracks, t => Assert.Empty(t.Clips));

        string[] ids = [project.Id, sequence.Id, .. sequence.AllTracks.Select(t => t.Id)];
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.StartsWith("project-", project.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDefault_UsesGivenFrameRateAndSurvivesSerialization()
    {
        Project project = ProjectFactory.CreateDefault("NTSC", GuidIdGenerator.Instance, Rational.Ntsc30);

        Assert.Equal(Rational.Ntsc30, project.Sequences[0].FrameRate);
        SerializationSamples.AssertDeepEqual(project, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project)));
    }

    [Fact]
    public void Arguments_AreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectFactory.CreateDefault(null!, new SequentialIdGenerator()));
        Assert.Throws<ArgumentNullException>(() => ProjectFactory.CreateDefault("x", null!));
    }
}
