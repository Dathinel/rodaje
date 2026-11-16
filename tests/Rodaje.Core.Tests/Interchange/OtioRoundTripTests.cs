using System.Text;
using System.Text.Json;
using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Otio;
using Rodaje.Core.Model;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>
/// Pruebas de ida y vuelta para OpenTimelineIO (fase F4b de D-062):
/// 500 semillas con metadata, 500 sin metadata, edición externa simulada y corpus hostil.
/// </summary>
public sealed class OtioRoundTripTests
{
    [Fact]
    public void RoundTrip_500_random_timelines_with_metadata()
    {
        var options = new InterchangeOptions();
        for (int seed = 1; seed <= 500; seed++)
        {
            var timelineOptions = new RandomTimelineOptions
            {
                Titles = seed % 5 == 0,
                Effects = seed % 7 == 0,
            };

            TimelineDraft draft = RandomTimelines.Create(seed, timelineOptions);
            var fidelityReport = new InterchangeReport();
            TimelineDraft expected = Fidelity.Project(draft.Project, draft.Sequence, InterchangeFormat.Otio, options, fidelityReport);

            IReadOnlyList<InterchangeOutput> outputs = OtioWriter.Write(draft.Project, draft.Sequence, null, options);
            Assert.Single(outputs);

            var readReport = new InterchangeReport();
            TimelineDraft actual = OtioReader.Read(outputs[0].Text, null, options, readReport);

            string? diff = Fidelity.Difference(expected, actual);
            Assert.True(diff is null, $"Semilla {seed} falló con metadata: {diff}");
        }
    }

    [Fact]
    public void RoundTrip_500_random_timelines_without_metadata()
    {
        var options = new InterchangeOptions();
        for (int seed = 1; seed <= 500; seed++)
        {
            var timelineOptions = new RandomTimelineOptions
            {
                Titles = seed % 5 == 0,
                Effects = seed % 7 == 0,
            };

            TimelineDraft draft = RandomTimelines.Create(seed, timelineOptions);
            var fidelityReport = new InterchangeReport();
            TimelineDraft expected = OtioFidelity.ProjectWithoutMetadata(draft.Project, draft.Sequence, options, fidelityReport);

            IReadOnlyList<InterchangeOutput> outputs = OtioWriter.Write(draft.Project, draft.Sequence, null, options);
            Assert.Single(outputs);

            string strippedJson = StripMetadataRodaje(outputs[0].Text);
            var readReport = new InterchangeReport();
            TimelineDraft actual = OtioReader.Read(strippedJson, null, options, readReport);

            string? diff = Fidelity.Difference(expected, actual);
            Assert.True(diff is null, $"Semilla {seed} falló sin metadata: {diff}");
        }
    }

    [Fact]
    public void Simulated_external_edit_moving_a_clip_keeps_other_clips_adjustments()
    {
        TimelineDraft draft = RandomTimelines.Create(42, new RandomTimelineOptions { Effects = true });
        var options = new InterchangeOptions();
        IReadOnlyList<InterchangeOutput> outputs = OtioWriter.Write(draft.Project, draft.Sequence, null, options);
        string json = outputs[0].Text;

        // Modificar externamente insertando un Gap antes del primer clip en la primera pista
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name != "tracks")
                {
                    prop.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject("tracks");
                foreach (JsonProperty tProp in prop.Value.EnumerateObject())
                {
                    if (tProp.Name != "children")
                    {
                        tProp.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartArray("children");
                    bool firstTrack = true;
                    foreach (JsonElement track in tProp.Value.EnumerateArray())
                    {
                        if (!firstTrack)
                        {
                            track.WriteTo(writer);
                            continue;
                        }

                        firstTrack = false;
                        writer.WriteStartObject();
                        foreach (JsonProperty trkProp in track.EnumerateObject())
                        {
                            if (trkProp.Name != "children")
                            {
                                trkProp.WriteTo(writer);
                                continue;
                            }

                            writer.WriteStartArray("children");
                            // Insertar un hueco de 24 cuadros al principio para mover el primer clip
                            writer.WriteStartObject();
                            writer.WriteString("OTIO_SCHEMA", "Gap.1");
                            writer.WriteString("name", "ExtraGap");
                            writer.WriteStartObject("source_range");
                            writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
                            writer.WriteStartObject("start_time");
                            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
                            writer.WriteNumber("value", 0);
                            writer.WriteNumber("rate", draft.Sequence.FrameRate.ToDouble());
                            writer.WriteEndObject();
                            writer.WriteStartObject("duration");
                            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
                            writer.WriteNumber("value", 24);
                            writer.WriteNumber("rate", draft.Sequence.FrameRate.ToDouble());
                            writer.WriteEndObject();
                            writer.WriteEndObject();
                            writer.WriteStartArray("effects");
                            writer.WriteEndArray();
                            writer.WriteStartArray("markers");
                            writer.WriteEndArray();
                            writer.WriteStartObject("metadata");
                            writer.WriteEndObject();
                            writer.WriteEndObject(); // ExtraGap

                            foreach (JsonElement ch in trkProp.Value.EnumerateArray())
                            {
                                ch.WriteTo(writer);
                            }

                            writer.WriteEndArray();
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        string modifiedJson = Encoding.UTF8.GetString(ms.ToArray());
        var report = new InterchangeReport();
        TimelineDraft read = OtioReader.Read(modifiedJson, null, options, report);

        // Manda lo editado: el primer clip de V1 empieza 24 cuadros más tarde
        Track v1 = read.Sequence.VideoTracks[0];
        Assert.True(v1.Clips[0].Start >= Time.FromFrames(24, read.Sequence.FrameRate));

        // Los clips que no cambiaron en su rango/medio conservan sus efectos y ajustes
        Assert.Contains(read.Sequence.AllTracks.SelectMany(t => t.Clips), c => c.Effects.Count > 0 || c.Color is not null);
    }

    [Fact]
    public void Simulated_external_edit_changing_clip_speed_discards_its_adjustments()
    {
        TimelineDraft draft = RandomTimelines.Create(10, new RandomTimelineOptions { Effects = true, Speeds = false });
        Clip clipToChange = draft.Sequence.VideoTracks[0].Clips[0];
        clipToChange.Color = "#FF0000";

        var options = new InterchangeOptions();
        IReadOnlyList<InterchangeOutput> outputs = OtioWriter.Write(draft.Project, draft.Sequence, null, options);
        string json = outputs[0].Text;

        // Modificar externamente la velocidad del primer clip añadiendo LinearTimeWarp.1
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name != "tracks")
                {
                    prop.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject("tracks");
                foreach (JsonProperty tProp in prop.Value.EnumerateObject())
                {
                    if (tProp.Name != "children")
                    {
                        tProp.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartArray("children");
                    bool firstClipModified = false;
                    foreach (JsonElement track in tProp.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (JsonProperty trkProp in track.EnumerateObject())
                        {
                            if (trkProp.Name != "children")
                            {
                                trkProp.WriteTo(writer);
                                continue;
                            }

                            writer.WriteStartArray("children");
                            foreach (JsonElement item in trkProp.Value.EnumerateArray())
                            {
                                string schema = item.TryGetProperty("OTIO_SCHEMA", out JsonElement s) ? s.GetString() ?? "" : "";
                                if (!firstClipModified && (schema == "Clip.1" || schema == "Clip.2"))
                                {
                                    firstClipModified = true;
                                    writer.WriteStartObject();
                                    foreach (JsonProperty cProp in item.EnumerateObject())
                                    {
                                        if (cProp.Name != "effects")
                                        {
                                            cProp.WriteTo(writer);
                                            continue;
                                        }

                                        writer.WriteStartArray("effects");
                                        writer.WriteStartObject();
                                        writer.WriteString("OTIO_SCHEMA", "LinearTimeWarp.1");
                                        writer.WriteString("name", "EditedSpeed");
                                        writer.WriteNumber("time_scalar", 3.0);
                                        writer.WriteStartObject("metadata");
                                        writer.WriteEndObject();
                                        writer.WriteEndObject();
                                        writer.WriteEndArray();
                                    }

                                    writer.WriteEndObject();
                                }
                                else
                                {
                                    item.WriteTo(writer);
                                }
                            }

                            writer.WriteEndArray();
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        string modifiedJson = Encoding.UTF8.GetString(ms.ToArray());
        var report = new InterchangeReport();
        TimelineDraft read = OtioReader.Read(modifiedJson, null, options, report);

        // Manda lo editado: el clip tiene velocidad 3x
        Clip editedClip = read.Sequence.VideoTracks[0].Clips[0];
        Assert.NotNull(editedClip.Speed);
        Assert.Equal(new Rational(3, 1), editedClip.Speed.Rate);

        // El clip cambiado no resucita sus ajustes
        Assert.Null(editedClip.Color);
        Assert.Empty(editedClip.Effects);

        // El informe contiene el aviso exacto
        Assert.Contains(report.Issues, i => i.Message.Contains("Rodaje descartó sus ajustes porque el clip cambió fuera"));
    }

    [Fact]
    public void Simulated_external_edit_deleting_transition_removes_transition_and_keeps_clips()
    {
        TimelineDraft draft = RandomTimelines.Create(7, new RandomTimelineOptions { Crossfades = true, Effects = true });
        var options = new InterchangeOptions();
        IReadOnlyList<InterchangeOutput> outputs = OtioWriter.Write(draft.Project, draft.Sequence, null, options);
        string json = outputs[0].Text;

        // Modificar externamente eliminando todos los Transition.1 del JSON
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            WriteFilteringSchema(doc.RootElement, writer, "Transition.1");
        }

        string modifiedJson = Encoding.UTF8.GetString(ms.ToArray());
        var report = new InterchangeReport();
        TimelineDraft read = OtioReader.Read(modifiedJson, null, options, report);

        // Ya no hay transiciones
        Assert.All(read.Sequence.AllTracks, t => Assert.Empty(t.Transitions));

        // Los clips conservan sus ajustes
        Assert.Contains(read.Sequence.AllTracks.SelectMany(t => t.Clips), c => c.Effects.Count > 0 || c.Color is not null);
    }

    [Fact]
    public void Hostile_ten_thousand_nested_stacks_throws_interchange_exception()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("{\"OTIO_SCHEMA\":\"Stack.1\",\"name\":\"s\",\"children\":[");
        }

        sb.Append("{\"OTIO_SCHEMA\":\"Gap.1\",\"name\":\"g\"}");
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append("]}");
        }

        string json = $"{{\"OTIO_SCHEMA\":\"Timeline.1\",\"name\":\"deep\",\"tracks\":{sb}}}";

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("-25.0")]
    [InlineData("NaN")]
    [InlineData("1e308")]
    public void Hostile_invalid_rate_throws_interchange_exception(string rateValue)
    {
        string json = $$"""
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "InvalidRate",
              "global_start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": {{rateValue}} },
              "tracks": { "OTIO_SCHEMA": "Stack.1", "name": "tracks", "children": [] }
            }
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("1e308")]
    public void Hostile_invalid_time_value_throws_interchange_exception(string value)
    {
        string json = $$"""
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "InvalidTimeValue",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "Gap.1",
                        "name": "Hueco",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": {{value}}, "rate": 24.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 10, "rate": 24.0 }
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        Assert.Throws<InterchangeException>(() =>
            InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport()));
    }

    [Fact]
    public void Hostile_unknown_class_converts_to_gap_with_report()
    {
        string json = """
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "UnknownClass",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "UnknownEffect.99",
                        "name": "Misterio",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": 25.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 50, "rate": 25.0 }
                        }
                      },
                      {
                        "OTIO_SCHEMA": "Clip.1",
                        "name": "ClipAfter",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 0, "rate": 25.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 25, "rate": 25.0 }
                        },
                        "media_reference": {
                          "OTIO_SCHEMA": "ExternalReference.1",
                          "target_url": "file:///C:/Medios/test.mp4"
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var report = new InterchangeReport();
        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), report);

        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Unknown || i.Message.Contains("UnknownEffect.99"));
        Track track = Assert.Single(draft.Sequence.VideoTracks);
        Clip clip = Assert.Single(track.Clips);
        Assert.Equal(Time.FromFrames(50, new Rational(25, 1)), clip.Start);
    }

    [Fact]
    public void Clip1_schema_reads_correctly()
    {
        string json = """
            {
              "OTIO_SCHEMA": "Timeline.1",
              "name": "Clip1Test",
              "tracks": {
                "OTIO_SCHEMA": "Stack.1",
                "name": "tracks",
                "children": [
                  {
                    "OTIO_SCHEMA": "Track.1",
                    "name": "V1",
                    "kind": "Video",
                    "children": [
                      {
                        "OTIO_SCHEMA": "Clip.1",
                        "name": "LegacyClip",
                        "source_range": {
                          "OTIO_SCHEMA": "TimeRange.1",
                          "start_time": { "OTIO_SCHEMA": "RationalTime.1", "value": 10, "rate": 25.0 },
                          "duration": { "OTIO_SCHEMA": "RationalTime.1", "value": 50, "rate": 25.0 }
                        },
                        "media_reference": {
                          "OTIO_SCHEMA": "ExternalReference.1",
                          "name": "LegacyMedia",
                          "target_url": "file:///C:/Medios/legacy.mov"
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var report = new InterchangeReport();
        TimelineDraft draft = InterchangeFiles.ReadDraft(json, InterchangeFormat.Otio, null, new InterchangeOptions(), report);

        Track track = Assert.Single(draft.Sequence.VideoTracks);
        Clip clip = Assert.Single(track.Clips);
        Assert.Equal(Time.FromFrames(0, new Rational(25, 1)), clip.Start);
        Assert.Equal(Time.FromFrames(50, new Rational(25, 1)), clip.Duration);
        Assert.Equal(Time.FromFrames(10, new Rational(25, 1)), clip.SourceIn);
        MediaRef media = Assert.Single(draft.Project.Media);
        Assert.Equal(@"C:\Medios\legacy.mov", media.Path);
        Assert.Equal("LegacyMedia", media.Name);
    }

    private static string StripMetadataRodaje(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            WriteWithoutRodaje(doc.RootElement, writer);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteWithoutRodaje(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.Name == "rodaje")
                {
                    continue;
                }

                writer.WritePropertyName(prop.Name);
                WriteWithoutRodaje(prop.Value, writer);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                WriteWithoutRodaje(item, writer);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }

    private static void WriteFilteringSchema(JsonElement element, Utf8JsonWriter writer, string schemaToFilter)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name);
                WriteFilteringSchema(prop.Value, writer, schemaToFilter);
            }

            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("OTIO_SCHEMA", out JsonElement s) &&
                    s.GetString() == schemaToFilter)
                {
                    continue;
                }

                WriteFilteringSchema(item, writer, schemaToFilter);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
