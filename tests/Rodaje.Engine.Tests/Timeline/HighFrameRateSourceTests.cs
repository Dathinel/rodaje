using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Material de 120 y 240 fps en una línea de 30 o 60: el plan pide un cuadro por cuadro de la secuencia, no todos.</summary>
public sealed class HighFrameRateSourceTests
{
    [Theory]
    [InlineData(120, 30)]
    [InlineData(240, 30)]
    [InlineData(120, 60)]
    [InlineData(240, 60)]
    public void Solo_se_piden_los_cuadros_que_se_ven(int sourceFps, int sequenceFps)
    {
        var sourceRate = new Rational(sourceFps, 1);
        var sequenceRate = new Rational(sequenceFps, 1);
        var b = new TimelineBuilder(1, 0, sequenceRate);
        b.AddMedia("m", sourceRate, sourceFps * 4);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

        int step = sourceFps / sequenceFps;
        long shown = 0;
        long previous = -1;
        for (long index = plan.FirstFrame; index != long.MaxValue; index = plan.NextShown(index + 1))
        {
            if (previous >= 0)
            {
                Assert.Equal(step, index - previous);
            }

            previous = index;
            shown++;
        }

        Assert.Equal(sequenceFps * 4, shown);
        for (int f = 0; f < sequenceFps * 4; f++)
        {
            Assert.Equal(f * step, plan.FrameAt(Time.FromFrames(f, sequenceRate)));
        }
    }
}
