using System.Text.Json.Nodes;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Serialization;

/// <summary>Campo <c>transcripts</c> del formato 16 (D-061): solo se escribe si hay alguna y vuelve igual.</summary>
public sealed class TranscriptFormatTests
{
    private static Project WithTranscript()
    {
        Project project = ProjectSerializer.Deserialize(Fuzz.ProjectFileMutationFuzzTests.RichProject());
        project.Transcripts.Add(new MediaTranscript
        {
            Id = "transcript-1",
            MediaId = project.Media[0].Id,
            Language = "es",
            Model = "large-v3-turbo-q5_0",
            Created = new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc),
            MediaDuration = Time.FromSeconds(10),
            Speakers = [new TranscriptSpeaker { Id = "speaker-1", Name = "Ana", Color = "#3A7BD5" }],
            Words =
            [
                new SpokenWord { Start = Time.Zero, End = Time.FromSeconds(0.5), Text = "Hola", Confidence = 0.98, SpeakerId = "speaker-1", ParagraphStart = true },
                new SpokenWord { Start = Time.FromSeconds(0.6), End = Time.FromSeconds(1), Text = "a", Confidence = 0.91 },
            ],
        });
        return project;
    }

    [Fact]
    public void A_transcript_writes_16_with_short_keys_and_round_trips()
    {
        string written = ProjectSerializer.Serialize(WithTranscript());

        JsonNode root = JsonNode.Parse(written)!;
        Assert.Equal(16, (int)root["schemaVersion"]!);
        JsonObject second = root["transcripts"]![0]!["words"]![1]!.AsObject();
        Assert.Equal(["s", "e", "t", "c"], second.Select(p => p.Key));
        Assert.Equal("2026-11-15T10:00:00Z", (string)root["transcripts"]![0]!["created"]!);

        Project reopened = ProjectSerializer.Deserialize(written);
        Assert.True(reopened.Transcripts[0].ContentEquals(WithTranscript().Transcripts[0]));
        Assert.Equal(written, ProjectSerializer.Serialize(reopened));
    }

    [Fact]
    public void Removing_the_last_transcript_goes_back_to_the_old_format()
    {
        Project project = WithTranscript();
        project.Transcripts.Clear();

        string written = ProjectSerializer.Serialize(project);

        Assert.Equal(13, (int)JsonNode.Parse(written)!["schemaVersion"]!);
        Assert.DoesNotContain("transcripts", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Clone_copies_the_transcripts_deeply()
    {
        Project project = WithTranscript();

        Project copy = project.Clone();
        copy.Transcripts[0].Words[0].Text = "Adiós";

        Assert.Equal("Hola", project.Transcripts[0].Words[0].Text);
        Assert.Same(project.Transcripts[0], project.FindTranscript(project.Media[0].Id));
    }
}
