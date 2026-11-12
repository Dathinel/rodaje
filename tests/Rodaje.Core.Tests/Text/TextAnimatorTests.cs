using Rodaje.Core.Model;
using Rodaje.Core.Text;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Core.Tests.Text;

/// <summary>La animación por unidades: desfase, orden, curvas con las interpolaciones de los keyframes y determinismo.</summary>
public class TextAnimatorTests
{
    private static StyleAnimation Fade(double stagger, TextUnit unit = TextUnit.Letter, UnitOrder order = UnitOrder.Forward) => new()
    {
        DurationSeconds = 1,
        Unit = unit,
        Stagger = stagger,
        Order = order,
        Curves = new() { [T.Opacity] = [new(0, 0), new(1, 1)] },
    };

    [Fact]
    public void Block_animations_without_blur_are_not_per_unit()
    {
        Assert.Null(TextAnimator.From(new StyleAnimation { DurationSeconds = 1, Curves = new() { [T.Y] = [new(0, 10)] } }));
        Assert.NotNull(TextAnimator.From(new StyleAnimation { DurationSeconds = 1, Curves = new() { [StyleAnimation.Blur] = [new(0, 10)] } }));
        Assert.NotNull(TextAnimator.From(Fade(0)));
        Assert.Null(TextAnimator.From(null));
    }

    [Fact]
    public void Without_stagger_every_unit_moves_together()
    {
        TextAnimator a = TextAnimator.From(Fade(0))!;
        Span<double> delays = stackalloc double[5];
        TextAnimator.Delays(UnitOrder.Forward, delays);
        Span<UnitPose> poses = stackalloc UnitPose[5];
        a.Evaluate(0.25, delays, poses);
        foreach (UnitPose pose in poses)
        {
            Assert.Equal(0.25, pose.Opacity, 9);
            Assert.Equal(1, pose.Scale);
        }
    }

    [Fact]
    public void Stagger_starts_each_unit_in_turn_and_all_finish_together()
    {
        TextAnimator a = TextAnimator.From(Fade(0.5))!;
        double[] delays = new double[3];
        TextAnimator.Delays(UnitOrder.Forward, delays);
        Assert.Equal([0, 0.5, 1], delays);

        // La fase dura 1: la unidad k arranca en 0,5·retraso y dura 0,5.
        Assert.Equal(0.5, a.Evaluate(0.25, 0).Opacity, 9);
        Assert.Equal(0, a.Evaluate(0.25, 0.5).Opacity, 9);
        Assert.Equal(0.5, a.Evaluate(0.5, 0.5).Opacity, 9);
        Assert.Equal(0, a.Evaluate(0.5, 1).Opacity, 9);
        Assert.All(delays, d => Assert.Equal(1, a.Evaluate(1, d).Opacity, 9));
        Assert.All(delays, d => Assert.Equal(0, a.Evaluate(0, d).Opacity, 9));
    }

    [Fact]
    public void Full_stagger_is_a_typewriter_each_letter_jumps_in_its_turn()
    {
        TextAnimator a = TextAnimator.From(TextAnimationPresets.Find("maquina")!.In())!;
        double[] delays = new double[4];
        TextAnimator.Delays(UnitOrder.Forward, delays);
        double[] Visible(double p) => [.. delays.Select(d => a.Evaluate(p, d).Opacity)];
        Assert.Equal([1, 0, 0, 0], Visible(0));
        Assert.Equal([1, 1, 0, 0], Visible(0.4));
        Assert.Equal([1, 1, 1, 0], Visible(0.7));
        Assert.Equal([1, 1, 1, 1], Visible(1));

        // La salida es la misma al revés: van desapareciendo en el mismo orden.
        TextAnimator exit = TextAnimator.From(TextAnimationPresets.Find("maquina")!.Out())!;
        Assert.Equal([1, 1, 1, 1], delays.Select(d => exit.Evaluate(0.01, d).Opacity).Skip(1).Prepend(1).ToArray());

        Assert.Equal([0, 0, 1, 1], delays.Select(d => exit.Evaluate(0.4, d).Opacity).ToArray());
        Assert.Equal([0, 0, 0, 0], delays.Select(d => exit.Evaluate(1, d).Opacity).ToArray());
    }

    [Theory]
    [InlineData(UnitOrder.Forward, new[] { 0, 0.25, 0.5, 0.75, 1 })]
    [InlineData(UnitOrder.Backward, new[] { 1, 0.75, 0.5, 0.25, 0 })]
    [InlineData(UnitOrder.Center, new[] { 1, 0.5, 0, 0.5, 1 })]
    [InlineData(UnitOrder.Edges, new[] { 0, 0.5, 1, 0.5, 0 })]
    public void Orders_spread_the_delays_from_zero_to_one(UnitOrder order, double[] expected)
    {
        double[] delays = new double[5];
        TextAnimator.Delays(order, delays);
        Assert.Equal(expected, delays);
    }

    [Fact]
    public void Random_order_is_a_fixed_permutation_of_the_forward_delays()
    {
        double[] first = new double[40];
        double[] second = new double[40];
        TextAnimator.Delays(UnitOrder.Random, first);
        TextAnimator.Delays(UnitOrder.Random, second);
        Assert.Equal(first, second);
        double[] forward = new double[40];
        TextAnimator.Delays(UnitOrder.Forward, forward);
        Assert.Equal(forward, first.Order().ToArray());
        Assert.NotEqual(forward, first);
    }

    [Fact]
    public void Curves_use_the_keyframe_easings_including_bounce()
    {
        TextAnimator a = TextAnimator.From(TextAnimationPresets.Find("rebote")!.In(stagger: 0))!;
        double end = a.Evaluate(1, 0).Y;
        Assert.Equal(0, end, 9);
        Assert.Equal(-120, a.Evaluate(0, 0).Y, 9);

        // Con rebote la letra llega al suelo antes del final y vuelve a subir: la curva no es monótona.
        double[] ys = [.. Enumerable.Range(0, 101).Select(i => a.Evaluate(i / 100.0, 0).Y)];
        int turns = 0;
        for (int i = 2; i < ys.Length; i++)
        {
            turns += Math.Sign(ys[i] - ys[i - 1]) != Math.Sign(ys[i - 1] - ys[i - 2]) && ys[i] != ys[i - 1] ? 1 : 0;
        }

        Assert.True(turns >= 2, $"El rebote debería cambiar de sentido varias veces ({turns}).");
        Assert.Equal(KeyframeMath.Bounce(0.5), (a.Evaluate(0.5, 0).Y + 120) / 120, 9);
    }

    [Fact]
    public void Every_preset_starts_hidden_or_moved_and_rests_at_the_end()
    {
        foreach (TextAnimationPreset preset in TextAnimationPresets.All)
        {
            TextAnimator entry = TextAnimator.From(preset.In())!;
            Assert.NotNull(entry);
            UnitPose done = entry.Evaluate(1, 1);
            Assert.Equal(UnitPose.Rest.Opacity, done.Opacity, 6);
            Assert.Equal(1, done.Scale, 6);
            Assert.Equal(0, done.X, 6);
            Assert.Equal(0, done.Y, 6);
            Assert.Equal(0, done.Blur, 6);
            Assert.NotEqual(UnitPose.Rest, entry.Evaluate(0, 1));

            TextAnimator exit = TextAnimator.From(preset.Out())!;
            Assert.Equal(1, exit.Evaluate(0, 1).Opacity, 6);
            Assert.Equal(entry.Evaluate(0, 1).Opacity, exit.Evaluate(1, 0).Opacity, 6);
            Assert.Same(preset, TextAnimationPresets.Match(preset.In(), entry: true));
            Assert.Same(preset, TextAnimationPresets.Match(preset.Out(TextUnit.Word, 0.3, 2), entry: false));
        }
    }

    [Fact]
    public void The_raster_scale_covers_the_largest_unit_and_elastic_overshoot()
    {
        Assert.Equal(1, TextAnimator.From(Fade(0))!.MaxScale);
        Assert.Equal(1.15, TextAnimator.From(TextAnimationPresets.Find("desenfoque")!.In())!.MaxScale, 9);
        Assert.True(TextAnimator.From(TextAnimationPresets.Find("elastico")!.In())!.MaxScale > 1.2);
    }

    [Fact]
    public void Evaluating_reserves_no_memory()
    {
        TextAnimator a = TextAnimator.From(TextAnimationPresets.Find("desenfoque")!.In())!;
        double[] delays = new double[200];
        TextAnimator.Delays(UnitOrder.Random, delays);
        UnitPose[] poses = new UnitPose[200];
        a.Evaluate(0.5, delays, poses);

        // La ventana con menos bytes de hasta tres: una reserva por llamada sale en todas; en la CI de Windows una sola
        // ventana contó 6576 B de una vez (0,33 B por pose: del runtime en este hilo, no de Evaluate).
        long fewest = long.MaxValue;
        for (int round = 0; round < 3 && fewest != 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                a.Evaluate(i / 100.0, delays, poses);
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, fewest);
    }
}
