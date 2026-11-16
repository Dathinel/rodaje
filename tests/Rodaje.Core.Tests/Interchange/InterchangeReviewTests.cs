using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>Los hallazgos de la revisión de bloque de D-062 (ChatGPT, 15-nov) que se arreglaron en el núcleo común y en la EDL.</summary>
public sealed class InterchangeReviewTests
{
    private static readonly Rational Fps25 = new(25, 1);

    private static TimelineDraft ReadEdl(string text, Rational? rate = null, InterchangeReport? report = null) =>
        InterchangeFiles.ReadDraft(text, InterchangeFormat.Edl, null, new InterchangeOptions { FrameRate = rate ?? Fps25 }, report ?? new InterchangeReport());

    [Fact]
    public void Numbers_that_overflow_come_out_as_an_interchange_error()
    {
        const string fcpxml = """
            <fcpxml version="1.10"><resources><format id="r1" frameDuration="1/25s" width="1920" height="1080"/>
            <asset id="a" name="a" start="0s" duration="10s" hasVideo="1" format="r1"><media-rep kind="original-media" src="file:///C:/a.mov"/></asset></resources>
            <library><event name="e"><project name="p"><sequence format="r1" duration="10s" tcStart="0s"><spine>
            <asset-clip ref="a" offset="9223372036854775807/705600000s" start="0s" duration="1/705600000s"/>
            <asset-clip ref="a" offset="0s" start="0s" duration="1/4294967296s"/>
            </spine></sequence></project></event></library></fcpxml>
            """;
        const string edl = "001 A V C 00:00:00:00 00:00:01:00 01:00:00:00 01:00:01:00\n002 A V C 00:00:01:00 00:00:01:00 01:00:01:00 01:00:01:00\n002 B V D 9223372036854775807 00:00:01:00 00:00:02:00 01:00:01:00 01:00:02:00\n";

        Exception? error = Record.Exception(() => InterchangeFiles.ReadDraft(fcpxml, InterchangeFormat.Fcpxml, null, new InterchangeOptions(), new InterchangeReport()));
        Assert.True(error is null or InterchangeException, error?.GetType().Name);
        Exception? edlError = Record.Exception(() => ReadEdl(edl));
        Assert.True(edlError is null or InterchangeException, edlError?.GetType().Name);
    }

    [Fact]
    public void Thousands_of_attributes_count_against_the_xml_budget()
    {
        string xml = "<a " + string.Join(' ', Enumerable.Range(0, 300).Select(i => $"x{i}=\"\"")) + "/>";

        Assert.Throws<InterchangeException>(() => XmlLite.Parse(xml, InterchangeLimits.Default with { MaxObjects = 200 }));
    }

    [Fact]
    public void Text_read_without_a_file_still_has_the_size_budget()
    {
        var options = new InterchangeOptions { Limits = InterchangeLimits.Default with { MaxFileBytes = 1000 } };

        Assert.Throws<InterchangeException>(() => InterchangeFiles.ReadDraft(new string(' ', 2000), InterchangeFormat.Edl, null, options, new InterchangeReport()));
    }

    [Fact]
    public void A_million_garbage_lines_do_not_make_a_million_warnings()
    {
        var report = new InterchangeReport();

        ReadEdl(string.Concat(Enumerable.Repeat("basura\n", 5000)), report: report);

        Assert.InRange(report.Issues.Count, 1, 101);
    }

    [Fact]
    public void A_network_clip_name_is_not_used_as_a_path()
    {
        TimelineDraft draft = ReadEdl("001 A V C 00:00:00:00 00:00:01:00 01:00:00:00 01:00:01:00\n* FROM CLIP NAME: \\\\servidor\\share\\toma.mov\n");

        Assert.Equal("A", draft.Project.Media.Single().Path);
    }

    [Fact]
    public void Drop_frame_wins_over_a_25_fps_sequence_and_colons_still_count_as_drop_frame()
    {
        const string edl = "FCM: DROP FRAME\n001 A V C 00:10:00:00 00:10:00:10 01:00:00;00 01:00:00;10\n";
        var report = new InterchangeReport();

        TimelineDraft draft = ReadEdl(edl, Fps25, report);

        Assert.Equal(Rational.Ntsc30, draft.Sequence.FrameRate);
        Assert.Equal(Time.FromFrames(17_982, Rational.Ntsc30), draft.Sequence.VideoTracks[0].Clips.Single().SourceIn);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Info && i.Message.Contains("drop frame", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clip_past_midnight_is_reported_instead_of_vanishing()
    {
        TimelineDraft draft = RandomTimelines.Create(2);
        draft.Sequence.StartTimecode = SmpteStart(draft.Sequence.FrameRate);
        var report = new InterchangeReport();

        Fidelity.Project(draft.Project, draft.Sequence, InterchangeFormat.Edl, new InterchangeOptions { FrameRate = draft.Sequence.FrameRate, VideoTracks = [0] }, report);

        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Lost && i.Message.Contains("24 h", StringComparison.Ordinal));
    }

    private static string SmpteStart(Rational rate) =>
        Rodaje.Core.Timing.SmpteTimecode.CanDropFrame(rate) ? "23:59:59;00" : "23:59:59:00";

    [Fact]
    public void The_edl_report_covers_every_video_track_it_writes()
    {
        TimelineDraft draft = RandomTimelines.Create(9, new RandomTimelineOptions { VideoTracks = 2 });
        draft.Sequence.VideoTracks[1].Clips.First(c => c.SequenceId is null).Color = "#FF0000";
        foreach (Clip clip in draft.Sequence.VideoTracks[0].Clips)
        {
            clip.Color = null;
        }

        var report = new InterchangeReport();
        Fidelity.Project(draft.Project, draft.Sequence, InterchangeFormat.Edl, new InterchangeOptions { FrameRate = draft.Sequence.FrameRate }, report);

        Assert.Contains(report.Issues, i => i.Message.Contains("color", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reel_name_alone_is_never_taken_for_a_file_of_the_open_project()
    {
        var target = new Project { Media = [new MediaRef { Id = "ya", Name = "TOMA01", Path = "TOMA01", Duration = Time.FromSeconds(100), HasVideo = true }] };

        InterchangeResult result = SequenceBuilder.Build(ReadEdl("001 TOMA01 V C 00:00:00:00 00:00:01:00 01:00:00:00 01:00:01:00\n"), target, null, new SequentialIdGenerator(), new InterchangeReport());

        Assert.Single(result.Media);
        Assert.DoesNotContain(result.Sequence.AllTracks.SelectMany(t => t.Clips), c => c.MediaId == "ya");
    }

    [Fact]
    public void A_disabled_clip_inside_a_compound_does_not_hide_the_one_below()
    {
        Time F(long n) => Time.FromFrames(n, Fps25);
        var media = new MediaRef { Id = "m", Name = "m", Path = @"C:\m.mov", Duration = F(1000), HasVideo = true, FrameRate = Fps25, Width = 10, Height = 10 };
        var inner = new Sequence { Id = "in", Name = "Dentro" };
        inner.VideoTracks.Add(new Track { Id = "b", Kind = TrackKind.Video, Name = "abajo", Clips = [new Clip { Id = "low", MediaId = "m", SourceIn = F(0), SourceOut = F(100) }] });
        inner.VideoTracks.Add(new Track { Id = "a", Kind = TrackKind.Video, Name = "arriba", Clips = [new Clip { Id = "off", MediaId = "m", Start = F(20), SourceIn = F(500), SourceOut = F(540), Enabled = false }] });
        var main = new Sequence { Id = "s", Name = "s" };
        main.VideoTracks.Add(new Track { Id = "v", Kind = TrackKind.Video, Name = "V1", Clips = [new Clip { Id = "comp", SequenceId = "in", SourceIn = F(0), SourceOut = F(100) }] });

        IReadOnlyList<WalkTrack> flat = TimelineWalk.Of(new Project { Media = [media], Sequences = [inner, main] }, main, true, InterchangeLimits.Default, new InterchangeReport());

        WalkClip only = Assert.Single(flat[0].Items.OfType<WalkClip>());
        Assert.Equal(F(100), only.Duration);
    }

    /// <summary>Lo encontró la autoprueba: una pista «Video 1» silenciada volvía como «V1» y sonando, sin aviso.</summary>
    [Fact]
    public void Fcpxml_track_names_and_states_come_back_as_the_rule_says_and_are_reported()
    {
        TimelineDraft draft = RandomTimelines.Create(12);
        draft.Sequence.VideoTracks[0].Name = "Video 1";
        draft.Sequence.AudioTracks[0].Name = "Entrevista";
        draft.Sequence.AudioTracks[0].Muted = true;
        var options = new InterchangeOptions { FrameRate = draft.Sequence.FrameRate };
        var report = new InterchangeReport();

        TimelineDraft expected = Fidelity.Project(draft.Project, draft.Sequence, InterchangeFormat.Fcpxml, options, report);
        string text = InterchangeFiles.Serialize(draft.Project, draft.Sequence, null, InterchangeFormat.Fcpxml, options, new InterchangeReport())[0].Text;
        TimelineDraft read = InterchangeFiles.ReadDraft(text, InterchangeFormat.Fcpxml, null, options, new InterchangeReport());

        Assert.Null(Fidelity.Difference(expected, read));
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Track && i.Name == "Entrevista");
    }
}
