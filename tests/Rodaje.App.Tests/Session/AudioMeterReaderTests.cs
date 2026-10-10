using Rodaje.App.Services;
using Rodaje.Audio.Metering;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

public sealed class AudioMeterReaderTests
{
    private static readonly AudioLevels Loud = new(new ChannelLevel(0.5f, 0.5f, 0.3f), new ChannelLevel(0.5f, 0.5f, 0.3f), 480, false);

    /// <summary>Una mezcla que siempre suena fuerte y anota cada tramo que se le pide.</summary>
    private sealed class Mix
    {
        public List<(Time From, Time To, Time RmsFloor)> Calls { get; } = [];
    }

    private static readonly Func<Mix, Time, Time, Time, AudioLevels> Measure = static (mix, from, to, floor) =>
    {
        mix.Calls.Add((from, to, floor));
        return Loud;
    };

    [Fact]
    public void While_playing_it_measures_from_the_previous_read_to_what_sounds_now()
    {
        var reader = new AudioMeterReader();
        var mix = new Mix();

        reader.Take(mix, Time.FromSeconds(1), playing: true, TimeSpan.Zero, Measure);
        AudioLevels levels = reader.Take(mix, Time.FromSeconds(1.01), playing: true, TimeSpan.FromMilliseconds(5), Measure);

        Assert.Equal(Loud, levels);
        Assert.Equal((Time.FromSeconds(1), Time.FromSeconds(1), Time.FromSeconds(1)), mix.Calls[0]);
        Assert.Equal((Time.FromSeconds(1), Time.FromSeconds(1.015), Time.FromSeconds(1)), mix.Calls[1]);
    }

    [Fact]
    public void Paused_it_is_silent_and_does_not_measure_the_stopped_position()
    {
        var reader = new AudioMeterReader();
        var mix = new Mix();
        reader.Take(mix, Time.FromSeconds(2), playing: true, TimeSpan.Zero, Measure);

        AudioLevels paused = reader.Take(mix, Time.FromSeconds(2.05), playing: false, TimeSpan.FromSeconds(3), Measure);
        AudioLevels again = reader.Take(mix, Time.FromSeconds(2.05), playing: false, TimeSpan.FromSeconds(4), Measure);

        Assert.Equal(AudioLevels.Silence, paused);
        Assert.Equal(AudioLevels.Silence, again);
        Assert.Equal(0, paused.Frames);
        Assert.True(double.IsNegativeInfinity(paused.Left.RmsDecibels));
        Assert.Single(mix.Calls);
    }

    [Fact]
    public void Resuming_after_a_pause_starts_a_fresh_window()
    {
        var reader = new AudioMeterReader();
        var mix = new Mix();
        reader.Take(mix, Time.FromSeconds(2), playing: true, TimeSpan.Zero, Measure);
        reader.Take(mix, Time.FromSeconds(2.1), playing: false, TimeSpan.Zero, Measure);

        reader.Take(mix, Time.FromSeconds(2.1), playing: true, TimeSpan.Zero, Measure);

        Assert.Equal((Time.FromSeconds(2.1), Time.FromSeconds(2.1), Time.FromSeconds(2.1)), mix.Calls[^1]);
    }

    [Fact]
    public void Without_audio_it_is_silent()
    {
        var reader = new AudioMeterReader();

        Assert.Equal(AudioLevels.Silence, reader.Take<Mix>(null, Time.FromSeconds(1), playing: true, TimeSpan.Zero, Measure));
    }

    [Fact]
    public void After_pausing_the_bars_fall_to_zero()
    {
        var reader = new AudioMeterReader();
        var mix = new Mix();
        var ballistics = new MeterBallistics();
        ballistics.Update(reader.Take(mix, Time.FromSeconds(1), playing: true, TimeSpan.Zero, Measure).Left.TruePeakDecibels, 1 / 60.0);
        Assert.False(ballistics.IsIdle);

        for (int frame = 0; frame < 60 * 10; frame++)
        {
            AudioLevels levels = reader.Take(mix, Time.FromSeconds(1), playing: false, TimeSpan.Zero, Measure);
            ballistics.Update(levels.Frames > 0 ? levels.Left.TruePeakDecibels : double.NegativeInfinity, 1 / 60.0);
        }

        Assert.True(ballistics.IsIdle);
    }
}
