using System.Numerics;
using Rodaje.Analysis.Video;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests.Video;

/// <summary>
/// Seguimiento de una región sobre un objetivo sintético: un disco texturado que se mueve, gira y cambia de tamaño
/// sobre un fondo quieto. El error se mide en píxeles contra el movimiento verdadero.
/// </summary>
[Collection(VideoTestGroup.Name)]
public sealed class RegionTrackerTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 360;
    private const int Frames = 60;
    private static readonly Vector2 Center = new(0.05f, 0.02f);
    private const float Radius = 0.17f;

    private static readonly ShakyScene Object = new(11);
    private static readonly ShakyScene Background = new(29);

    /// <summary>Lleva un punto del disco (mundo, en altos centrados) a su lugar en el cuadro <paramref name="k"/>.</summary>
    private static Similarity Path(int k) => Similarity.FromParts(
        0.35 * Math.Sin(k / 25.0), 1 + (0.18 * Math.Sin(k / 30.0)), (0.0035 * k) - 0.05, 0.0015 * k);

    [Fact]
    public void Follows_a_moving_rotating_and_scaling_target_within_a_pixel()
    {
        var tracker = new RegionTracker(Width, Height);
        Vector2[] region = Circle(Path(0), 0.8f * Radius, 24);
        Vector2[] probes = [ToPixels(Path(0), Center), ToPixels(Path(0), Center + new Vector2(0.12f, 0)), ToPixels(Path(0), Center + new Vector2(0, -0.12f))];
        tracker.StartAnalysis(Frame(0), region, probes);

        double worst = 0, worstAnchor = 0;
        for (int k = 1; k < Frames; k++)
        {
            RegionStep step = tracker.StepAnalysis(Frame(k));
            Assert.False(step.Lost);
            Vector2[] truth = [ToPixels(Path(k), Center), ToPixels(Path(k), Center + new Vector2(0.12f, 0)), ToPixels(Path(k), Center + new Vector2(0, -0.12f))];
            for (int i = 0; i < probes.Length; i++)
            {
                (double x, double y) = step.Motion.Apply(probes[i].X, probes[i].Y);
                worst = Math.Max(worst, Vector2.Distance(new Vector2((float)x, (float)y), truth[i]));
                worstAnchor = Math.Max(worstAnchor, Vector2.Distance(step.Anchors[i], truth[i]));
            }
        }

        output.WriteLine($"Error máximo en {Frames} cuadros: región {worst:0.00} px, puntos sueltos {worstAnchor:0.00} px.");
        Assert.InRange(worst, 0, 1.0);
        Assert.InRange(worstAnchor, 0, 1.5);
    }

    [Fact]
    public void Tracks_backwards_as_well()
    {
        var tracker = new RegionTracker(Width, Height);
        int last = Frames - 1;
        Vector2 probe = ToPixels(Path(last), Center);
        tracker.StartAnalysis(Frame(last), Circle(Path(last), 0.8f * Radius, 24));
        RegionStep? step = null;
        for (int k = last - 1; k >= 0; k--)
        {
            step = tracker.StepAnalysis(Frame(k));
        }

        (double x, double y) = step!.Motion.Apply(probe.X, probe.Y);
        Assert.InRange(Vector2.Distance(new Vector2((float)x, (float)y), ToPixels(Path(0), Center)), 0, 1.0);
        Assert.InRange(step.Motion.Scale, (Path(0).Scale / Path(last).Scale) - 0.01, (Path(0).Scale / Path(last).Scale) + 0.01);
    }

    [Fact]
    public void A_flat_region_is_lost_and_stays_put()
    {
        var tracker = new RegionTracker(Width, Height);
        float[] flat = new float[Width * Height];
        Array.Fill(flat, 128);
        tracker.StartAnalysis(flat, [new(100, 100), new(200, 100), new(200, 200), new(100, 200)]);
        RegionStep step = tracker.StepAnalysis(flat);
        Assert.True(step.Lost);
        Assert.Equal(Similarity.Identity, step.Motion);
    }

    private static float[] Frame(int k)
    {
        float[] objectFrame = new float[Width * Height];
        float[] background = new float[Width * Height];
        Object.Render(Path(k), Width, Height, objectFrame);
        Background.Render(Similarity.Identity, Width, Height, background);
        Similarity back = Path(k).Inverse();
        for (int j = 0; j < Height; j++)
        {
            for (int i = 0; i < Width; i++)
            {
                (double wx, double wy) = back.Apply((i + 0.5 - (Width / 2.0)) / Height, (j + 0.5 - (Height / 2.0)) / Height);
                double d = Math.Sqrt(((wx - Center.X) * (wx - Center.X)) + ((wy - Center.Y) * (wy - Center.Y)));
                // Borde del disco suavizado un píxel, como lo vería una cámara.
                float t = (float)Math.Clamp(((Radius - d) * Height * Path(k).Scale) + 0.5, 0, 1);
                int n = (j * Width) + i;
                background[n] += t * (objectFrame[n] - background[n]);
            }
        }

        return background;
    }

    private static Vector2 ToPixels(Similarity s, Vector2 world)
    {
        (double x, double y) = s.Apply(world.X, world.Y);
        return new Vector2((float)((x * Height) + (Width / 2.0) - 0.5), (float)((y * Height) + (Height / 2.0) - 0.5));
    }

    private static Vector2[] Circle(Similarity s, float radius, int points) =>
        [.. Enumerable.Range(0, points).Select(i =>
        {
            float a = i * MathF.Tau / points;
            return ToPixels(s, Center + (radius * new Vector2(MathF.Cos(a), MathF.Sin(a))));
        })];
}
