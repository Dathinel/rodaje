using Rodaje.Analysis.Ducking;

namespace Rodaje.Analysis.Tests;

public class VoiceProbabilityTests
{
    private const double Window = 0.032;

    private static float[] Track(int length, params (int From, int To, float P)[] spans)
    {
        var p = new float[length];
        foreach ((int from, int to, float value) in spans)
        {
            p.AsSpan(from, to - from).Fill(value);
        }

        return p;
    }

    [Fact]
    public void Hysteresis_keeps_speech_between_the_two_thresholds()
    {
        // 0,9 abre; 0,4 (entre 0,35 y 0,5) no cierra; 0,1 cierra tras el silencio mínimo.
        float[] p = Track(100, (10, 30, 0.9f), (30, 40, 0.4f), (40, 100, 0.1f));
        List<TimeRange> ranges = VoiceProbability.ToRanges(p, Window, 100 * Window, new VoiceProbabilityOptions { Padding = 0 });
        TimeRange r = Assert.Single(ranges);
        Assert.Equal(10 * Window, r.Start, 6);
        Assert.Equal(40 * Window, r.End, 6);
    }

    [Fact]
    public void Short_pauses_do_not_split_and_short_blips_are_dropped()
    {
        // Pausa de 2 ventanas (64 ms < 100 ms) dentro de la frase; un golpe de 3 ventanas (96 ms < 250 ms) aparte.
        float[] p = Track(200, (10, 40, 0.9f), (42, 70, 0.9f), (120, 123, 0.95f));
        List<TimeRange> ranges = VoiceProbability.ToRanges(p, Window, 200 * Window, new VoiceProbabilityOptions { Padding = 0 });
        TimeRange r = Assert.Single(ranges);
        Assert.Equal(10 * Window, r.Start, 6);
        Assert.Equal(70 * Window, r.End, 6);
    }

    [Fact]
    public void Padding_never_overlaps_the_neighbour_and_speech_until_the_end_is_kept()
    {
        float[] p = Track(100, (10, 30, 0.9f), (35, 100, 0.9f));
        List<TimeRange> ranges = VoiceProbability.ToRanges(p, Window, 100 * Window, new VoiceProbabilityOptions { Padding = 0.2, MinimumSilence = 0.1 });
        Assert.Equal(2, ranges.Count);
        Assert.Equal((10 * Window) - 0.2, ranges[0].Start, 6);
        Assert.Equal(ranges[0].End, ranges[1].Start, 6);
        Assert.Equal(100 * Window, ranges[1].End, 6);
    }

    [Fact]
    public void Silence_has_no_speech() =>
        Assert.Empty(VoiceProbability.ToRanges(new float[500], Window, 500 * Window));
}
