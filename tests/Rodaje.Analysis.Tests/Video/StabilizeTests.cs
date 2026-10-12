using System.Diagnostics;
using Rodaje.Analysis.Video;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests.Video;

[Collection(VideoTestGroup.Name)]
public sealed class StabilizeTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 360;

    private static (Similarity[] Truth, FrameMotion[] Estimated) Analyze(int frames, int seed, double jitter = 0.012, double degrees = 0.4)
    {
        var scene = new ShakyScene(seed);
        Similarity[] camera = ShakyScene.Camera(frames, seed, jitter, degrees);
        var estimator = new MotionEstimator(Width * 3, Height * 3);
        Assert.Equal(Width, estimator.AnalysisWidth);
        float[] image = new float[Width * Height];
        foreach (Similarity c in camera)
        {
            scene.Render(c, Width, Height, image);
            estimator.PushAnalysis(image);
        }

        return (camera, [.. estimator.Motions]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Frame_to_frame_motion_is_recovered_to_a_twentieth_of_a_pixel(int seed)
    {
        (Similarity[] camera, FrameMotion[] motions) = Analyze(60, seed);
        double worstShift = 0, worstAngle = 0;
        int fewest = int.MaxValue;
        for (int k = 1; k < camera.Length; k++)
        {
            Similarity truth = camera[k].After(camera[k - 1].Inverse());
            Similarity found = motions[k].Motion;
            // Error en píxeles de análisis de dónde cae cada esquina del cuadro.
            foreach ((double x, double y) in new[] { (-0.89, -0.5), (0.89, -0.5), (-0.89, 0.5), (0.89, 0.5), (0.0, 0.0) })
            {
                (double tx, double ty) = truth.Apply(x, y);
                (double fx, double fy) = found.Apply(x, y);
                worstShift = Math.Max(worstShift, Math.Sqrt(((tx - fx) * (tx - fx)) + ((ty - fy) * (ty - fy))) * Height);
            }

            worstAngle = Math.Max(worstAngle, Math.Abs(truth.Angle - found.Angle) * 180 / Math.PI);
            fewest = Math.Min(fewest, motions[k].Inliers);
        }

        output.WriteLine($"semilla {seed}: error máximo {worstShift:F3} px (de {Height}), giro {worstAngle:F4}°, mínimo {fewest} esquinas coincidentes");
        Assert.True(worstShift < 0.05, $"{worstShift:F3} px");
        Assert.True(fewest >= 100, $"{fewest}");
    }

    /// <summary>Criterio de la fase 7: el movimiento residual baja al menos un 70 % con un temblor sintético conocido.</summary>
    [Theory]
    [InlineData(3, 0.5)]
    [InlineData(4, 0.5)]
    [InlineData(5, 1.0)]
    public void Known_synthetic_shake_drops_by_at_least_70_percent(int seed, double intensity)
    {
        (Similarity[] camera, FrameMotion[] motions) = Analyze(240, seed);
        StabilizationResult result = CameraPath.Stabilize([.. motions.Select(m => m.Motion)], 30, Width / (double)Height,
            new StabilizeOptions(intensity));
        // Lo que se ve: la cámara verdadera seguida de la corrección calculada con el movimiento estimado.
        Similarity[] shown = [.. camera.Select((c, k) => result.Corrections[k].After(c))];
        double before = CameraPath.Shake(camera, Width / (double)Height) * Height;
        double after = CameraPath.Shake(shown, Width / (double)Height) * Height;
        double drop = 1 - (after / before);
        output.WriteLine($"semilla {seed}, intensidad {intensity}: temblor {before:F3} → {after:F3} px/cuadro² (−{drop:P1}), recorte ×{result.Zoom:F3}");
        Assert.True(drop >= 0.70, $"{drop:P1}");
        Assert.InRange(result.Zoom, 1, 1.5);
    }

    [Fact]
    public void A_steady_pan_is_left_alone()
    {
        (Similarity[] camera, FrameMotion[] motions) = Analyze(90, 6, jitter: 0, degrees: 0);
        StabilizationResult result = CameraPath.Stabilize([.. motions.Select(m => m.Motion)], 30, Width / (double)Height,
            new StabilizeOptions(0.5, AutoCrop: false));
        double worst = 0;
        foreach (Similarity c in result.Corrections)
        {
            (double x, double y) = c.Apply(0.89, 0.5);
            worst = Math.Max(worst, Math.Sqrt(((x - 0.89) * (x - 0.89)) + ((y - 0.5) * (y - 0.5))) * Height);
        }

        output.WriteLine($"panorámica sin temblor: la corrección mueve una esquina como mucho {worst:F3} px");
        Assert.True(worst < 1.5, $"{worst:F3} px");
    }

    [Fact]
    public void Zero_intensity_does_not_correct()
    {
        Similarity[] camera = ShakyScene.Camera(50, 7);
        Similarity[] motions = [.. camera.Select((c, k) => k == 0 ? Similarity.Identity : c.After(camera[k - 1].Inverse()))];
        StabilizationResult result = CameraPath.Stabilize(motions, 30, 16 / 9.0, new StabilizeOptions(0));
        Assert.All(result.Corrections, c => Assert.Equal(Similarity.Identity, c));
        Assert.Equal(1, result.Zoom);
    }

    [Fact]
    public void Auto_crop_zoom_covers_every_frame()
    {
        Similarity[] camera = ShakyScene.Camera(200, 8);
        Similarity[] motions = [.. camera.Select((c, k) => k == 0 ? Similarity.Identity : c.After(camera[k - 1].Inverse()))];
        double aspect = 16 / 9.0;
        StabilizationResult result = CameraPath.Stabilize(motions, 30, aspect, new StabilizeOptions(1));
        Assert.True(result.Zoom > 1);
        foreach (Similarity c in result.Corrections)
        {
            Similarity back = c.Inverse();
            foreach ((double x, double y) in new[] { (-aspect / 2, -0.5), (aspect / 2, -0.5), (-aspect / 2, 0.5), (aspect / 2, 0.5) })
            {
                (double sx, double sy) = back.Apply(x, y);
                Assert.InRange(sx, (-aspect / 2) - 1e-9, (aspect / 2) + 1e-9);
                Assert.InRange(sy, -0.5 - 1e-9, 0.5 + 1e-9);
            }
        }
    }

    [Fact]
    public void Motion_track_round_trips_and_rejects_damage()
    {
        (_, FrameMotion[] motions) = Analyze(10, 9);
        var track = new MotionTrack(1920, 1080, new Core.Timing.Rational(30000, 1001), 42, motions);
        byte[] bytes = track.ToBytes();
        MotionTrack read = MotionTrack.FromBytes(bytes);
        Assert.Equal(track.FirstFrame, read.FirstFrame);
        Assert.Equal(track.FrameRate, read.FrameRate);
        Assert.Equal(motions, read.Motions);
        Assert.Throws<InvalidDataException>(() => MotionTrack.FromBytes(bytes.AsSpan(0, bytes.Length - 1)));
        bytes[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => MotionTrack.FromBytes(bytes));
    }

    [Fact]
    public void A_flat_frame_gives_no_motion()
    {
        var estimator = new MotionEstimator(Width, Height);
        float[] flat = new float[Width * Height];
        Array.Fill(flat, 128f);
        estimator.PushAnalysis(flat);
        FrameMotion motion = estimator.PushAnalysis(flat);
        Assert.Equal(Similarity.Identity, motion.Motion);
        Assert.Equal(0, motion.Inliers);
    }

    /// <summary>Criterio de la fase 7: el análisis de 1080p va a ≥60 cuadros por segundo (desde el plano de luma; la decodificación va aparte).</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Analysis_of_1080p_runs_at_60_fps_or_more()
    {
        var scene = new ShakyScene(10);
        Similarity[] camera = ShakyScene.Camera(24, 10);
        byte[][] frames = [.. camera.Select(c => scene.RenderLuma(c, 1920, 1080))];
        var estimator = new MotionEstimator(1920, 1080);
        foreach (byte[] f in frames.Take(4))
        {
            estimator.Push(new LumaFrame(f, 1920, 1080, 1920, LumaFormat.Gray8));
        }

        const int count = 240;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            // Ida y vuelta por los cuadros para que el movimiento siga siendo continuo.
            int k = i % (2 * frames.Length);
            byte[] f = frames[k < frames.Length ? k : (2 * frames.Length) - 1 - k];
            estimator.Push(new LumaFrame(f, 1920, 1080, 1920, LumaFormat.Gray8));
        }

        watch.Stop();
        double fps = count / watch.Elapsed.TotalSeconds;
        output.WriteLine($"1080p: {fps:F0} cuadros/s ({watch.Elapsed.TotalMilliseconds / count:F2} ms por cuadro), {Environment.ProcessorCount} hilos");
        // El criterio se exige compilado en Release; en Debug el JIT no optimiza y solo se pide un tercio.
        bool optimized = typeof(MotionEstimator).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        Assert.True(fps >= (optimized ? 60 : 20), $"{fps:F0} cuadros/s (optimizado: {optimized})");
    }

    [Fact]
    public void Ten_bit_planes_give_the_same_motion_as_eight_bit()
    {
        var scene = new ShakyScene(11);
        Similarity[] camera = ShakyScene.Camera(3, 11);
        var eight = new MotionEstimator(1280, 720);
        var high = new MotionEstimator(1280, 720);
        var low = new MotionEstimator(1280, 720);
        foreach (Similarity c in camera)
        {
            byte[] luma = scene.RenderLuma(c, 1280, 720);
            byte[] p010 = new byte[luma.Length * 2];
            byte[] p10 = new byte[luma.Length * 2];
            for (int i = 0; i < luma.Length; i++)
            {
                BitConverter.TryWriteBytes(p010.AsSpan(i * 2), (ushort)(luma[i] << 8));
                BitConverter.TryWriteBytes(p10.AsSpan(i * 2), (ushort)(luma[i] << 2));
            }

            eight.Push(new LumaFrame(luma, 1280, 720, 1280, LumaFormat.Gray8));
            high.Push(new LumaFrame(p010, 1280, 720, 2560, LumaFormat.Gray16High));
            low.Push(new LumaFrame(p10, 1280, 720, 2560, LumaFormat.Gray16Low10));
        }

        Assert.Equal(eight.Motions, high.Motions);
        Assert.Equal(eight.Motions, low.Motions);
    }
}
