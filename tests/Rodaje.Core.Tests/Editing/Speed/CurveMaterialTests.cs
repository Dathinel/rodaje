using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>
/// El caso del defecto: «Bala», «Héroe» y «Ninguna» sobre un clip de 10 s lo dejaban en 9,942 s, porque cada cambio
/// redondeaba el final al cuadro con la curva y el siguiente partía del material ya redondeado.
/// </summary>
public sealed class CurveMaterialTests
{
    public static TheoryData<int, int> FrameRates => new() { { 25, 1 }, { 30, 1 }, { 30000, 1001 }, { 24, 1 }, { 60, 1 } };

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void Bullet_then_hero_then_none_keeps_the_ten_seconds(int num, int den)
    {
        var fps = new Rational(num, den);

        // 10 s, o el cuadro más cercano si la cadencia no cae justo (a 29,97 fps, 10,01 s).
        Time ten = Time.FromSeconds(10).SnapToFrame(fps);
        TrimFixture f = Fixture(fps, ten);

        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Bullet));
        Assert.Equal(ten, f.Clip("c").SourceOut);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero));
        Assert.Equal(ten, f.Clip("c").SourceOut);
        Assert.Equal(f.Clip("c").End, f.Clip("c").End.SnapToFrame(fps));
        TimelineEdits.SetCurve(f.Ctx, "c", null);

        Clip clip = f.Clip("c");
        f.AssertValid();
        Assert.Null(clip.Speed);
        Assert.Equal(Time.Zero, clip.SourceIn);
        Assert.Equal(ten, clip.SourceOut);
        Assert.Equal(ten, clip.Duration);
    }

    [Fact]
    public void Every_preset_after_every_other_keeps_the_material_with_the_end_on_a_frame()
    {
        foreach (SpeedPreset first in SpeedPresets.All)
        {
            foreach (SpeedPreset second in SpeedPresets.All)
            {
                TrimFixture f = Fixture(Rational.Ntsc30, Time.FromFrames(210, Rational.Ntsc30));
                TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(first));
                TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(second));

                Clip clip = f.Clip("c");
                f.AssertValid();
                Assert.Equal(second, SpeedPresets.Match(clip.Speed!.Curve!));
                Assert.Equal(Time.FromFrames(210, Rational.Ntsc30), clip.SourceOut);
                Assert.Equal(clip.End, clip.End.SnapToFrame(Rational.Ntsc30));
            }
        }
    }

    [Fact]
    public void A_slow_speed_and_back_returns_the_original_material()
    {
        TrimFixture f = Fixture(Rational.Ntsc30, Time.FromFrames(299, Rational.Ntsc30));
        Time original = f.Clip("c").SourceOut;

        foreach (int hundredths in (int[])[37, 82, 69, 91, 13, 50])
        {
            TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(hundredths, 100));
            TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero));
        }

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(1, 1));
        f.AssertValid();
        Assert.Equal(original, f.Clip("c").SourceOut);
    }

    [Fact]
    public void A_clip_ending_between_frames_moves_to_the_nearest_frame_once()
    {
        TrimFixture f = Fixture(Rational.Ntsc30, Time.FromSeconds(10));
        Time frame = Time.FromFrames(1, Rational.Ntsc30);

        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Bullet));
        Time first = f.Clip("c").SourceOut;
        Assert.InRange((first - Time.FromSeconds(10)).Ticks, -frame.Ticks / 2, frame.Ticks / 2);
        foreach (SpeedPreset preset in SpeedPresets.All)
        {
            TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(preset));
            Assert.Equal(first, f.Clip("c").SourceOut);
        }

        TimelineEdits.SetCurve(f.Ctx, "c", null);
        f.AssertValid();
        Assert.Equal(first, f.Clip("c").SourceOut);
    }

    /// <summary>Un medio de 40 s y un clip de 0 a <paramref name="length"/> en v1, en una secuencia a <paramref name="fps"/>.</summary>
    private static TrimFixture Fixture(Rational fps, Time length)
    {
        var f = new TrimFixture();
        f.Sequence.FrameRate = fps;
        f.Project.Media.Add(new MediaRef { Id = "m4", Name = "cuatro", Duration = Time.FromSeconds(40), HasVideo = true, HasAudio = true });
        f.Track("v1").Clips.Add(new Clip { Id = "c", MediaId = "m4", SourceIn = Time.Zero, SourceOut = length, Start = Time.Zero });
        return f;
    }
}
