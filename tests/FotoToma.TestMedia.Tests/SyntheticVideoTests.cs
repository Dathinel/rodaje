namespace FotoToma.TestMedia.Tests;

public class SyntheticVideoTests
{
    [Fact]
    public void Render_IsDeterministic()
    {
        var a = new SyntheticVideo(320, 240);
        var b = new SyntheticVideo(320, 240);
        var frameA = new byte[a.FrameSize];
        var frameB = new byte[b.FrameSize];

        a.Render(frameA, 321);
        b.Render(frameB, 321);

        Assert.Equal(frameA, frameB);
        Assert.Equal(320 * 240 * 3 / 2, a.FrameSize);
    }

    [Fact]
    public void ConsecutiveFrames_DifferOutsideTheCode()
    {
        var video = new SyntheticVideo(320, 240);
        var first = new byte[video.FrameSize];
        var second = new byte[video.FrameSize];
        video.Render(first, 10);
        video.Render(second, 11);

        int lumaDifferences = 0;
        for (int i = 0; i < 320 * 240; i++)
        {
            if (first[i] != second[i])
            {
                lumaDifferences++;
            }
        }

        Assert.True(lumaDifferences > 320 * 240 / 2, $"Solo cambiaron {lumaDifferences} píxeles de luma.");
        Assert.NotEqual(first[(320 * 240)..], second[(320 * 240)..]);
    }

    [Fact]
    public void Constructor_RejectsOddSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyntheticVideo(321, 240));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyntheticVideo(320, 241));
    }

    [Fact]
    public void Render_RejectsShortBufferAndNegativeIndex()
    {
        var video = new SyntheticVideo(320, 240);

        Assert.Throws<ArgumentException>(() => video.Render(new byte[10], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => video.Render(new byte[video.FrameSize], -1));
    }
}
