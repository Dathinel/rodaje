using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public sealed class MulticamEditsCazaTests
{
    private static Time F(long frames) => Time.FromFrames(frames, Rational.Fps25);

    private static (EditContext Ctx, Track Track) CreateContext(params MediaRef[] media)
    {
        var project = new Project
        {
            Id = "proj-1",
            Name = "Multicam Project",
            Media = media.ToList(),
        };

        var track = new Track
        {
            Id = "v1",
            Kind = TrackKind.Video,
            Name = "Video 1",
        };

        var sequence = new Sequence
        {
            Id = "seq-1",
            Name = "Sequence 1",
            FrameRate = Rational.Fps25,
            VideoTracks = [track],
        };

        project.Sequences.Add(sequence);
        var ctx = new EditContext(project, sequence, new SequentialIdGenerator());
        return (ctx, track);
    }

    private static Clip AddClip(Track track, string id, string mediaId, long sourceIn, long sourceOut, long start)
    {
        var clip = new Clip
        {
            Id = id,
            MediaId = mediaId,
            SourceIn = F(sourceIn),
            SourceOut = F(sourceOut),
            Start = F(start),
        };
        track.Clips.Add(clip);
        track.SortClips();
        return clip;
    }

    [Fact]
    public void SwitchAngle_SplitsClipAtSpecifiedInstant()
    {
        var media1 = new MediaRef { Id = "m1", Name = "Cam 1", Duration = F(500), HasVideo = true };
        var media2 = new MediaRef { Id = "m2", Name = "Cam 2", Duration = F(500), HasVideo = true };
        var (ctx, track) = CreateContext(media1, media2);

        AddClip(track, "c1", "m1", sourceIn: 0, sourceOut: 100, start: 0);

        var angles = new MulticamAngle[]
        {
            new("m1", F(50)),
            new("m2", F(50)),
        };

        string rightId = MulticamEdits.SwitchAngle(ctx, angles, "v1", F(40), 1);

        Assert.Equal(2, track.Clips.Count);
        Clip left = track.Clips[0];
        Clip right = track.Clips[1];

        Assert.Equal("c1", left.Id);
        Assert.Equal(rightId, right.Id);
        Assert.Equal(F(0), left.Start);
        Assert.Equal(F(40), left.End);
        Assert.Equal(F(40), right.Start);
        Assert.Equal(F(100), right.End);
    }

    [Fact]
    public void SwitchAngle_SetsTargetAngleMediaOnRightPieceAndPreservesOriginalOnLeftPiece()
    {
        var media1 = new MediaRef { Id = "m1", Name = "Cam 1", Duration = F(500), HasVideo = true };
        var media2 = new MediaRef { Id = "m2", Name = "Cam 2", Duration = F(500), HasVideo = true };
        var (ctx, track) = CreateContext(media1, media2);

        AddClip(track, "c1", "m1", sourceIn: 10, sourceOut: 90, start: 20);

        var angles = new MulticamAngle[]
        {
            new("m1", F(100)),
            new("m2", F(100)),
        };

        MulticamEdits.SwitchAngle(ctx, angles, "v1", F(50), 1);

        Clip left = track.Clips[0];
        Clip right = track.Clips[1];

        Assert.Equal("m1", left.MediaId);
        Assert.Equal("m2", right.MediaId);
    }

    [Fact]
    public void SwitchAngle_AlignsRightPieceToSameCommonClockPoint()
    {
        var media1 = new MediaRef { Id = "m1", Name = "Cam 1", Duration = F(500), HasVideo = true };
        var media2 = new MediaRef { Id = "m2", Name = "Cam 2", Duration = F(500), HasVideo = true };
        var (ctx, track) = CreateContext(media1, media2);

        AddClip(track, "c1", "m1", sourceIn: 50, sourceOut: 150, start: 0);

        var angles = new MulticamAngle[]
        {
            new("m1", F(200)),
            new("m2", F(150)),
        };

        MulticamEdits.SwitchAngle(ctx, angles, "v1", F(40), 1);

        Clip left = track.Clips[0];
        Clip right = track.Clips[1];

        Time commonClockAtCut = angles[0].Offset + left.SourceOut;
        Time rightPieceCommonClockAtCut = angles[1].Offset + right.SourceIn;

        Assert.Equal(commonClockAtCut, rightPieceCommonClockAtCut);
        Assert.Equal(F(290), rightPieceCommonClockAtCut);
        Assert.Equal(angles[0].Offset + F(150), angles[1].Offset + right.SourceOut);
    }

    [Fact]
    public void SwitchAngle_AlignsCommonClockWhenTargetOffsetIsGreaterThanCurrentOffset()
    {
        var media1 = new MediaRef { Id = "m1", Name = "Cam 1", Duration = F(500), HasVideo = true };
        var media2 = new MediaRef { Id = "m2", Name = "Cam 2", Duration = F(500), HasVideo = true };
        var (ctx, track) = CreateContext(media1, media2);

        AddClip(track, "c1", "m1", sourceIn: 80, sourceOut: 180, start: 20);

        var angles = new MulticamAngle[]
        {
            new("m1", F(100)),
            new("m2", F(160)),
        };

        MulticamEdits.SwitchAngle(ctx, angles, "v1", F(50), 1);

        Clip left = track.Clips[0];
        Clip right = track.Clips[1];

        Time commonClockAtCut = angles[0].Offset + left.SourceOut;
        Time rightPieceCommonClockAtCut = angles[1].Offset + right.SourceIn;

        Assert.Equal(commonClockAtCut, rightPieceCommonClockAtCut);
        Assert.Equal(F(210), rightPieceCommonClockAtCut);
        Assert.Equal(angles[0].Offset + F(180), angles[1].Offset + right.SourceOut);
    }

    [Fact]
    public void SwitchAngle_AlignsCommonClockAcrossMultipleAngles()
    {
        var media1 = new MediaRef { Id = "m1", Name = "Cam 1", Duration = F(500), HasVideo = true };
        var media2 = new MediaRef { Id = "m2", Name = "Cam 2", Duration = F(500), HasVideo = true };
        var media3 = new MediaRef { Id = "m3", Name = "Cam 3", Duration = F(500), HasVideo = true };
        var (ctx, track) = CreateContext(media1, media2, media3);

        AddClip(track, "c1", "m3", sourceIn: 20, sourceOut: 100, start: 10);

        var angles = new MulticamAngle[]
        {
            new("m1", F(50)),
            new("m2", F(100)),
            new("m3", F(150)),
        };

        MulticamEdits.SwitchAngle(ctx, angles, "v1", F(40), 0);

        Clip left = track.Clips[0];
        Clip right = track.Clips[1];

        Assert.Equal("m3", left.MediaId);
        Assert.Equal("m1", right.MediaId);

        Time commonClockAtCut = angles[2].Offset + left.SourceOut;
        Time rightPieceCommonClockAtCut = angles[0].Offset + right.SourceIn;

        Assert.Equal(commonClockAtCut, rightPieceCommonClockAtCut);
        Assert.Equal(F(200), rightPieceCommonClockAtCut);
        Assert.Equal(angles[2].Offset + F(100), angles[0].Offset + right.SourceOut);
    }
}
