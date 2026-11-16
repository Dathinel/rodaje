using System.Text.RegularExpressions;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Plantillas de movimiento (D-064): un clip, una capa generada por capa, igual que los títulos sueltos.</summary>
public sealed partial class TemplateLayerPlanTests
{
    private static Clip Template(string id, Time start, Time duration)
    {
        TextStyle name = TimelineTitleTests.Animated();
        TextStyle role = TimelineTitleTests.Animated();
        role.Size = 48;
        return new Clip
        {
            Id = id,
            Start = start,
            SourceIn = Time.Zero,
            SourceOut = duration,
            Generator = new ClipGenerator
            {
                Kind = GeneratorKinds.Template,
                Template = new GeneratorTemplate
                {
                    Name = "Barra",
                    MinSeconds = 1,
                    Document = "{}",
                    Layers =
                    [
                        new TemplateLayer { Id = "nombre", Text = "Ana Ruiz", Style = name, X = -560, Y = 380 },
                        new TemplateLayer { Id = "cargo", Text = "Directora", Style = role, X = -560, Y = 450, Start = 0.2, End = 0.3 },
                    ],
                },
            },
        };
    }

    private static TimelineBuilder Build(Action<Clip>? change = null)
    {
        var b = new TimelineBuilder(2, 0);
        Clip clip = Template("t", S(1), S(3));
        clip.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.Y, 20), (T.Scale, 1.2)));
        change?.Invoke(clip);
        b.Sequence.VideoTracks[0].Clips.Add(clip);
        return b;
    }

    [GeneratedRegex(@"\| \d+:[^:]*:")]
    private static partial Regex LayerPrefix();

    private static List<string> Frames(TimelineBuilder b)
    {
        var backend = new FakeTimelineBackend();
        using OfflineTimeline offline = OfflineProjects.Open(b, backend, new OfflineTimelineOptions { Titles = new TimelineTitleTests.FakeTitles { AlwaysReady = true } });
        var frames = new List<string>();
        for (long f = 0; f < offline.FrameCount; f++)
        {
            using CompositionFrame frame = offline.Render(f, CancellationToken.None);
            frames.Add(LayerPrefix().Replace(OfflineProjects.Describe(frame), "| "));
        }

        return frames;
    }

    [Fact]
    public void Each_layer_goes_to_its_own_generated_lane_above_the_track_with_its_window()
    {
        TimelinePlan plan = Build().Plan();

        GeneratedClipPlan name = Assert.Single(plan.Layers[0].Generated);
        GeneratedClipPlan role = Assert.Single(plan.Layers[1].Generated);
        Assert.Equal((S(1), S(4)), (name.Start, name.End));
        Assert.Equal((S(1.2), S(3.7)), (role.Start, role.End));
        Assert.Equal((-560.0, 380.0), (name.OffsetX, name.OffsetY));
        Assert.Equal(20 + 380, name.TransformAt(S(2)).Y, 9);
        Assert.Equal([0, 0, 1], plan.Layers.Select(l => l.TrackIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_template_draws_the_same_frames_as_its_titles_converted(bool split)
    {
        TimelineBuilder template = Build();
        if (split)
        {
            Edits.Split(template.Sequence.Id, "t", S(1.25)).Apply(template.Project);
        }

        List<string> expected = Frames(template);

        TimelineBuilder titles = Build();
        if (split)
        {
            Edits.Split(titles.Sequence.Id, "t", S(1.25)).Apply(titles.Project);
        }

        foreach (Clip clip in titles.Sequence.VideoTracks[0].Clips.ToList())
        {
            Edits.ConvertTemplateToTitles(titles.Sequence.Id, clip.Id).Apply(titles.Project);
        }

        List<string> actual = Frames(titles);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Contains(expected, f => f.Contains("titulo:", StringComparison.Ordinal));
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }
}
