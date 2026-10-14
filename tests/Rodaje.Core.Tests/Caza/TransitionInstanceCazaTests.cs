using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.Core.Tests.Caza;

public class TransitionInstanceCazaTests
{
    [Fact]
    public void TransitionInstance_HoldsFromToDurationAlignmentAndCurve()
    {
        var duration = Time.FromSeconds(1);
        var transition = new TransitionInstance
        {
            From = "clip-1",
            To = "clip-2",
            Duration = duration,
            Alignment = TransitionAlignment.Center,
            Curve = Interpolation.Linear
        };

        Assert.Equal("clip-1", transition.From);
        Assert.Equal("clip-2", transition.To);
        Assert.Equal(duration, transition.Duration);
        Assert.Equal(TransitionAlignment.Center, transition.Alignment);
        Assert.Equal(Interpolation.Linear, transition.Curve);
    }

    [Fact]
    public void TransitionInstance_DefaultsToCenteredAlignment()
    {
        var transition = new TransitionInstance();

        Assert.Equal(TransitionAlignment.Center, transition.Alignment);
    }

    [Fact]
    public void CenteredTransition_SplitsDurationEquallyAroundCut()
    {
        var duration = Time.FromSeconds(2);
        var transition = new TransitionInstance
        {
            Duration = duration,
            Alignment = TransitionAlignment.Center
        };

        Assert.Equal(Time.FromSeconds(1), transition.Before);
        Assert.Equal(Time.FromSeconds(1), transition.After);
        Assert.Equal(duration, transition.Before + transition.After);
    }

    [Fact]
    public void TransitionToSideBefore_PlacesDurationBeforeCut()
    {
        var duration = Time.FromSeconds(2);
        var transition = new TransitionInstance
        {
            Duration = duration,
            Alignment = TransitionAlignment.Before
        };

        Assert.Equal(duration, transition.Before);
        Assert.Equal(Time.Zero, transition.After);
        Assert.Equal(duration, transition.Before + transition.After);
    }

    [Fact]
    public void TransitionToSideAfter_PlacesDurationAfterCut()
    {
        var duration = Time.FromSeconds(2);
        var transition = new TransitionInstance
        {
            Duration = duration,
            Alignment = TransitionAlignment.After
        };

        Assert.Equal(Time.Zero, transition.Before);
        Assert.Equal(duration, transition.After);
        Assert.Equal(duration, transition.Before + transition.After);
    }
}
