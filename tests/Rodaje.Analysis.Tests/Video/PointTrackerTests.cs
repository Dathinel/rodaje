using System.Diagnostics;
using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.Core.Effects;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests.Video;

/// <summary>
/// Seguidor de puntos sobre secuencias sintéticas con la posición verdadera conocida al subpíxel: traslación, giro,
/// escala, perspectiva, ruido, desenfoque de movimiento y oclusión. El error se mide en píxeles del cuadro.
/// </summary>
[Collection(VideoTestGroup.Name)]
public sealed class PointTrackerTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 360;
    private static readonly PlanarScene Scene = new(7);
    private static readonly Vector2 Pattern = new(31);
    private static readonly Vector2 Search = new(81);

    /// <summary>Cámara que corre, gira y se acerca: plano → imagen en el cuadro <paramref name="k"/>.</summary>
    private static Homography Moving(double k) =>
        Homography.Translation(320 + (2.37 * k) + (2 * Math.Sin(k / 3.0)), 180 + (1.21 * k))
            .After(Homography.Similarity(1 + (0.004 * k), 0.006 * k, 0, 0))
            .After(Homography.Translation(-200, -120));

    private static Homography Sliding(double k) => Homography.Translation(110 + (3.3 * k), 50 + (1.7 * k));

    /// <summary>Un plano que se inclina hacia atrás (perspectiva) mientras se desplaza.</summary>
    private static Homography Tilting(double k)
    {
        double tilt = 0.000012 * k;
        return new Homography(1, 0.02 * Math.Sin(k / 9.0), 100 + (1.1 * k), 0.01 * k / 30, 1, 40 + (0.6 * k), tilt / 3, tilt, 1).Normalized();
    }

    [Fact]
    public void One_point_on_a_clean_moving_plane_stays_under_a_quarter_pixel()
    {
        double worst = Run(Moving, 40, [new Vector2(320, 180)], out double mean);
        output.WriteLine($"Un punto, traslación con giro y escala, 40 cuadros: error medio {mean:0.000} px, máximo {worst:0.000} px.");
        Assert.InRange(worst, 0, 0.25);
    }

    [Fact]
    public void Two_points_follow_rotation_and_scale()
    {
        double worst = Run(Moving, 40, [new Vector2(230, 150), new Vector2(420, 230)], out double mean);
        output.WriteLine($"Dos puntos con giro y escala: error medio {mean:0.000} px, máximo {worst:0.000} px.");
        Assert.InRange(worst, 0, 0.25);
    }

    [Fact]
    public void Four_corners_follow_perspective()
    {
        Vector2[] corners = [new(200, 110), new(440, 115), new(450, 260), new(190, 250)];
        double worst = Run(Tilting, 40, corners, out double mean);
        output.WriteLine($"Cuatro esquinas con perspectiva: error medio {mean:0.000} px, máximo {worst:0.000} px.");
        Assert.InRange(worst, 0, 0.25);

        // Las esquinas seguidas dan la homografía del plano: un punto del medio cae donde debe.
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, Tilting(0)), [.. corners.Select(Target)]);
        PointResult[] last = [];
        for (int k = 1; k <= 30; k++)
        {
            last = tracker.Step(Scene.Render(Width, Height, Tilting(k)));
        }

        Homography motion = Homography.FromQuads(corners, [.. last.Select(r => r.Position)])!.Value;
        var middle = new Vector2(330, 190);
        Vector2 truth = PlanarScene.Project(Tilting(30), PlanarScene.Unproject(Tilting(0), middle));
        output.WriteLine($"Punto del medio por la homografía de las esquinas: {Vector2.Distance(motion.Apply(middle), truth):0.000} px.");
        Assert.InRange(Vector2.Distance(motion.Apply(middle), truth), 0, 0.3);
    }

    [Fact]
    public void Noise_and_motion_blur_stay_under_half_a_pixel()
    {
        var random = new Random(3);
        Vector2[] start = [new(300, 170), new(380, 210)];
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, Moving(0), 4, random), [.. start.Select(Target)]);
        double worst = 0;
        for (int k = 1; k <= 25; k++)
        {
            // Obturador de 180°: la imagen promedia la mitad del cuadro centrada en k.
            float[] frame = Scene.RenderBlurred(Width, Height, t => Moving(k - 0.25 + (0.5 * t)), 7);
            for (int i = 0; i < frame.Length; i++)
            {
                double u = 1 - random.NextDouble(), v = random.NextDouble();
                frame[i] += (float)(4 * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
            }

            PointResult[] step = tracker.Step(frame);
            for (int i = 0; i < start.Length; i++)
            {
                Assert.NotEqual(PointState.Lost, step[i].State);
                worst = Math.Max(worst, Vector2.Distance(step[i].Position, Truth(Moving, start[i], k)));
            }
        }

        output.WriteLine($"Ruido σ = 4 y desenfoque de movimiento (180°): error máximo {worst:0.000} px.");
        Assert.InRange(worst, 0, 0.5);
    }

    [Fact]
    public void A_brief_occlusion_is_marked_and_the_point_is_found_again()
    {
        Vector2 start = new(320, 180);
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, Moving(0)), [Target(start)]);
        int lost = 0;
        double afterError = 0;
        for (int k = 1; k <= 30; k++)
        {
            Vector2 truth = Truth(Moving, start, k);
            (Vector2, Vector2)? box = k is >= 10 and < 16 ? (truth - new Vector2(30), truth + new Vector2(30)) : null;
            PointResult[] step = tracker.Step(Scene.Render(Width, Height, Moving(k), occluder: box));
            if (k is >= 10 and < 16)
            {
                lost += step[0].State == PointState.Lost ? 1 : 0;
                Assert.Equal(0, step[0].Confidence);
            }
            else if (k >= 17)
            {
                Assert.Equal(PointState.Good, step[0].State);
                afterError = Math.Max(afterError, Vector2.Distance(step[0].Position, truth));
            }
        }

        output.WriteLine($"Tapado 6 cuadros: {lost} marcados como perdidos; tras reencontrarlo, error máximo {afterError:0.000} px.");
        Assert.Equal(6, lost);
        Assert.InRange(afterError, 0, 0.25);
    }

    [Fact]
    public void A_hidden_point_follows_the_others_while_it_is_lost()
    {
        Vector2[] start = [new(250, 160), new(400, 220)];
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, Sliding(0)), [.. start.Select(Target)]);
        double predicted = 0;
        for (int k = 1; k <= 20; k++)
        {
            Vector2 truth = Truth(Sliding, start[1], k);
            (Vector2, Vector2)? box = k >= 8 ? (truth - new Vector2(28), truth + new Vector2(28)) : null;
            PointResult[] step = tracker.Step(Scene.Render(Width, Height, Sliding(k), occluder: box));
            if (k >= 8)
            {
                Assert.Equal(PointState.Lost, step[1].State);
                predicted = Math.Max(predicted, Vector2.Distance(step[1].Position, truth));
            }
        }

        output.WriteLine($"Punto tapado llevado por el otro con el desplazamiento del otro: error máximo {predicted:0.00} px.");
        Assert.InRange(predicted, 0, 1);
    }

    [Fact]
    public void A_flat_image_loses_every_point()
    {
        float[] flat = new float[Width * Height];
        Array.Fill(flat, 128);
        var tracker = new PointTracker(Width, Height);
        tracker.Start(flat, [Target(new Vector2(320, 180))]);
        for (int k = 0; k < PointTracker.MaxLostFrames; k++)
        {
            Assert.Equal(PointState.Lost, tracker.Step(flat)[0].State);
        }

        Assert.True(tracker.IsLost);
    }

    [Fact]
    public void Works_backwards_too()
    {
        Vector2 start = Truth(Moving, new Vector2(320, 180), 30);
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, Moving(30)), [Target(start)]);
        PointResult[] step = [];
        for (int k = 29; k >= 0; k--)
        {
            step = tracker.Step(Scene.Render(Width, Height, Moving(k)));
        }

        Vector2 truth = PlanarScene.Project(Moving(0), PlanarScene.Unproject(Moving(30), start));
        Assert.InRange(Vector2.Distance(step[0].Position, truth), 0, 0.25);
    }

    /// <summary>
    /// La medida estricta: cuadros por segundo de reloj contra un umbral fijo. Un runner de la CI cargado dio 13 cuadros/s
    /// en 4K (umbral 15), así que corre fuera de la CI (TiempoReal); en la CI queda
    /// <see cref="The_cost_per_frame_does_not_depend_on_the_frame_size"/>, que compara los dos tamaños en la misma corrida.
    /// </summary>
    [Theory]
    [Trait("Categoria", "TiempoReal")]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public void Throughput_does_not_depend_on_the_frame_size(int width, int height)
    {
        const int frames = 60;
        var clip = new ShiftingClip(width, height, frames);
        var tracker = clip.Start(out PointTarget[] targets);
        var watch = Stopwatch.StartNew();
        PointResult[] last = [];
        for (int k = 1; k < frames; k++)
        {
            last = clip.Step(tracker, k);
        }

        watch.Stop();
        double fps = (frames - 1) / watch.Elapsed.TotalSeconds;
        output.WriteLine($"{width}×{height}, 4 puntos (patrón 41, búsqueda 121): {fps:0} cuadros/s solo el seguidor.");
        ShiftingClip.AssertFollowed(last, targets, frames - 1);
        // En Debug (sin optimizar) va unas cuatro veces más lento; el umbral vale para los dos.
        Assert.True(fps > 15, $"{fps:0} cuadros/s");
    }

    /// <summary>
    /// El costo del seguidor solo depende de sus ventanas, no del cuadro: 1080p y 4K se siguen intercalados, cuadro a
    /// cuadro, y se compara el paso más rápido de cada uno. La carga del runner afecta a los dos por igual y el mínimo es
    /// la medida que menos depende de ella; si el seguidor recorriera el cuadro entero, el 4K (cuatro veces el área) se
    /// iría bien por encima de la cota.
    /// </summary>
    [Fact]
    public void The_cost_per_frame_does_not_depend_on_the_frame_size()
    {
        const int frames = 40;
        var small = new ShiftingClip(1920, 1080, frames);
        var large = new ShiftingClip(3840, 2160, frames);
        PointTracker smallTracker = small.Start(out PointTarget[] smallTargets);
        PointTracker largeTracker = large.Start(out PointTarget[] largeTargets);
        double smallBest = double.MaxValue, largeBest = double.MaxValue;
        PointResult[] smallLast = [], largeLast = [];
        for (int k = 1; k < frames; k++)
        {
            // Se alterna cuál va primero para que ninguno herede siempre la caché caliente del otro.
            if (k % 2 == 0)
            {
                smallLast = Timed(small, smallTracker, k, ref smallBest);
                largeLast = Timed(large, largeTracker, k, ref largeBest);
            }
            else
            {
                largeLast = Timed(large, largeTracker, k, ref largeBest);
                smallLast = Timed(small, smallTracker, k, ref smallBest);
            }
        }

        output.WriteLine($"Paso más rápido: 1920×1080 {smallBest:0.00} ms, 3840×2160 {largeBest:0.00} ms ({largeBest / smallBest:0.00}×).");
        ShiftingClip.AssertFollowed(smallLast, smallTargets, frames - 1);
        ShiftingClip.AssertFollowed(largeLast, largeTargets, frames - 1);
        Assert.True(largeBest < 2 * smallBest, $"4K {largeBest:0.00} ms contra 1080p {smallBest:0.00} ms por cuadro.");

        static PointResult[] Timed(ShiftingClip clip, PointTracker tracker, int k, ref double best)
        {
            long start = Stopwatch.GetTimestamp();
            PointResult[] result = clip.Step(tracker, k);
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return result;
        }
    }

    private static PointTarget Target(Vector2 p) => new(p, Pattern, Search);

    private static Vector2 Truth(Func<double, Homography> view, Vector2 start, int k) =>
        PlanarScene.Project(view(k), PlanarScene.Unproject(view(0), start));

    private static double Run(Func<double, Homography> view, int frames, Vector2[] start, out double mean)
    {
        var tracker = new PointTracker(Width, Height);
        tracker.Start(Scene.Render(Width, Height, view(0)), [.. start.Select(Target)]);
        double worst = 0, sum = 0;
        int count = 0;
        for (int k = 1; k <= frames; k++)
        {
            PointResult[] step = tracker.Step(Scene.Render(Width, Height, view(k)));
            for (int i = 0; i < start.Length; i++)
            {
                Assert.Equal(PointState.Good, step[i].State);
                double error = Vector2.Distance(step[i].Position, Truth(view, start[i], k));
                worst = Math.Max(worst, error);
                sum += error;
                count++;
            }
        }

        mean = sum / count;
        return worst;
    }

    /// <summary>
    /// Una textura grande que se desplaza (2, 1) píxeles por cuadro: cada cuadro es una vista del mismo plano con otro
    /// comienzo y el paso de fila del plano, así que armar los 60 cuadros de un 4K no cuesta nada.
    /// </summary>
    private sealed class ShiftingClip
    {
        private const int MarginX = 400;
        private const int MarginY = 200;
        private readonly byte[] _plane;
        private readonly int _width;
        private readonly int _height;

        public ShiftingClip(int width, int height, int frames)
        {
            Assert.True(2 * frames <= MarginX && frames <= MarginY);
            _width = width;
            _height = height;
            float[] plane = new PlanarScene(5).Render(width + MarginX, height + MarginY, Homography.Identity);
            _plane = new byte[plane.Length];
            for (int i = 0; i < plane.Length; i++)
            {
                _plane[i] = (byte)Math.Clamp(plane[i], 0, 255);
            }
        }

        private int Stride => _width + MarginX;

        public PointTracker Start(out PointTarget[] targets)
        {
            Vector2 c = new(_width / 2f, _height / 2f);
            targets = [.. new[] { c + new Vector2(-300, -200), c + new Vector2(300, -200), c + new Vector2(300, 200), c + new Vector2(-300, 200) }
                .Select(p => new PointTarget(p, new Vector2(41), new Vector2(121)))];
            var tracker = new PointTracker(_width, _height);
            tracker.Start(Frame(0), targets);
            return tracker;
        }

        public PointResult[] Step(PointTracker tracker, int k) => tracker.Step(Frame(k));

        public static void AssertFollowed(PointResult[] last, PointTarget[] targets, int k)
        {
            Assert.All(last, r => Assert.Equal(PointState.Good, r.State));
            Assert.InRange(Vector2.Distance(last[0].Position, targets[0].Position - new Vector2(2 * k, k)), 0, 0.3);
        }

        private LumaFrame Frame(int k)
        {
            int offset = (k * Stride) + (2 * k);
            return new LumaFrame(_plane.AsSpan(offset), _width, _height, Stride, LumaFormat.Gray8);
        }
    }
}
