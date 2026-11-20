using System.Diagnostics;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Performance;

/// <summary>La gráfica del inspector con miles de keyframes: se muestrea por los píxeles que tiene, no por 48 muestras por tramo.</summary>
public class KeyframeGraphScalingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static Parameter Wobble(int count)
    {
        var p = new Parameter();
        for (int i = 0; i < count; i++)
        {
            p.Keyframes.Add(new Keyframe(Time.FromSeconds(i / 30.0), Math.Sin(i * 0.9), Interpolation.EaseInOut));
        }

        return p;
    }

    [Theory]
    [InlineData(50)]
    [InlineData(1250)]
    public void Las_muestras_no_pasan_del_tope_de_pixeles(int count)
    {
        Parameter p = Wobble(count);
        const int Cap = 1200;
        IReadOnlyList<GraphPoint> points = KeyframeGraph.Sample(p, GraphMode.Value, maxPoints: Cap);
        Assert.True(points.Count <= Math.Max(Cap, (2 * (count - 1)) + 1), $"{points.Count} puntos");
        Assert.Equal(0, points[0].Seconds);
        Assert.Equal((p.Keyframes[^1].Time - p.Keyframes[0].Time).ToSeconds(), points[^1].Seconds, 3);
    }

    [Fact]
    public void Muestrear_1250_keyframes_cuesta_milisegundos()
    {
        Parameter p = Wobble(1250);
        KeyframeGraph.Sample(p, GraphMode.Value, maxPoints: 1200);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            KeyframeGraph.Sample(p, GraphMode.Value, maxPoints: 1200);
        }

        double ms = sw.Elapsed.TotalMilliseconds / 10;
        sw.Restart();
        int old = KeyframeGraph.Sample(p, GraphMode.Value).Count;
        output.WriteLine($"1250 keyframes: {ms:F2} ms y {KeyframeGraph.Sample(p, GraphMode.Value, maxPoints: 1200).Count} puntos con tope; sin tope {sw.Elapsed.TotalMilliseconds:F2} ms y {old} puntos");
        Assert.True(ms < 50, $"{ms} ms por dibujo");
    }

    [Fact]
    public void El_recorrido_del_visor_con_1000_keyframes_no_pasa_de_su_tope()
    {
        var transform = new EffectInstance { Id = "t", Type = BuiltInEffects.Transform };
        foreach (string name in new[] { BuiltInEffects.TransformParameters.X, BuiltInEffects.TransformParameters.Y })
        {
            transform.Parameters[name] = Wobble(1000);
        }

        var sw = Stopwatch.StartNew();
        IReadOnlyList<System.Numerics.Vector2> points = Rodaje.App.Views.Player.MotionPathView.Sample(transform, 1920, 1080);
        double ms = sw.Elapsed.TotalMilliseconds;
        output.WriteLine($"recorrido de 1000 keyframes: {points.Count} puntos, {ms:F1} ms (antes ~24.000 puntos)");
        Assert.InRange(points.Count, 999, 2600);
        Assert.True(ms < 500, $"{ms} ms");
    }
}
