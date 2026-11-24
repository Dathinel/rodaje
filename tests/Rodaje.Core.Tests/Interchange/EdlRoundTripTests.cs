using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Edl;
using Rodaje.Core.Model;
using Rational = Rodaje.Core.Timing.Rational;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Core.Tests.Interchange;

/// <summary>EDL CMX 3600 (fase F4a de D-062): ida y vuelta por propiedad, muestras escritas a mano y entradas hostiles.</summary>
public sealed class EdlRoundTripTests
{
    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 1; seed <= 500; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Every_video_track_comes_back_as_the_rule_says(int seed)
    {
        TimelineDraft draft = RandomTimelines.Create(seed, new RandomTimelineOptions { Titles = seed % 5 == 0, Effects = seed % 7 == 0 });
        Sequence sequence = draft.Sequence;
        for (int track = 0; track < sequence.VideoTracks.Count; track++)
        {
            var options = new InterchangeOptions { FrameRate = sequence.FrameRate, VideoTracks = [track] };
            InterchangeOutput output = Assert.Single(EdlWriter.Write(draft.Project, sequence, null, options));
            TimelineDraft expected = Fidelity.Project(draft.Project, sequence, InterchangeFormat.Edl, options, new InterchangeReport());

            TimelineDraft read = EdlReader.Read(output.Text, null, options, new InterchangeReport());

            Assert.True(Fidelity.Difference(expected, read) is null, $"Semilla {seed}, pista {track}: {Fidelity.Difference(expected, read)}\n{output.Text}");
        }
    }

    [Fact]
    public void Writing_all_tracks_gives_one_file_per_video_track_with_audio_only_in_the_first()
    {
        TimelineDraft draft = RandomTimelines.Create(4);
        var options = new InterchangeOptions { FrameRate = draft.Sequence.FrameRate };

        IReadOnlyList<InterchangeOutput> outputs = EdlWriter.Write(draft.Project, draft.Sequence, null, options);

        Assert.Equal(["", "_V2"], outputs.Select(o => o.Suffix));
        Assert.Contains(" A  ", outputs[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(" A  ", outputs[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_video_track_without_clips_does_not_get_a_file_of_its_own()
    {
        TimelineDraft draft = RandomTimelines.Create(4);
        draft.Sequence.VideoTracks.Add(new Track { Id = "vacia", Kind = TrackKind.Video, Name = "Vacía" });
        var options = new InterchangeOptions { FrameRate = draft.Sequence.FrameRate };

        IReadOnlyList<InterchangeOutput> outputs = EdlWriter.Write(draft.Project, draft.Sequence, null, options);

        Assert.Equal(["", "_V2"], outputs.Select(o => o.Suffix));
        Assert.All(outputs, o => Assert.Contains("001", o.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_first_track_is_skipped_too_and_the_busy_one_takes_the_main_name()
    {
        TimelineDraft draft = RandomTimelines.Create(4);
        Sequence sequence = draft.Sequence;
        sequence.VideoTracks.Insert(0, new Track { Id = "vacia", Kind = TrackKind.Video, Name = "Vacía" });
        var options = new InterchangeOptions { FrameRate = sequence.FrameRate, IncludeAudio = false };

        IReadOnlyList<InterchangeOutput> outputs = EdlWriter.Write(draft.Project, sequence, null, options);

        Assert.Equal("", outputs[0].Suffix);
        Assert.All(outputs, o => Assert.Contains("001", o.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Without_any_event_one_empty_list_is_still_written()
    {
        TimelineDraft draft = RandomTimelines.Create(4);
        foreach (Track track in draft.Sequence.VideoTracks.Concat(draft.Sequence.AudioTracks))
        {
            track.Clips.Clear();
        }

        IReadOnlyList<InterchangeOutput> outputs = EdlWriter.Write(draft.Project, draft.Sequence, null, new InterchangeOptions { FrameRate = draft.Sequence.FrameRate });

        Assert.Equal([""], outputs.Select(o => o.Suffix));
    }

    [Fact]
    public void A_hand_written_list_with_cut_dissolve_speed_freeze_and_marker_reads_as_expected()
    {
        const string edl = """
            TITLE: Muestra
            FCM: NON-DROP FRAME

            001  TOMA01   V     C        00:00:10:00 00:00:12:00 01:00:00:00 01:00:02:00
            * FROM CLIP NAME: Toma 01
            * SOURCE FILE: C:\material\toma 01.mov
            * LOC: 01:00:01:00 RED     Entra la música
            002  TOMA01   V     C        00:00:12:00 00:00:12:00 01:00:02:00 01:00:02:00
            002  TOMA02   V     D    025 00:00:04:00 00:00:07:00 01:00:02:00 01:00:05:00
            * FROM CLIP NAME: Toma 02
            003  TOMA03   V     C        00:00:00:00 00:00:02:00 01:00:06:00 01:00:08:00
            M2   TOMA03       050.0                00:00:00:00
            004  TOMA03   V     C        00:00:05:00 00:00:06:00 01:00:08:00 01:00:09:00
            M2   TOMA03       000.0                00:00:05:00
            005  TOMA01   A     C        00:00:10:00 00:00:12:00 01:00:00:00 01:00:02:00
            """;
        var rate = new Rational(25, 1);
        Time F(long frames) => Time.FromFrames(frames, rate);

        TimelineDraft draft = EdlReader.Read(edl, null, new InterchangeOptions { FrameRate = rate }, new InterchangeReport());

        Sequence sequence = draft.Sequence;
        Assert.Equal(("Muestra", "01:00:00:00"), (sequence.Name, sequence.StartTimecode));
        List<Clip> video = sequence.VideoTracks[0].Clips;
        Assert.Equal([F(0), F(50), F(150), F(200)], video.Select(c => c.Start));
        Assert.Equal([F(250), F(100), F(0), F(125)], video.Select(c => c.SourceIn));
        Assert.Equal(F(50), video[0].Duration);
        Assert.Equal((new Rational(2, 1), F(100)), (video[2].Speed!.Rate, video[2].SourceOut));
        Assert.True(video[3].Speed!.Freeze);
        TransitionInstance dissolve = Assert.Single(sequence.VideoTracks[0].Transitions);
        Assert.Equal((video[0].Id, video[1].Id, F(25), TransitionAlignment.After), (dissolve.From, dissolve.To, dissolve.Duration, dissolve.Alignment));
        Assert.Equal((F(25), "Entra la música"), (sequence.Markers.Single().Time, sequence.Markers.Single().Name));
        Clip audio = sequence.AudioTracks.Single().Clips.Single();
        Assert.Equal(video[0].MediaId, audio.MediaId);
        MediaRef toma01 = draft.Project.FindMedia(video[0].MediaId)!;
        Assert.Equal(("Toma 01", "TOMA01", F(300), true, true), (toma01.Name, toma01.Reel, toma01.Duration, toma01.HasVideo, toma01.HasAudio));
        Assert.Equal(0, toma01.Width); // provisional: se resuelve al reenlazar
        Assert.Equal("TOMA03", draft.Project.FindMedia(video[2].MediaId)!.Path);

        string written = EdlWriter.Write(draft.Project, sequence, null, new InterchangeOptions { FrameRate = rate, VideoTracks = [0] })[0].Text;
        string[] lines = [.. written.Split('\n').Select(l => l.TrimEnd('\r')).Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(t => t.All(char.IsAsciiDigit)).Take(99)))];
        // Los números de evento salen en orden de grabación (el audio de 001 es el 002): se comparan las líneas sin ellos.
        Assert.Contains("TOMA01 V C 00:00:12:00 00:00:12:00 01:00:02:00 01:00:02:00", lines);
        Assert.Contains("TOMA02 V D 025 00:00:04:00 00:00:07:00 01:00:02:00 01:00:05:00", lines);
        Assert.Contains("M2 TOMA03 050.0 00:00:00:00", lines);
        Assert.Contains("M2 TOMA03 000.0 00:00:05:00", lines);
        Assert.Contains("* LOC: 01:00:01:00 RED Entra la música", lines);
        Assert.Contains("TOMA01 A C 00:00:10:00 00:00:12:00 01:00:00:00 01:00:02:00", lines);
    }

    [Fact]
    public void Drop_frame_reads_at_29_97_with_the_skipped_numbers()
    {
        const string edl = """
            TITLE: DF
            FCM: DROP FRAME
            001  A001     V     C        00:01:00;02 00:01:00;12 00:59:59;00 00:59:59;10
            """;

        TimelineDraft draft = EdlReader.Read(edl, null, new InterchangeOptions(), new InterchangeReport());

        Assert.Equal(Rational.Ntsc30, draft.Sequence.FrameRate);
        Assert.Equal("00:00:00;00", draft.Sequence.StartTimecode);
        Clip clip = draft.Sequence.VideoTracks[0].Clips.Single();
        Assert.Equal(Time.FromFrames(1800, Rational.Ntsc30), clip.SourceIn);
        Assert.Equal(Time.FromFrames(10, Rational.Ntsc30), clip.Duration);
    }

    [Fact]
    public void The_report_names_what_does_not_fit()
    {
        TimelineDraft draft = RandomTimelines.Create(5, new RandomTimelineOptions { Titles = true, Effects = true, AudioTracks = 3 });
        var report = new InterchangeReport();

        Fidelity.Project(draft.Project, draft.Sequence, InterchangeFormat.Edl, new InterchangeOptions { FrameRate = draft.Sequence.FrameRate }, report);

        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Audio && i.Severity == IssueSeverity.Lost);
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Track);
    }

    [Fact]
    public void Garbage_lines_bad_timecodes_and_a_zero_rate_do_not_break_the_reader()
    {
        const string edl = "TITLE: x\nesto no es nada\n001 R V C 99:99:99:99 00:00:01:00 01:00:00:00 01:00:01:00\n002 R V C 00:00:00:00\n* LOC: nada\n";
        var report = new InterchangeReport();

        TimelineDraft draft = EdlReader.Read(edl, null, new InterchangeOptions { FrameRate = new Rational(25, 1) }, report);

        Assert.Empty(draft.Sequence.AllTracks.SelectMany(t => t.Clips));
        Assert.True(report.Issues.Count >= 2);
        Assert.Throws<InterchangeException>(() => EdlReader.Read("", null, new InterchangeOptions { FrameRate = new Rational(1, 100) }, new InterchangeReport()));
    }

    [Fact]
    public void A_hundred_thousand_events_stop_at_the_budget()
    {
        var text = new System.Text.StringBuilder("TITLE: grande\n");
        for (int i = 0; i < 100_000; i++)
        {
            text.Append("001  R  V  C  00:00:00:00 00:00:00:01 01:00:00:00 01:00:00:01\n");
        }

        Assert.Throws<InterchangeException>(() => EdlReader.Read(text.ToString(), null, new InterchangeOptions { FrameRate = new Rational(25, 1), Limits = InterchangeLimits.Default with { MaxObjects = 50_000 } }, new InterchangeReport()));
    }
}
