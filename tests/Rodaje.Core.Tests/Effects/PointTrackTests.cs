using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Effects;

public sealed class PointTrackTests
{
    private static Time F(long frame) => Time.FromFrames(frame, new Rational(30, 1));

    [Fact]
    public void Homography_from_quads_maps_the_corners_and_inverts()
    {
        Vector2[] from = [new(0, 0), new(1920, 0), new(1920, 1080), new(0, 1080)];
        Vector2[] to = [new(300.25f, 120.5f), new(1500, 200), new(1620.75f, 900), new(250, 1000)];
        Homography h = Homography.FromQuads(from, to)!.Value;
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(Vector2.Distance(h.Apply(from[i]), to[i]), 0, 1e-3);
        }

        Homography back = h.Inverse()!.Value;
        Vector2 p = new(777, 333);
        Assert.InRange(Vector2.Distance(back.Apply(h.Apply(p)), p), 0, 1e-3);
        Assert.False(h.IsAffine);
        Assert.True(Homography.IsConvex(to));
    }

    [Fact]
    public void Collinear_or_crossed_quads_have_no_homography()
    {
        Vector2[] square = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        Assert.Null(Homography.FromQuads(square, [new(0, 0), new(1, 0), new(2, 0), new(0, 1)]));
        Assert.False(Homography.IsConvex([new(0, 0), new(1, 1), new(1, 0), new(0, 1)]));
    }

    [Fact]
    public void Similarity_from_two_points_recovers_scale_and_rotation()
    {
        Homography truth = Homography.Similarity(1.5, 0.3, 10, -4);
        Vector2 a = new(3, 4), b = new(-2, 7);
        Homography h = Homography.SimilarityFrom(a, b, truth.Apply(a), truth.Apply(b));
        Vector2 c = new(11, -5);
        Assert.InRange(Vector2.Distance(h.Apply(c), truth.Apply(c)), 0, 1e-4);
    }

    [Fact]
    public void Samples_round_trip_and_bad_text_is_ignored()
    {
        var track = new PointTrack([F(0), F(1)], [[new(new(0.5f, 0.25f), 1)], [new(new(0.501234f, 0.25f), 0.42f)]]);
        string text = track.Format();
        Assert.Equal("0@0.5,0.25,1|23520000@0.501234,0.25,0.42", text);
        PointTrack again = PointTrack.Parse(text)!;
        Assert.Equal(2, again.Count);
        Assert.Equal(0.42f, again.FrameAt(1)[0].Confidence, 3);
        Assert.Null(PointTrack.Parse("5@0.1,0.1,1|5@0.2,0.2,1"));
        Assert.Null(PointTrack.Parse("0@0.1,0.1,1|9@0.1,0.1,1;0.2,0.2,1"));
        Assert.Null(PointTrack.Parse("0@0.1,0.1,2"));
    }

    [Fact]
    public void Merge_replaces_only_the_retracked_span()
    {
        PointTrack track = Line(0, 10, 0f);
        PointTrack update = Line(4, 6, 1f);
        PointTrack merged = track.Merge(update);
        Assert.Equal(11, merged.Count);
        Assert.Equal(1f + 0.05f, merged.FrameAt(5)[0].Position.Y, 4);
        Assert.Equal(0.09f, merged.FrameAt(9)[0].Position.Y, 4);
    }

    [Fact]
    public void Doubtful_frames_are_filled_and_smoothing_keeps_steady_motion()
    {
        TrackSample[][] frames = [.. Enumerable.Range(0, 11).Select(i => new[] { new TrackSample(new(0.01f * i, 0), i is 4 or 5 ? 0.2f : 0.95f) })];
        frames[4][0] = frames[4][0] with { Position = new(0.9f, 0.9f) };
        var track = new PointTrack([.. Enumerable.Range(0, 11).Select(i => F(i))], frames);
        PointTrack filled = track.FillDoubtful(0.7f);
        Assert.Equal(0.04f, filled.FrameAt(4)[0].Position.X, 4);
        Assert.Equal(0f, filled.FrameAt(4)[0].Position.Y, 4);

        // Una recta sale igual del suavizado (la regresión local no achata el movimiento parejo).
        PointTrack smooth = filled.Smooth(3, 0.7f);
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(0.01f * i, smooth.FrameAt(i)[0].Position.X, 4);
        }
    }

    [Fact]
    public void Points_round_trip()
    {
        TrackPointSpec[] points = [new(new(0.5f, 0.4f), new(0.02f, 0.035f), new(0.06f, 0.1f))];
        Assert.Equal(points, PointTrack.ParsePoints(PointTrack.FormatPoints(points)));
        Assert.Empty(PointTrack.ParsePoints("1,2,3"));
    }

    private static PointTrack Line(int first, int last, float offset) => new(
        [.. Enumerable.Range(first, last - first + 1).Select(i => F(i))],
        [.. Enumerable.Range(first, last - first + 1).Select(i => new[] { new TrackSample(new(0, offset + (0.01f * i)), 1) })]);
}
