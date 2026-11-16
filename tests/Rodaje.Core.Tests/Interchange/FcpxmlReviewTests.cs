using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Fcpxml;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Interchange;

public sealed class FcpxmlReviewTests
{
    private static TimelineDraft Read(string story, string assetStart = "0s", InterchangeReport? report = null) =>
        FcpxmlReader.Read($"""
            <fcpxml version="1.10"><resources>
              <format id="f" frameDuration="1/25s" width="1920" height="1080"/>
              <asset id="a" name="Plano" start="{assetStart}" duration="200s" hasVideo="1" format="f">
                <media-rep kind="original-media" src="file:///C:/Medios/Plano.mov"/>
              </asset>
            </resources><project name="Revisión"><sequence format="f" tcStart="0s"><spine>
            {story}
            </spine></sequence></project></fcpxml>
            """, null, new InterchangeOptions(), report ?? new InterchangeReport());

    private static (Project Project, Sequence Sequence, Clip Clip) Model()
    {
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.FromSeconds(10), SourceIn = Time.FromSeconds(100), SourceOut = Time.FromSeconds(110) };
        var sequence = new Sequence { Id = "s", Name = "Revisión", FrameRate = Rational.Fps25,
            VideoTracks = [new Track { Id = "v", Name = "V1", Kind = TrackKind.Video, Clips = [clip] }] };
        var project = new Project { Id = "p", Sequences = [sequence], Media = [new MediaRef
        {
            Id = "m", Name = "Plano", Path = @"C:\Medios\Plano.mov", Duration = Time.FromSeconds(200),
            HasVideo = true, FrameRate = Rational.Fps25, Width = 1920, Height = 1080, AudioChannels = 2, SampleRate = 48000,
        }] };
        return (project, sequence, clip);
    }

    private static XmlNode Write(Project project, Sequence sequence, InterchangeOptions? options = null, string? directory = null) =>
        XmlLite.Parse(FcpxmlWriter.Write(project, sequence, directory, options ?? new InterchangeOptions())[0].Text, InterchangeLimits.Default);

    private static XmlNode Spine(XmlNode root) => root.Element("library")!.Element("event")!.Element("project")!.Element("sequence")!.Element("spine")!;

    private static TimelineDraft Projected(Project project, Sequence sequence, InterchangeReport report, InterchangeOptions? options = null) =>
        FcpxmlFidelity.Project(project, sequence, options ?? new InterchangeOptions(), report);

    private static void EqualRoundTrip(Project project, Sequence sequence, InterchangeOptions? options = null)
    {
        options ??= new InterchangeOptions();
        var expected = Projected(project, sequence, new InterchangeReport(), options);
        var actual = FcpxmlReader.Read(FcpxmlWriter.Write(project, sequence, null, options)[0].Text, null, options, new InterchangeReport());
        Assert.Null(Fidelity.Difference(expected, actual));
    }

    [Fact]
    public void Asset_start_is_subtracted_on_read()
    {
        var draft = Read("""<asset-clip ref="a" offset="0s" start="3610s" duration="5s"/>""", "3600s");
        var clip = Assert.Single(draft.Sequence.VideoTracks[0].Clips);
        Assert.Equal(Time.FromSeconds(10), clip.SourceIn);
        Assert.Equal(Time.FromSeconds(15), clip.SourceOut);
    }

    [Fact]
    public void Asset_start_is_added_on_write()
    {
        var (project, sequence, clip) = Model();
        project.Media[0].Timecode = "01:00:00:00";
        clip.SourceIn = Time.FromSeconds(10);
        clip.SourceOut = Time.FromSeconds(15);
        Assert.Equal("3610s", Spine(Write(project, sequence)).Element("asset-clip")!["start"]);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Connected_offsets_use_parent_local_time_recursively()
    {
        var draft = Read("""
            <asset-clip ref="a" offset="10s" start="100s" duration="10s">
              <asset-clip ref="a" lane="1" offset="102s" start="50s" duration="4s">
                <asset-clip ref="a" lane="2" offset="51s" start="0s" duration="1s"/>
              </asset-clip>
              <asset-clip ref="a" lane="-1" offset="103s" start="20s" duration="4s">
                <asset-clip ref="a" lane="-2" offset="21s" start="0s" duration="1s"/>
              </asset-clip>
            </asset-clip>
            """);
        Assert.Equal(Time.FromSeconds(12), draft.Sequence.VideoTracks[1].Clips[0].Start);
        Assert.Equal(Time.FromSeconds(13), draft.Sequence.VideoTracks[2].Clips[0].Start);
        Assert.Equal(Time.FromSeconds(13), draft.Sequence.AudioTracks[0].Clips[0].Start);
        Assert.Equal(Time.FromSeconds(14), draft.Sequence.AudioTracks[1].Clips[0].Start);
    }

    [Fact]
    public void Gap_local_start_applies_to_connected_clips_and_markers()
    {
        var draft = Read("""
            <gap offset="10s" start="100s" duration="10s">
              <asset-clip ref="a" lane="1" offset="102s" start="0s" duration="4s"/>
              <marker start="103s" value="Marca"/>
            </gap>
            """);
        Assert.Equal(Time.FromSeconds(12), draft.Sequence.VideoTracks[0].Clips[0].Start);
        Assert.Equal(Time.FromSeconds(13), draft.Sequence.Markers[0].Time);
    }

    [Fact]
    public void Connected_offsets_are_written_in_parent_time()
    {
        var (project, sequence, _) = Model();
        sequence.VideoTracks.Add(new Track { Id = "v2", Name = "V2", Kind = TrackKind.Video,
            Clips = [new Clip { Id = "child", MediaId = "m", Start = Time.FromSeconds(12), SourceOut = Time.FromSeconds(2) }] });
        var parent = Spine(Write(project, sequence)).Element("asset-clip")!;
        Assert.Equal("102s", parent.Element("asset-clip")!["offset"]);
        EqualRoundTrip(project, sequence);
    }

    [Theory]
    [InlineData(false, 102, 12)]
    [InlineData(true, 104, 12)]
    public void Markers_are_read_in_parent_source_time(bool retimed, int local, int expected)
    {
        string map = retimed ? """<timeMap><timept time="100s" value="100s" interp="linear"/><timept time="110s" value="120s" interp="linear"/></timeMap>""" : "";
        var draft = Read($"""<asset-clip ref="a" offset="10s" start="100s" duration="10s">{map}<marker start="{local}s" value="Marca"/></asset-clip>""");
        Assert.Equal(Time.FromSeconds(expected), Assert.Single(draft.Sequence.Markers).Time);
    }

    [Theory]
    [InlineData(false, "102s")]
    [InlineData(true, "104s")]
    public void Markers_are_written_through_the_source_map(bool retimed, string expected)
    {
        var (project, sequence, clip) = Model();
        if (retimed)
        {
            clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
            clip.SourceOut = Time.FromSeconds(120);
            clip.Duration = Time.FromSeconds(10);
        }
        sequence.Markers.Add(new Marker { Id = "mk", Name = "Marca", Time = Time.FromSeconds(12) });
        Assert.Equal(expected, Spine(Write(project, sequence)).Element("asset-clip")!.Element("marker")!["start"]);
        EqualRoundTrip(project, sequence);
    }

    [Theory]
    [InlineData("120s", "140s", 130, 140, false, false)]
    [InlineData("140s", "120s", 120, 130, true, false)]
    [InlineData("123s", "123s", 123, 123, false, true)]
    public void Time_map_anchors_source_at_clip_start(string first, string last, int expectedIn, int expectedOut, bool reverse, bool freeze)
    {
        var draft = Read($"""
            <asset-clip ref="a" offset="10s" start="105s" duration="5s"><timeMap>
              <timept time="100s" value="{first}" interp="linear"/>
              <timept time="110s" value="{last}" interp="linear"/>
            </timeMap></asset-clip>
            """);
        var clip = draft.Sequence.VideoTracks[0].Clips[0];
        // Al entrar a mitad del mapa: 130→140, 130→120, o el cuadro 123 congelado.
        Assert.Equal(Time.FromSeconds(expectedIn), clip.SourceIn);
        Assert.Equal(Time.FromSeconds(expectedOut) + (freeze ? Time.FromTicks(1) : Time.Zero), clip.SourceOut);
        Assert.Equal(reverse, clip.Speed!.Reverse);
        Assert.Equal(freeze, clip.Speed.Freeze);
    }

    [Fact]
    public void Freeze_writer_uses_the_frozen_source_value_and_valid_time_attributes()
    {
        var (project, sequence, clip) = Model();
        clip.Speed = new ClipSpeed { Freeze = true };
        clip.SourceIn = Time.FromSeconds(123);
        clip.SourceOut = clip.SourceIn + Time.FromTicks(1);
        clip.Duration = Time.FromSeconds(5);
        var points = Spine(Write(project, sequence)).Element("asset-clip")!.Element("timeMap")!.Elements("timept").ToList();
        Assert.Equal("123s", points[0]["value"]);
        Assert.Equal("123s", points[1]["value"]);
        Assert.Equal("123s", points[0]["time"]);
        Assert.Equal("128s", points[1]["time"]);
        Assert.Null(points[0]["start"]);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Freeze_uses_an_unretimed_container_for_connections_and_markers()
    {
        var (project, sequence, clip) = Model();
        clip.Speed = new ClipSpeed { Freeze = true };
        clip.SourceOut = clip.SourceIn + Time.FromTicks(1);
        clip.Duration = Time.FromSeconds(10);
        sequence.VideoTracks.Add(new Track { Id = "v2", Name = "V2", Kind = TrackKind.Video,
            Clips = [new Clip { Id = "child", MediaId = "m", Start = Time.FromSeconds(12), SourceOut = Time.FromSeconds(2) }] });
        sequence.Markers.Add(new Marker { Id = "mk", Name = "Marca", Time = Time.FromSeconds(13) });
        var container = Spine(Write(project, sequence)).Element("clip");
        Assert.NotNull(container);
        Assert.Equal("0s", container["start"]);
        Assert.Equal("2s", Assert.Single(container.Elements("asset-clip"), c => c["lane"] == "1")["offset"]);
        Assert.Equal("3s", container.Element("marker")!["start"]);
        var actual = FcpxmlReader.Read(FcpxmlWriter.Write(project, sequence, null, new InterchangeOptions())[0].Text, null, new InterchangeOptions(), new InterchangeReport());
        Assert.Equal(Time.FromSeconds(12), actual.Sequence.VideoTracks[1].Clips[0].Start);
        Assert.Equal(Time.FromSeconds(13), actual.Sequence.Markers[0].Time);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Speed_curve_is_replaced_by_its_mean_and_report_identifies_clip_and_time()
    {
        var (project, sequence, clip) = Model();
        clip.Speed = new ClipSpeed { Curve = new SpeedCurve { Span = Time.FromSeconds(10), Points = [new SpeedPoint(0, 100), new SpeedPoint(10000, 300)] } };
        clip.SourceOut = Time.FromSeconds(120);
        clip.Duration = Time.FromSeconds(10);
        var report = new InterchangeReport();
        var projected = Projected(project, sequence, report);
        Assert.Equal(new Rational(2, 1), projected.Sequence.VideoTracks[0].Clips[0].Speed!.Rate);
        var issue = Assert.Single(report.Issues, i => i.Kind == IssueKind.Speed);
        Assert.Contains("la curva se cambia por su velocidad media", issue.Message, StringComparison.Ordinal);
        Assert.Equal("c", issue.ClipId);
        Assert.Equal(Time.FromSeconds(10), issue.Time);
        Assert.Contains("01:00:10:00", report.ToText(t => SmpteTimecode.FromTime(t + Time.FromSeconds(3600), Rational.Fps25, false).Format()), StringComparison.Ordinal);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Mean_speed_keeps_rational_components_larger_than_int32()
    {
        var (project, sequence, clip) = Model();
        clip.Speed = new ClipSpeed { Curve = new SpeedCurve { Span = Time.FromSeconds(10), Points = [new SpeedPoint(0, 50), new SpeedPoint(10000, 50)] } };
        clip.SourceOut = clip.SourceIn + Time.FromTicks(3528000001);
        clip.Duration = Time.FromSeconds(10);
        var projected = Projected(project, sequence, new InterchangeReport());
        Assert.Equal(new Rational(3528000001, 7056000000), projected.Sequence.VideoTracks[0].Clips[0].Speed!.Rate);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Imported_speed_curve_report_identifies_clip_and_time()
    {
        var report = new InterchangeReport();
        var draft = Read("""
            <asset-clip ref="a" name="Curva" offset="10s" start="0s" duration="10s"><timeMap>
              <timept time="0s" value="0s" interp="linear"/><timept time="5s" value="5s" interp="linear"/><timept time="10s" value="20s" interp="linear"/>
            </timeMap></asset-clip>
            """, report: report);
        var issue = Assert.Single(report.Issues, i => i.Kind == IssueKind.Speed);
        Assert.Contains("la curva se cambia por su velocidad media", issue.Message, StringComparison.Ordinal);
        Assert.Equal(draft.Sequence.VideoTracks[0].Clips[0].Id, issue.ClipId);
        Assert.Equal(Time.FromSeconds(10), issue.Time);
    }

    [Theory]
    [InlineData(BuiltInEffects.Transform, BuiltInEffects.TransformParameters.X, 108)]
    [InlineData(BuiltInEffects.Transform, BuiltInEffects.TransformParameters.Opacity, 0.25)]
    [InlineData(BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, -6)]
    [InlineData(BuiltInEffects.Crop, BuiltInEffects.CropParameters.Left, 12)]
    public void Animated_parameters_use_first_keyframe_and_report_the_loss(string type, string name, double expected)
    {
        var (project, sequence, clip) = Model();
        clip.Effects.Add(new EffectInstance { Id = "e", Type = type, Parameters = new()
        {
            [name] = new Parameter { Value = 999, Keyframes = [new Keyframe(Time.FromSeconds(100), expected), new Keyframe(Time.FromSeconds(110), 1)] },
        } });
        var report = new InterchangeReport();
        var projected = Projected(project, sequence, report);
        var parameter = projected.Sequence.VideoTracks[0].Clips[0].Effects[0].Parameters[name];
        Assert.Equal(expected, parameter.Value);
        Assert.Empty(parameter.Keyframes);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Lost && i.ClipId == "c" && i.Time == Time.FromSeconds(10) && i.Message.Contains("keyframe", StringComparison.Ordinal));
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Crop_projection_matches_the_four_parameters_read()
    {
        var (project, sequence, clip) = Model();
        clip.Effects.Add(new EffectInstance { Id = "crop", Type = BuiltInEffects.Crop, Parameters = new() { [BuiltInEffects.CropParameters.Left] = new Parameter { Value = 12 } } });
        var crop = Projected(project, sequence, new InterchangeReport()).Sequence.VideoTracks[0].Clips[0].Effects[0];
        Assert.Equal(4, crop.Parameters.Count);
        Assert.Equal(0, crop.Parameters[BuiltInEffects.CropParameters.Top].Value);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Mixer_and_track_losses_are_reported_and_removed()
    {
        var (project, sequence, _) = Model();
        sequence.AudioBuses.Add(new AudioBus { Id = "bus", Name = "Voces" });
        sequence.MasterEffects.Add(new EffectInstance { Id = "master", Type = BuiltInEffects.Audio });
        sequence.MasterGainDb = -3;
        var track = sequence.VideoTracks[0];
        track.Name = "Montaje";
        track.Muted = track.Hidden = track.Solo = true;
        track.Effects.Add(new EffectInstance { Id = "track", Type = BuiltInEffects.Transform });
        var report = new InterchangeReport();
        var projected = Projected(project, sequence, report).Sequence;
        Assert.Empty(projected.AudioBuses);
        Assert.Empty(projected.MasterEffects);
        Assert.Equal(0, projected.MasterGainDb);
        Assert.False(projected.VideoTracks[0].Muted);
        Assert.False(projected.VideoTracks[0].Hidden);
        Assert.False(projected.VideoTracks[0].Solo);
        Assert.Empty(projected.VideoTracks[0].Effects);
        Assert.Equal("V1", projected.VideoTracks[0].Name);
        foreach (string detail in new[] { "bus", "maestros", "ganancia maestra", "silencio", "nombre" })
            Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Lost && i.Message.Contains(detail, StringComparison.OrdinalIgnoreCase));
        EqualRoundTrip(project, sequence);
    }

    [Theory]
    [InlineData(SequenceColorSpace.HdrPq, "9-16-9 (Rec. 2020 PQ)")]
    [InlineData(SequenceColorSpace.HdrHlg, "9-18-9 (Rec. 2020 HLG)")]
    public void Hdr_color_space_is_written_and_read(SequenceColorSpace colorSpace, string expected)
    {
        var (project, sequence, _) = Model();
        sequence.ColorSpace = colorSpace;
        Assert.Contains(Write(project, sequence).Element("resources")!.Elements("format"), f => f["colorSpace"] == expected);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Reel_and_marker_notes_are_removed_and_reported()
    {
        var (project, sequence, _) = Model();
        project.Media[0].Reel = "Rollo 1";
        sequence.Markers.Add(new Marker { Id = "mk", Name = "Marca", Time = Time.FromSeconds(12), Note = "Revisar" });
        var report = new InterchangeReport();
        var projected = Projected(project, sequence, report);
        Assert.Null(projected.Project.Media[0].Reel);
        Assert.Null(projected.Sequence.Markers[0].Note);
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Media && i.Message.Contains("carrete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Issues, i => i.Kind == IssueKind.Marker && i.Message.Contains("nota", StringComparison.OrdinalIgnoreCase));
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Real_model_still_has_no_rate_or_duration_and_keeps_virtual_source()
    {
        var (project, sequence, clip) = Model();
        project.Media[0] = new MediaRef { Id = "m", Name = "Foto", Path = @"C:\Medios\Foto.jpg", IsStillImage = true, HasVideo = true, Width = 4000, Height = 3000 };
        clip.SourceIn = Time.FromSeconds(120);
        clip.SourceOut = Time.FromSeconds(135);
        var actual = FcpxmlReader.Read(FcpxmlWriter.Write(project, sequence, null, new InterchangeOptions())[0].Text, null, new InterchangeOptions(), new InterchangeReport());
        var media = Assert.Single(actual.Project.Media);
        Assert.True(media.IsStillImage);
        Assert.Null(media.FrameRate);
        Assert.Equal(Time.Zero, media.Duration);
        Assert.Equal(4000, media.Width);
        Assert.Equal(3000, media.Height);
        Assert.Equal(Time.FromSeconds(120), actual.Sequence.VideoTracks[0].Clips[0].SourceIn);
        Assert.Equal(Time.FromSeconds(135), actual.Sequence.VideoTracks[0].Clips[0].SourceOut);
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Options_choose_default_timecode_and_exported_tracks()
    {
        var (project, sequence, clip) = Model();
        sequence.VideoTracks.Add(new Track { Id = "v2", Name = "V2", Kind = TrackKind.Video, Clips = [clip.Clone()] });
        sequence.VideoTracks[1].Clips[0].Id = "second";
        sequence.VideoTracks[1].Clips[0].SourceIn = Time.FromSeconds(30);
        sequence.VideoTracks[1].Clips[0].SourceOut = Time.FromSeconds(35);
        sequence.AudioTracks.Add(new Track { Id = "a", Name = "A1", Kind = TrackKind.Audio, Clips = [clip.Clone()] });
        sequence.AudioTracks[0].Clips[0].Id = "audio";
        var options = new InterchangeOptions { DefaultStartTimecode = "02:00:00:00", VideoTracks = [1], IncludeAudio = false };
        var actual = FcpxmlReader.Read(FcpxmlWriter.Write(project, sequence, null, options)[0].Text, null, options, new InterchangeReport());
        Assert.Equal("02:00:00:00", actual.Sequence.StartTimecode);
        Assert.Empty(actual.Sequence.AudioTracks);
        Assert.Equal(Time.FromSeconds(30), Assert.Single(Assert.Single(actual.Sequence.VideoTracks).Clips).SourceIn);
        EqualRoundTrip(project, sequence, options);
        Assert.Null(sequence.StartTimecode);
        Assert.Equal(2, sequence.VideoTracks.Count);
    }

    [Fact]
    public void Adjustment_order_matches_the_fcpxml_110_dtd()
    {
        var (project, sequence, clip) = Model();
        clip.Speed = new ClipSpeed { Rate = new Rational(2, 1) };
        clip.SourceOut = Time.FromSeconds(120);
        clip.Duration = Time.FromSeconds(10);
        clip.Effects =
        [
            new EffectInstance { Id = "transform", Type = BuiltInEffects.Transform, Parameters = new() { [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = 0.5 } } },
            new EffectInstance { Id = "audio", Type = BuiltInEffects.Audio, Parameters = new() { [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = -6 } } },
            new EffectInstance { Id = "crop", Type = BuiltInEffects.Crop },
        ];
        var node = Spine(Write(project, sequence)).Element("asset-clip")!;
        Assert.Equal(new[] { "timeMap", "adjust-crop", "adjust-conform", "adjust-transform", "adjust-blend", "adjust-volume" }, node.Children.Select(c => c.Name));
        EqualRoundTrip(project, sequence);
    }

    [Fact]
    public void Zero_gain_first_keyframe_still_exports_the_audio_adjustment()
    {
        var (project, sequence, clip) = Model();
        clip.Effects.Add(new EffectInstance { Id = "audio", Type = BuiltInEffects.Audio, Parameters = new()
        {
            [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = -6, Keyframes = [new Keyframe(Time.FromSeconds(100), 0), new Keyframe(Time.FromSeconds(110), -12)] },
        } });
        Assert.Equal("0dB", Spine(Write(project, sequence)).Element("asset-clip")!.Element("adjust-volume")?["amount"]);
        EqualRoundTrip(project, sequence);
    }

    [Theory]
    [InlineData("00:00:00:00", null, "00:00:00:00")]
    [InlineData("02:00:00:00", "03:00:00:00", "03:00:00:00")]
    public void Default_timecode_allows_zero_and_respects_sequence_timecode(string fallback, string? own, string expected)
    {
        var (project, sequence, _) = Model();
        sequence.StartTimecode = own;
        var options = new InterchangeOptions { DefaultStartTimecode = fallback };
        var actual = FcpxmlReader.Read(FcpxmlWriter.Write(project, sequence, null, options)[0].Text, null, options, new InterchangeReport());
        Assert.Equal(expected, actual.Sequence.StartTimecode);
        EqualRoundTrip(project, sequence, options);
    }

    [Fact]
    public void Excluding_audio_also_disables_embedded_audio_on_video_clips()
    {
        var (project, sequence, _) = Model();
        project.Media[0].HasAudio = true;
        sequence.VideoTracks.Add(new Track { Id = "v2", Name = "V2", Kind = TrackKind.Video,
            Clips = [new Clip { Id = "child", MediaId = "m", Start = Time.FromSeconds(12), SourceOut = Time.FromSeconds(2) }] });
        var options = new InterchangeOptions { IncludeAudio = false };
        var parent = Spine(Write(project, sequence, options)).Element("asset-clip")!;
        Assert.Equal("video", parent["srcEnable"]);
        Assert.Equal("video", parent.Element("asset-clip")!["srcEnable"]);
        EqualRoundTrip(project, sequence, options);
    }

    [Fact]
    public void Relative_media_path_uses_project_directory()
    {
        var (project, sequence, _) = Model();
        project.Media[0].Path = @"Medios\Plano con espacio.mov";
        var root = Write(project, sequence, directory: @"C:\Edicion\Proyecto");
        Assert.Equal("file:///C:/Edicion/Proyecto/Medios/Plano%20con%20espacio.mov", root.Element("resources")!.Element("asset")!.Element("media-rep")!["src"]);
    }
}
