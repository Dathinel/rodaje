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

        // Las palabras van en una sola línea, porque son casi todo el peso del archivo (D-061).
        Assert.Contains(
            """{"s":0,"e":352800000,"t":"Hola","c":0.98,"sp":"speaker-1","p":true},{"s":423360000,"e":705600000,"t":"a","c":0.91}""",
            written,
            StringComparison.Ordinal);

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
    public void An_hour_of_speech_grows_the_file_less_than_0_6_MB()
    {
        // Una hora de habla son unas 9 000 palabras y ~0,5 MB de JSON (D-061).
        Project project = WithTranscript();
        string[] vocabulary = ["hola", "mundo", "entonces", "palabra", "vale", "entrevista"];
        double[] confidences = [0.9, 0.85, 0.92, 0.99, 0.88];
        project.Transcripts[0].MediaDuration = Time.FromSeconds(3600);
        project.Transcripts[0].Words =
        [
            .. Enumerable.Range(0, 9000).Select(i => new SpokenWord
            {
                Start = Time.FromSeconds(i * 0.4),
                End = Time.FromSeconds(i * 0.4 + 0.3),
                Text = vocabulary[i % vocabulary.Length],
                Confidence = confidences[i % confidences.Length],
                SpeakerId = i % 10 == 0 ? "speaker-1" : null,
            }),
        ];

        int withTranscript = System.Text.Encoding.UTF8.GetByteCount(ProjectSerializer.Serialize(project));
        project.Transcripts.Clear();
        int without = System.Text.Encoding.UTF8.GetByteCount(ProjectSerializer.Serialize(project));

        Assert.True(withTranscript - without <= 600_000, $"la transcripción de una hora pesa {withTranscript - without} bytes");
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
