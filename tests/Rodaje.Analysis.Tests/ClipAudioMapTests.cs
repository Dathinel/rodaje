using Rodaje.Core.Model;
using CoreTime = Rodaje.Core.Timing.Time;

namespace Rodaje.Analysis.Tests;

public sealed class ClipAudioMapTests
{
    private static CoreTime S(double s) => CoreTime.FromSeconds(s);

    [Fact]
    public void Without_speed_seconds_are_offset_by_the_clip_start()
    {
        var clip = new Clip { Id = "c", MediaId = "m", SourceIn = S(10), SourceOut = S(20), Start = S(100) };
        Assert.Equal((S(10), S(20)), ClipAudioMap.SourceWindow(clip));
        Assert.Equal(S(102.5), ClipAudioMap.ToTimeline(clip, 2.5));
        Assert.Equal((S(101), S(103)), ClipAudioMap.ToTimeline(clip, new TimeRange(1, 3)));
        Assert.Equal(S(110), ClipAudioMap.ToTimeline(clip, 10));
        Assert.Equal([(S(101), S(103))], ClipAudioMap.ToTimeline(clip, [new TimeRange(1, 3), new TimeRange(4, 4)]));
    }

    [Fact]
    public void At_double_speed_the_timeline_runs_at_half()
    {
        var clip = new Clip { Id = "c", MediaId = "m", SourceIn = S(0), SourceOut = S(10), Start = S(50), Speed = new ClipSpeed { Rate = new(2, 1) }, Duration = S(5) };
        Assert.Equal(S(55), clip.End);
        Assert.Equal(4.0, (ClipAudioMap.ToTimeline(clip, 8) - clip.Start).ToSeconds(), 3);
    }
}
