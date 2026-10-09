namespace Rodaje.TestMedia.Tests;

public class AudioCodeTests
{
    private const int Rate = 48000;

    private static float[] Generate(long firstSample, long frames, int channels = 2)
    {
        var samples = new float[frames * channels];
        AudioCode.Generate(samples, firstSample, Rate, channels);
        return samples;
    }

    [Fact]
    public void Detect_FindsEverySecondAtExactSample()
    {
        float[] audio = Generate(0, 5 * Rate);

        IReadOnlyList<AudioMark> marks = AudioCode.Detect(audio, Rate, 2);

        Assert.Equal(5, marks.Count);
        for (int s = 0; s < 5; s++)
        {
            Assert.Equal((long)s * Rate, marks[s].Sample);
            Assert.Equal(s, marks[s].Second);
            Assert.True(marks[s].Correlation > 0.99);
        }
    }

    [Fact]
    public void Detect_PositionsAreRelativeToBuffer()
    {
        const int padding = 12_345;
        float[] audio = Generate(0, 3 * Rate, channels: 1);
        var padded = new float[padding + audio.Length];
        audio.CopyTo(padded, padding);

        IReadOnlyList<AudioMark> marks = AudioCode.Detect(padded, Rate, 1);

        Assert.Equal([padding, padding + Rate, padding + 2 * Rate], marks.Select(m => m.Sample));
    }

    [Fact]
    public void Detect_SurvivesNoiseGainAndLowPass()
    {
        float[] audio = Generate(0, 4 * Rate, channels: 1);
        var random = new Random(7);
        float previous = 0;
        for (int i = 0; i < audio.Length; i++)
        {
            // Paso bajo de un polo (~10 kHz), media ganancia y ruido blanco a -40 dB.
            previous += 0.73f * (audio[i] - previous);
            audio[i] = 0.5f * previous + 0.01f * (float)(random.NextDouble() * 2 - 1);
        }

        IReadOnlyList<AudioMark> marks = AudioCode.Detect(audio, Rate, 1);

        Assert.Equal(4, marks.Count);
        for (int s = 0; s < 4; s++)
        {
            Assert.InRange(marks[s].Sample - (long)s * Rate, -3, 3);
            Assert.Equal(s, marks[s].Second);
        }
    }

    [Fact]
    public void Generate_InChunks_MatchesSingleCall()
    {
        float[] whole = Generate(0, Rate + 777);
        float[] first = Generate(0, 1000);
        float[] rest = Generate(1000, Rate + 777 - 1000);

        Assert.Equal(whole, first.Concat(rest).ToArray());
    }

    [Fact]
    public void SecondNumber_WrapsAtModulus()
    {
        long start = (long)(AudioCode.SecondModulus - 2) * Rate;
        float[] audio = Generate(start, 4 * Rate, channels: 1);

        IReadOnlyList<AudioMark> marks = AudioCode.Detect(audio, Rate, 1);

        Assert.Equal([AudioCode.SecondModulus - 2, AudioCode.SecondModulus - 1, 0, 1], marks.Select(m => m.Second ?? -1));
    }

    [Fact]
    public void Detect_LeavesSecondNull_WhenMarkIsCutShort()
    {
        float[] audio = Generate(0, Rate + Rate / 4, channels: 1);

        IReadOnlyList<AudioMark> marks = AudioCode.Detect(audio, Rate, 1);

        Assert.Equal(2, marks.Count);
        Assert.Equal(0, marks[0].Second);
        Assert.Equal(Rate, marks[1].Sample);
        Assert.Null(marks[1].Second);
    }

    [Fact]
    public void Detect_FindsNothingInSilenceOrSteadyTone()
    {
        var silence = new float[2 * Rate];
        var tone = new float[2 * Rate];
        for (int i = 0; i < tone.Length; i++)
        {
            tone[i] = 0.3f * MathF.Sin(2 * MathF.PI * 1000 * i / Rate);
        }

        Assert.Empty(AudioCode.Detect(silence, Rate, 1));
        Assert.Empty(AudioCode.Detect(tone, Rate, 1));
    }

    [Fact]
    public void Generate_RejectsBadArguments()
    {
        Assert.Throws<ArgumentException>(() => AudioCode.Generate(new float[3], 0, Rate, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioCode.Generate(new float[4], -1, Rate, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioCode.Generate(new float[4], 0, Rate, 0));
    }
}
