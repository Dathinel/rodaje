using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Text;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Text;

public sealed class TextLoopTests
{
    [Fact]
    public void Quarter_cycle_has_the_documented_amplitudes()
    {
        UnitPose wobble = TextLoops.Delta(new StyleLoop { Kind = TextLoopKind.Wobble }, 0.25 / 0.8);
        Assert.Equal(12, wobble.X, 9);
        Assert.Equal(5, wobble.Rotation, 9);
        Assert.Equal(1.06, TextLoops.Delta(new StyleLoop { Kind = TextLoopKind.Pulse }, 0.25 / 1.2).Scale, 9);
        Assert.Equal(10, TextLoops.Delta(new StyleLoop { Kind = TextLoopKind.Float }, 0.25 / 0.5).Y, 9);
        var shake = new StyleLoop { Kind = TextLoopKind.Shake };
        UnitPose first = TextLoops.Delta(shake, 0.17);
        Assert.NotEqual(first, TextLoops.Delta(shake, 0.17 + 1 / shake.Speed));
        _ = TextLoops.Delta(shake, 900);
        Assert.Equal(first, TextLoops.Delta(shake.Clone(), 0.17));
    }

    [Theory]
    [InlineData(TextLoopKind.Wobble, 0.8)]
    [InlineData(TextLoopKind.Shake, 6)]
    [InlineData(TextLoopKind.Pulse, 1.2)]
    [InlineData(TextLoopKind.Float, 0.5)]
    public void Defaults_and_periodicity(TextLoopKind kind, double speed)
    {
        var loop = new StyleLoop { Kind = kind };
        Assert.Equal(speed, loop.Speed);
        Assert.Equal(1, loop.Amount);
        foreach (double t in new[] { -3.2, 0, 0.17, 1.2, 200.3 })
        {
            UnitPose a = TextLoops.Delta(loop, t);
            Assert.Equal(a, TextLoops.Delta(loop, t));
            if (kind != TextLoopKind.Shake)
            {
                UnitPose b = TextLoops.Delta(loop, t + 1 / speed);
                Assert.Equal(a.X, b.X, 8);
                Assert.Equal(a.Y, b.Y, 8);
                Assert.Equal(a.Scale, b.Scale, 8);
                Assert.Equal(a.Rotation, b.Rotation, 8);
            }
        }
    }

    [Theory]
    [InlineData(TextLoopKind.Wobble)]
    [InlineData(TextLoopKind.Shake)]
    [InlineData(TextLoopKind.Pulse)]
    [InlineData(TextLoopKind.Float)]
    public void Finite_bounded_and_amount_clamped(TextLoopKind kind)
    {
        foreach (double amount in new[] { -10.0, 0.1, 1, 2, 100 })
        {
            var loop = new StyleLoop { Kind = kind, Amount = amount };
            for (int i = -100; i <= 100; i++)
            {
                double t = i * 0.019;
                UnitPose pose = TextLoops.Delta(loop, t);
                Assert.Equal(TextLoops.Delta(new StyleLoop { Kind = kind, Amount = Math.Clamp(amount, 0.1, 2) }, t), pose);
                Assert.InRange(pose.X, -24, 24);
                Assert.InRange(pose.Y, -20, 20);
                Assert.InRange(pose.Rotation, -10, 10);
                Assert.InRange(pose.Scale, 0.88, 1.12);
                Assert.Equal(1, pose.Opacity);
                if (kind == TextLoopKind.Shake)
                {
                    Assert.InRange(pose.X, -6, 6);
                    Assert.InRange(pose.Y, -6, 6);
                }
            }
        }

        foreach (double t in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, -double.MaxValue })
        {
            UnitPose pose = TextLoops.Delta(new StyleLoop { Kind = kind }, t);
            Assert.True(double.IsFinite(pose.X) && double.IsFinite(pose.Y) && double.IsFinite(pose.Scale) && double.IsFinite(pose.Rotation));
        }
    }

    [Theory]
    [InlineData(TextLoopKind.Wobble)]
    [InlineData(TextLoopKind.Shake)]
    [InlineData(TextLoopKind.Pulse)]
    [InlineData(TextLoopKind.Float)]
    public void Roundtrip_clone_and_equality(TextLoopKind kind)
    {
        Project project = ValidProject();
        Clip title = MakeTitle("loop", 40, 0, 3);
        project.Sequences[0].VideoTracks[0].Clips.Add(title);
        TextStyle style = title.Generator!.Style!;
        style.Loop = new StyleLoop { Kind = kind, Amount = 1.7, Speed = 2.3 };
        TextStyle clone = style.Clone();
        Assert.NotSame(style.Loop, clone.Loop);
        Assert.True(style.ContentEquals(clone));
        clone.Loop!.Kind = (TextLoopKind)99;
        Assert.False(style.ContentEquals(clone));
        clone = style.Clone();
        clone.Loop!.Amount = 0.2;
        Assert.False(style.ContentEquals(clone));
        clone = style.Clone();
        clone.Loop!.Speed = 3;
        Assert.False(style.ContentEquals(clone));
        clone.Loop = null;
        Assert.False(style.ContentEquals(clone));
        Project read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.True(style.ContentEquals(read.Sequences[0].VideoTracks[0].Clips[^1].Generator!.Style!));
    }

    public static IEnumerable<object[]> InvalidLoops()
    {
        yield return [new StyleLoop { Kind = (TextLoopKind)99 }];
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 0.09, 2.01 })
        {
            yield return [new StyleLoop { Amount = value }];
        }

        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1, 0, 0.09, 20.01 })
        {
            yield return [new StyleLoop { Speed = value }];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidLoops))]
    public void Validator_rejects_invalid_loop(StyleLoop loop)
    {
        Project project = ValidProject();
        Clip title = MakeTitle("loop", 40, 0, 3);
        project.Sequences[0].VideoTracks[0].Clips.Add(title);
        title.Generator!.Style!.Loop = loop;
        Assert.NotEmpty(ModelValidator.Validate(project));
    }
}
