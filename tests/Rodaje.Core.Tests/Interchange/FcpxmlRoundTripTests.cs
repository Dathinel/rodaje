using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Interchange;
using Rodaje.Core.Interchange.Fcpxml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Interchange;

public sealed class FcpxmlRoundTripTests
{
    private static readonly Rational Rate25 = new(25, 1);

    [Fact]
    public void RoundTrip_500_random_timelines()
    {
        var options = new InterchangeOptions();
        for (int seed = 0; seed < 500; seed++)
        {
            var timelineOptions = (seed % 5 == 0)
                ? new RandomTimelineOptions { Effects = true }
                : new RandomTimelineOptions();

            TimelineDraft draft = RandomTimelines.Create(seed, timelineOptions);
            var fidelityReport = new InterchangeReport();
            TimelineDraft expected = FcpxmlFidelity.Project(draft.Project, draft.Sequence, options, fidelityReport);

            IReadOnlyList<InterchangeOutput> outputs = FcpxmlWriter.Write(draft.Project, draft.Sequence, null, options);
            Assert.Single(outputs);

            var readReport = new InterchangeReport();
            TimelineDraft actual = FcpxmlReader.Read(outputs[0].Text, null, options, readReport);

            string? diff = Fidelity.Difference(expected, actual);
            if (diff is not null)
            {
                Assert.Fail($"Semilla {seed} falló: {diff}");
            }
        }
    }

    [Fact]
    public void RoundTrip_compound_clip_as_ref_clip_inside_media()
    {
        var project = new Project { Id = "p", Name = "Proyecto Compuesto" };
        var media = new MediaRef
        {
            Id = "m1",
            Name = "Plano01",
            Path = @"C:\Medios\Plano01.mov",
            Duration = Time.FromFrames(200, Rate25),
            HasVideo = true,
            HasAudio = true,
            FrameRate = Rate25,
            Width = 1920,
            Height = 1080,
            SampleRate = 48000,
            AudioChannels = 2,
        };
        project.Media.Add(media);

        var innerSeq = new Sequence
        {
            Id = "compound_seq",
            Name = "Secuencia Anidada",
            FrameRate = Rate25,
            Width = 1920,
            Height = 1080,
        };
        var innerTrack = new Track { Id = "it1", Kind = TrackKind.Video, Name = "V1" };
        innerTrack.Clips.Add(new Clip
        {
            Id = "ic1",
            MediaId = media.Id,
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromFrames(50, Rate25),
        });
        innerTrack.Clips.Add(new Clip
        {
            Id = "ic2",
            MediaId = media.Id,
            Start = Time.FromFrames(50, Rate25),
            SourceIn = Time.FromFrames(10, Rate25),
            SourceOut = Time.FromFrames(60, Rate25),
        });
        innerSeq.VideoTracks.Add(innerTrack);
        project.Sequences.Add(innerSeq);

        var mainSeq = new Sequence
        {
            Id = "main_seq",
            Name = "Secuencia Principal",
            FrameRate = Rate25,
            Width = 1920,
            Height = 1080,
        };
        var mainTrack = new Track { Id = "mt1", Kind = TrackKind.Video, Name = "V1" };
        mainTrack.Clips.Add(new Clip
        {
            Id = "ref1",
            SequenceId = innerSeq.Id,
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromFrames(100, Rate25),
        });
        mainSeq.VideoTracks.Add(mainTrack);
        project.Sequences.Add(mainSeq);

        var options = new InterchangeOptions();
        var fidelityReport = new InterchangeReport();
        TimelineDraft expected = FcpxmlFidelity.Project(project, mainSeq, options, fidelityReport);

        IReadOnlyList<InterchangeOutput> outputs = FcpxmlWriter.Write(project, mainSeq, null, options);
        Assert.Single(outputs);
        string xml = outputs[0].Text;

        // Comprueba que los recursos contienen un elemento <media> con la secuencia anidada
        Assert.Contains("<media ", xml, StringComparison.Ordinal);
        Assert.Contains("<ref-clip ", xml, StringComparison.Ordinal);

        var readReport = new InterchangeReport();
        TimelineDraft actual = FcpxmlReader.Read(xml, null, options, readReport);

        string? diff = Fidelity.Difference(expected, actual);
        Assert.Null(diff);

        Assert.Equal(2, actual.Project.Sequences.Count);
        Clip mainClip = Assert.Single(actual.Sequence.VideoTracks[0].Clips);
        Assert.True(mainClip.IsCompound);
        Assert.NotNull(mainClip.SequenceId);
        Sequence readInner = Assert.Single(actual.Project.Sequences, s => s.Id == mainClip.SequenceId);
        Assert.Equal(2, readInner.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public void RoundTrip_transform_with_formulas_1920x1080_sequence_and_4000x3000_media()
    {
        var project = new Project { Id = "p", Name = "Proyecto Transform" };
        var media = new MediaRef
        {
            Id = "m1",
            Name = "FotoGrande",
            Path = @"C:\Medios\Foto4K.jpg",
            Duration = Time.FromFrames(100, Rate25),
            HasVideo = true,
            HasAudio = false,
            FrameRate = Rate25,
            Width = 4000,
            Height = 3000,
        };
        project.Media.Add(media);

        var sequence = new Sequence
        {
            Id = "seq",
            Name = "Secuencia 1080p",
            FrameRate = Rate25,
            Width = 1920,
            Height = 1080,
        };
        var track = new Track { Id = "t1", Kind = TrackKind.Video, Name = "V1" };

        double x_px = 192.0;
        double y_px = -108.0;
        double scale = 1.5;
        double rotation = -30.0;
        double anchorX = 54.0;
        double anchorY = 27.0;
        double opacity = 0.85;

        var clip = new Clip
        {
            Id = "c1",
            MediaId = media.Id,
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromFrames(50, Rate25),
            Effects =
            [
                new EffectInstance
                {
                    Id = "eff_trans",
                    Type = BuiltInEffects.Transform,
                    Parameters = new Dictionary<string, Parameter>(StringComparer.Ordinal)
                    {
                        [BuiltInEffects.TransformParameters.X] = new Parameter { Value = x_px },
                        [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = y_px },
                        [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = scale },
                        [BuiltInEffects.TransformParameters.ScaleX] = new Parameter { Value = 1.0 },
                        [BuiltInEffects.TransformParameters.ScaleY] = new Parameter { Value = 1.0 },
                        [BuiltInEffects.TransformParameters.Rotation] = new Parameter { Value = rotation },
                        [BuiltInEffects.TransformParameters.AnchorX] = new Parameter { Value = anchorX },
                        [BuiltInEffects.TransformParameters.AnchorY] = new Parameter { Value = anchorY },
                        [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = opacity },
                    }
                }
            ]
        };
        track.Clips.Add(clip);
        sequence.VideoTracks.Add(track);
        project.Sequences.Add(sequence);

        var options = new InterchangeOptions();
        IReadOnlyList<InterchangeOutput> outputs = FcpxmlWriter.Write(project, sequence, null, options);
        Assert.Single(outputs);
        string xml = outputs[0].Text;

        // Comprueba las fórmulas en el XML:
        // posX = 192 * 100 / 1080 = 17.77777777777778
        // posY = -(-108) * 100 / 1080 = 10
        // rot = -(-30) = 30
        // ancX = 54 * 100 / 1080 = 5
        // ancY = -27 * 100 / 1080 = -2.5
        Assert.Contains("adjust-conform", xml, StringComparison.Ordinal);
        Assert.Contains("adjust-transform", xml, StringComparison.Ordinal);
        Assert.Contains("position=\"17.777777777777", xml, StringComparison.Ordinal);
        Assert.Contains("10\"", xml, StringComparison.Ordinal);
        Assert.Contains("scale=\"1.5 1.5\"", xml, StringComparison.Ordinal);
        Assert.Contains("rotation=\"30\"", xml, StringComparison.Ordinal);
        Assert.Contains("anchor=\"5 -2.5\"", xml, StringComparison.Ordinal);
        Assert.Contains("adjust-blend", xml, StringComparison.Ordinal);
        Assert.Contains("amount=\"0.85\"", xml, StringComparison.Ordinal);

        var readReport = new InterchangeReport();
        TimelineDraft actual = FcpxmlReader.Read(xml, null, options, readReport);

        Clip readClip = Assert.Single(actual.Sequence.VideoTracks[0].Clips);
        EffectInstance readEffect = Assert.Single(readClip.Effects, e => e.Type == BuiltInEffects.Transform);

        Assert.Equal(x_px, readEffect.Parameters[BuiltInEffects.TransformParameters.X].Value, 4);
        Assert.Equal(y_px, readEffect.Parameters[BuiltInEffects.TransformParameters.Y].Value, 4);
        Assert.Equal(scale, readEffect.Parameters[BuiltInEffects.TransformParameters.Scale].Value, 4);
        Assert.Equal(rotation, readEffect.Parameters[BuiltInEffects.TransformParameters.Rotation].Value, 4);
        Assert.Equal(anchorX, readEffect.Parameters[BuiltInEffects.TransformParameters.AnchorX].Value, 4);
        Assert.Equal(anchorY, readEffect.Parameters[BuiltInEffects.TransformParameters.AnchorY].Value, 4);
        Assert.Equal(opacity, readEffect.Parameters[BuiltInEffects.TransformParameters.Opacity].Value, 4);
    }

    [Fact]
    public void RoundTrip_titles_become_gap_and_marker()
    {
        var project = new Project { Id = "p", Name = "Proyecto Título" };
        var sequence = new Sequence
        {
            Id = "seq",
            Name = "Secuencia",
            FrameRate = Rate25,
            Width = 1920,
            Height = 1080,
        };
        var track = new Track { Id = "t1", Kind = TrackKind.Video, Name = "V1" };
        track.Clips.Add(new Clip
        {
            Id = "title1",
            Start = Time.FromFrames(10, Rate25),
            SourceIn = Time.Zero,
            SourceOut = Time.FromFrames(25, Rate25),
            Generator = new ClipGenerator
            {
                Text = "Texto de apertura",
                Style = new TextStyle { Size = 48 },
            }
        });
        sequence.VideoTracks.Add(track);
        project.Sequences.Add(sequence);

        var options = new InterchangeOptions();
        var fidelityReport = new InterchangeReport();
        TimelineDraft expected = FcpxmlFidelity.Project(project, sequence, options, fidelityReport);

        IReadOnlyList<InterchangeOutput> outputs = FcpxmlWriter.Write(project, sequence, null, options);
        Assert.Single(outputs);
        string xml = outputs[0].Text;

        Assert.Contains("<gap ", xml, StringComparison.Ordinal);
        Assert.Contains("<marker ", xml, StringComparison.Ordinal);
        Assert.Contains("value=\"Texto de apertura\"", xml, StringComparison.Ordinal);

        var readReport = new InterchangeReport();
        TimelineDraft actual = FcpxmlReader.Read(xml, null, options, readReport);

        string? diff = Fidelity.Difference(expected, actual);
        Assert.Null(diff);
    }

    [Fact]
    public void Hostile_billion_laughs_entity_expansion_throws_interchange_exception()
    {
        string xml = @"<?xml version=""1.0""?>
<!DOCTYPE fcpxml [
  <!ENTITY lol ""lol"">
  <!ENTITY lol1 ""&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;"">
  <!ENTITY lol2 ""&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;"">
  <!ENTITY lol3 ""&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;"">
]>
<fcpxml version=""1.10"">
  <resources>
    <format id=""r1"" frameDuration=""1/25s""/>
  </resources>
  <library>
    <event name=""&lol3;""/>
  </library>
</fcpxml>";

        var options = new InterchangeOptions();
        var report = new InterchangeReport();
        Assert.Throws<InterchangeException>(() => FcpxmlReader.Read(xml, null, options, report));
    }

    [Fact]
    public void Hostile_external_entity_system_xxe_throws_interchange_exception()
    {
        string xml = @"<?xml version=""1.0""?>
<!DOCTYPE fcpxml [
  <!ENTITY xxe SYSTEM ""file:///etc/passwd"">
]>
<fcpxml version=""1.10"">
  <resources>
    <format id=""r1"" frameDuration=""1/25s""/>
  </resources>
  <library>
    <event name=""&xxe;""/>
  </library>
</fcpxml>";

        var options = new InterchangeOptions();
        var report = new InterchangeReport();
        Assert.Throws<InterchangeException>(() => FcpxmlReader.Read(xml, null, options, report));
    }

    [Fact]
    public void Hostile_ten_thousand_nesting_levels_throws_interchange_exception()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<fcpxml version=\"1.10\">");
        for (int i = 0; i < 10000; i++) sb.Append("<group>");
        for (int i = 0; i < 10000; i++) sb.Append("</group>");
        sb.Append("</fcpxml>");

        var options = new InterchangeOptions();
        var report = new InterchangeReport();
        Assert.Throws<InterchangeException>(() => FcpxmlReader.Read(sb.ToString(), null, options, report));
    }

    [Fact]
    public void Hostile_duration_division_by_zero_throws_interchange_exception()
    {
        string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<fcpxml version=""1.10"">
  <resources>
    <format id=""r1"" frameDuration=""1/0s""/>
  </resources>
  <library>
    <event name=""Evento"">
      <project name=""Proyecto"">
        <sequence format=""r1"" duration=""1/0s"" tcStart=""0s"">
          <spine>
            <gap offset=""0s"" duration=""1/0s""/>
          </spine>
        </sequence>
      </project>
    </event>
  </library>
</fcpxml>";

        var options = new InterchangeOptions();
        var report = new InterchangeReport();
        Assert.Throws<InterchangeException>(() => FcpxmlReader.Read(xml, null, options, report));
    }

    [Fact]
    public void Hostile_negative_duration_throws_interchange_exception()
    {
        string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<fcpxml version=""1.10"">
  <resources>
    <format id=""r1"" frameDuration=""1/25s""/>
  </resources>
  <library>
    <event name=""Evento"">
      <project name=""Proyecto"">
        <sequence format=""r1"" duration=""-10s"" tcStart=""0s"">
          <spine>
            <gap offset=""0s"" duration=""-10s""/>
          </spine>
        </sequence>
      </project>
    </event>
  </library>
</fcpxml>";

        var options = new InterchangeOptions();
        var report = new InterchangeReport();
        Assert.Throws<InterchangeException>(() => FcpxmlReader.Read(xml, null, options, report));
    }
}
