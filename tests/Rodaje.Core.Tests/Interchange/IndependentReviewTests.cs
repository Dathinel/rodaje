using System.Text.Json.Nodes;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Entradas nuevas de la revisión independiente de D-062 (16-nov).</summary>
public sealed class IndependentReviewTests
{
    private static (TimelineDraft Draft, JsonNode Root) WriteOtio(int seed)
    {
        TimelineDraft draft = RandomTimelines.Create(seed);
        var options = new InterchangeOptions { FrameRate = draft.Sequence.FrameRate };
        string text = InterchangeFiles.Serialize(draft.Project, draft.Sequence, null, InterchangeFormat.Otio, options, new InterchangeReport())[0].Text;
        return (draft, JsonNode.Parse(text)!);
    }

    private static InterchangeResult Import(string text, InterchangeFormat format, Project? target = null, IIdGenerator? ids = null, Rational? rate = null) =>
        InterchangeFiles.Parse(text, format, null, target ?? new Project { Id = "t" }, null, new InterchangeOptions { FrameRate = rate }, ids ?? new SequentialIdGenerator());

    [Fact]
    public void A_damaged_rodaje_block_with_a_matching_fingerprint_falls_back_to_standard_otio()
    {
        (TimelineDraft draft, JsonNode root) = WriteOtio(3);
        root["metadata"]!["rodaje"]!["media"] = "roto";

        InterchangeResult result = Import(root.ToJsonString(), InterchangeFormat.Otio);

        Assert.Equal(draft.Sequence.AllTracks.Sum(t => t.Clips.Count), result.Sequence.AllTracks.Sum(t => t.Clips.Count));
        Assert.Contains(result.Report.Issues, i => i.Message.Contains("dañado", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rodaje_block_that_points_to_missing_media_falls_back_to_standard_otio()
    {
        (_, JsonNode root) = WriteOtio(4);
        foreach (JsonNode? track in root["metadata"]!["rodaje"]!["sequence"]!["videoTracks"]!.AsArray())
        {
            foreach (JsonNode? clip in track!["clips"]!.AsArray())
            {
                clip!["mediaId"] = "no-existe";
            }
        }

        InterchangeResult result = Import(root.ToJsonString(), InterchangeFormat.Otio);

        Assert.Contains(result.Report.Issues, i => i.Message.Contains("dañado", StringComparison.Ordinal));
        Assert.All(result.Sequence.AllTracks.SelectMany(t => t.Clips), c => Assert.Contains(result.Media, m => m.Id == c.MediaId));
    }

    [Fact]
    public void A_damaged_clip_block_only_drops_that_clips_settings()
    {
        (TimelineDraft draft, JsonNode root) = WriteOtio(5);
        root["metadata"]!["rodaje"]!["huella"] = "editado fuera";
        JsonNode firstClip = FirstClip(root["tracks"]!)!;
        firstClip["metadata"]!["rodaje"]!["clip"] = "roto";

        InterchangeResult result = Import(root.ToJsonString(), InterchangeFormat.Otio);

        Assert.Equal(draft.Sequence.AllTracks.Sum(t => t.Clips.Count), result.Sequence.AllTracks.Sum(t => t.Clips.Count));
        Assert.Contains(result.Report.Issues, i => i.Message.Contains("dañados", StringComparison.Ordinal));
    }

    [Fact]
    public void Drop_frame_edl_across_the_minute_keeps_two_frames()
    {
        const string edl = "TITLE: x\nFCM: DROP FRAME\n\n001  TOMA01   V     C        00:00:59;28 00:01:00;02 01:00:00;00 01:00:00;02\n";
        TimelineDraft draft = InterchangeFiles.ReadDraft(edl, InterchangeFormat.Edl, null, new InterchangeOptions { FrameRate = new Rational(25, 1) }, new InterchangeReport());

        Clip clip = draft.Sequence.VideoTracks[0].Clips.Single();
        Assert.Equal(Rational.Ntsc30, draft.Sequence.FrameRate);
        Assert.Equal(Time.FromFrames(2, Rational.Ntsc30), clip.Duration);
        Assert.Equal(Time.FromFrames(1798, Rational.Ntsc30), clip.SourceIn);
    }

    [Fact]
    public void Importing_the_same_otio_twice_into_one_project_stays_valid()
    {
        (_, JsonNode root) = WriteOtio(6);
        string text = root.ToJsonString();
        var target = new Project { Id = "t" };
        var ids = new SequentialIdGenerator();
        for (int n = 0; n < 2; n++)
        {
            InterchangeResult result = Import(text, InterchangeFormat.Otio, target, ids);
            target.Media.AddRange(result.Media);
            target.Sequences.AddRange(result.Compounds);
            target.Sequences.Add(result.Sequence);
        }

        Assert.Empty(ModelValidator.Validate(target));
    }

    [Fact]
    public void A_cycle_of_fcpxml_ref_clips_is_an_interchange_error()
    {
        const string fcpxml = """
            <fcpxml version="1.10"><resources><format id="r1" frameDuration="1/25s" width="1920" height="1080"/>
            <media id="m1" name="uno"><sequence format="r1" duration="2s"><spine><ref-clip ref="m2" offset="0s" duration="2s"/></spine></sequence></media>
            <media id="m2" name="dos"><sequence format="r1" duration="2s"><spine><ref-clip ref="m1" offset="0s" duration="2s"/></spine></sequence></media>
            </resources><library><event name="e"><project name="p"><sequence format="r1" duration="2s" tcStart="0s"><spine>
            <ref-clip ref="m1" offset="0s" duration="2s"/>
            </spine></sequence></project></event></library></fcpxml>
            """;

        Assert.Throws<InterchangeException>(() => Import(fcpxml, InterchangeFormat.Fcpxml));
    }

    [Fact]
    public void An_edl_dissolve_longer_than_its_clips_does_not_break_the_import()
    {
        const string edl = "TITLE: x\nFCM: NON-DROP FRAME\n\n001  A  V  C        00:00:00:00 00:00:01:00 01:00:00:00 01:00:01:00\n001  B  V  D    100 00:00:00:00 00:00:01:00 01:00:01:00 01:00:02:00\n";

        Exception? error = Record.Exception(() => Import(edl, InterchangeFormat.Edl, rate: new Rational(25, 1)));

        Assert.True(error is null or InterchangeException, error?.ToString());
    }

    [Fact]
    public void Disabling_a_clip_outside_rodaje_comes_back_disabled()
    {
        (TimelineDraft draft, JsonNode root) = WriteOtio(7);
        int before = draft.Sequence.AllTracks.SelectMany(t => t.Clips).Count(c => !c.Enabled);
        JsonNode clip = FirstClip(root["tracks"]!)!;
        Assert.True(clip["enabled"]?.GetValue<bool>() ?? true);
        clip["enabled"] = false;

        TimelineDraft read = InterchangeFiles.ReadDraft(root.ToJsonString(), InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport());

        Assert.Equal(before + 1, read.Project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips).Count(c => !c.Enabled));
    }

    [Fact]
    public void Otio_source_ranges_are_in_the_media_timecode()
    {
        const string otio = """
            {"OTIO_SCHEMA":"Timeline.1","name":"t","global_start_time":{"OTIO_SCHEMA":"RationalTime.1","value":0,"rate":25},
             "tracks":{"OTIO_SCHEMA":"Stack.1","name":"tracks","children":[{"OTIO_SCHEMA":"Track.1","name":"V1","kind":"Video","children":[
              {"OTIO_SCHEMA":"Clip.2","name":"a","source_range":{"OTIO_SCHEMA":"TimeRange.1",
                "start_time":{"OTIO_SCHEMA":"RationalTime.1","value":90250,"rate":25},"duration":{"OTIO_SCHEMA":"RationalTime.1","value":50,"rate":25}},
               "active_media_reference_key":"DEFAULT_MEDIA","media_references":{"DEFAULT_MEDIA":{"OTIO_SCHEMA":"ExternalReference.1","name":"a",
                "target_url":"file:///C:/medios/a.mov","available_range":{"OTIO_SCHEMA":"TimeRange.1",
                "start_time":{"OTIO_SCHEMA":"RationalTime.1","value":90000,"rate":25},"duration":{"OTIO_SCHEMA":"RationalTime.1","value":1000,"rate":25}}}}}]}]}}
            """;

        TimelineDraft read = InterchangeFiles.ReadDraft(otio, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport());
        Clip clip = read.Sequence.VideoTracks[0].Clips.Single();
        Assert.Equal(Time.FromFrames(250, new Rational(25, 1)), clip.SourceIn);

        MediaRef media = read.Project.Media.Single();
        Assert.Equal("01:00:00:00", media.Timecode);
        JsonNode written = JsonNode.Parse(InterchangeFiles.Serialize(read.Project, read.Sequence, null, InterchangeFormat.Otio, new InterchangeOptions(), new InterchangeReport())[0].Text)!;
        Assert.Equal(90250, FirstClip(written["tracks"]!)!["source_range"]!["start_time"]!["value"]!.GetValue<double>());
    }

    [Fact]
    public void Two_otio_stacks_with_the_same_name_keep_their_own_clips()
    {
        static string Stack(string file) => $$$$"""
            {"OTIO_SCHEMA":"Stack.1","name":"Compuesto","source_range":{"OTIO_SCHEMA":"TimeRange.1","start_time":{"OTIO_SCHEMA":"RationalTime.1","value":0,"rate":25},"duration":{"OTIO_SCHEMA":"RationalTime.1","value":25,"rate":25}},
             "children":[{"OTIO_SCHEMA":"Track.1","name":"V1","kind":"Video","children":[{"OTIO_SCHEMA":"Clip.2","name":"{{{{file}}}}",
              "source_range":{"OTIO_SCHEMA":"TimeRange.1","start_time":{"OTIO_SCHEMA":"RationalTime.1","value":0,"rate":25},"duration":{"OTIO_SCHEMA":"RationalTime.1","value":25,"rate":25}},
              "active_media_reference_key":"DEFAULT_MEDIA","media_references":{"DEFAULT_MEDIA":{"OTIO_SCHEMA":"ExternalReference.1","name":"{{{{file}}}}","target_url":"file:///C:/medios/{{{{file}}}}"}}}]}]}
            """;
        string otio = $$$"""
            {"OTIO_SCHEMA":"Timeline.1","name":"t","tracks":{"OTIO_SCHEMA":"Stack.1","name":"tracks","children":[{"OTIO_SCHEMA":"Track.1","name":"V1","kind":"Video","children":[{{{Stack("A.mov")}}},{{{Stack("B.mov")}}}]}]}}
            """;

        TimelineDraft read = InterchangeFiles.ReadDraft(otio, InterchangeFormat.Otio, null, new InterchangeOptions(), new InterchangeReport());

        List<string> files = [.. read.Sequence.VideoTracks[0].Clips
            .Select(c => read.Project.FindSequence(c.SequenceId!)!.VideoTracks[0].Clips.Single())
            .Select(c => Path.GetFileName(read.Project.FindMedia(c.MediaId)!.Path))];
        Assert.Equal(["A.mov", "B.mov"], files);
    }

    [Fact]
    public void Otio_with_chosen_tracks_does_not_bring_back_the_others()
    {
        TimelineDraft draft = RandomTimelines.Create(8, new RandomTimelineOptions { Compounds = false });
        var options = new InterchangeOptions { FrameRate = draft.Sequence.FrameRate, VideoTracks = [0], IncludeAudio = false };
        string text = InterchangeFiles.Serialize(draft.Project, draft.Sequence, null, InterchangeFormat.Otio, options, new InterchangeReport())[0].Text;

        TimelineDraft read = InterchangeFiles.ReadDraft(text, InterchangeFormat.Otio, null, options, new InterchangeReport());

        Assert.DoesNotContain(read.Sequence.AudioTracks, t => t.Clips.Count > 0);
        Assert.Single(read.Sequence.VideoTracks, t => t.Clips.Count > 0);
    }

    [Fact]
    public void A_disabled_transform_does_not_move_the_clip_in_fcpxml()
    {
        var rate = new Rational(25, 1);
        var media = new MediaRef { Id = "m", Name = "a", Path = @"C:\medios\a.mov", Duration = Time.FromFrames(100, rate), HasVideo = true, FrameRate = rate, Width = 1920, Height = 1080 };
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromFrames(50, rate) };
        clip.Effects.Add(new EffectInstance
        {
            Id = "e",
            Type = BuiltInEffects.Transform,
            Enabled = false,
            Parameters = { [BuiltInEffects.TransformParameters.X] = new Parameter { Value = 500 } },
        });
        var sequence = new Sequence { Id = "s", Name = "s", FrameRate = rate, Width = 1920, Height = 1080, VideoTracks = [new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Clips = [clip] }] };
        var project = new Project { Id = "p", Media = [media], Sequences = [sequence] };
        var options = new InterchangeOptions { FrameRate = rate };
        var report = new InterchangeReport();

        string text = InterchangeFiles.Serialize(project, sequence, null, InterchangeFormat.Fcpxml, options, report)[0].Text;
        TimelineDraft read = InterchangeFiles.ReadDraft(text, InterchangeFormat.Fcpxml, null, options, new InterchangeReport());

        Assert.DoesNotContain("adjust-transform", text, StringComparison.Ordinal);
        Assert.Empty(read.Sequence.VideoTracks[0].Clips.Single().Effects);
        Assert.Contains(report.Issues, i => i.Message.Contains("desactivado", StringComparison.Ordinal));
    }

    private static JsonNode? FirstClip(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["OTIO_SCHEMA"]?.GetValue<string>().StartsWith("Clip.", StringComparison.Ordinal) == true && obj["metadata"]?["rodaje"]?["clip"] is not null)
            {
                return obj;
            }

            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                if (property.Value is not null && FirstClip(property.Value) is { } found)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not null && FirstClip(item) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
