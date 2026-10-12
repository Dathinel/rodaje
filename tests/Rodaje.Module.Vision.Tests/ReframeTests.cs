using Xunit.Abstractions;

namespace Rodaje.Module.Vision.Tests;

/// <summary>Reencuadre con un sujeto sintético: se queda, camina, vuelve; con ruido, huecos y otra persona más chica.</summary>
public class ReframeTests(ITestOutputHelper output)
{
    private const int SourceWidth = 1920, SourceHeight = 1080;
    private const double Vertical = 9.0 / 16;

    /// <summary>Caja verdadera del sujeto en el segundo <paramref name="t"/> (normalizada).</summary>
    internal static Box Truth(double t)
    {
        double cx = t switch
        {
            < 4 => 0.3,
            < 8 => 0.3 + (0.4 * (t - 4) / 4),
            < 12 => 0.7,
            < 13.5 => 0.7 - (0.5 * (t - 12) / 1.5),
            _ => 0.2 + (0.02 * Math.Sin(t * 3)),
        };
        double w = 0.09, h = 0.4;
        return new Box((float)(cx - (w / 2)), 0.35f, (float)w, (float)h, 0.9f);
    }

    /// <summary>Detecciones a <paramref name="fps"/> cuadros por segundo durante 20 s.</summary>
    internal static List<ReframeFrame> Detections(double fps, int seed)
    {
        var random = new Random(seed);
        var frames = new List<ReframeFrame>();
        for (int i = 0; i < 20 * fps; i++)
        {
            double t = i / fps;
            var boxes = new List<Box>();
            bool hole = t is > 9 and < 10;
            if (!hole && random.NextDouble() > 0.1)
            {
                Box truth = Truth(t);
                float Noise() => (float)((random.NextDouble() - 0.5) * 0.012);
                boxes.Add(new Box(truth.X + Noise(), truth.Y + Noise(), truth.W + Noise(), truth.H + Noise(), 0.8f + (0.2f * (float)random.NextDouble())));
            }

            if (t is > 5 and < 15 && random.NextDouble() > 0.3)
            {
                boxes.Add(new Box(0.85f, 0.5f, 0.04f, 0.15f, 0.7f));
            }

            frames.Add(new ReframeFrame(t, boxes));
        }

        return frames;
    }

    /// <summary>Centro del recorte en <paramref name="t"/> interpolando los keyframes, como lo hará la app.</summary>
    internal static (double X, double Y) At(IReadOnlyList<Keyframe> keys, double t)
    {
        if (t <= keys[0].Time || keys.Count == 1)
        {
            return (keys[0].X, keys[0].Y);
        }

        for (int k = 1; k < keys.Count; k++)
        {
            if (t <= keys[k].Time)
            {
                double f = (t - keys[k - 1].Time) / (keys[k].Time - keys[k - 1].Time);
                return (keys[k - 1].X + ((keys[k].X - keys[k - 1].X) * f), keys[k - 1].Y + ((keys[k].Y - keys[k - 1].Y) * f));
            }
        }

        return (keys[^1].X, keys[^1].Y);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Follows_the_subject_with_few_keyframes(double smoothing)
    {
        ReframeResult result = Reframer.Run(Vertical, SourceWidth, SourceHeight, Detections(10, 7), smoothing);
        double cropW = Vertical * SourceHeight / SourceWidth;
        int inside = 0, total = 0;
        for (double t = 0; t < 20; t += 1 / 30.0)
        {
            (double x, double y) = At(result.Keyframes, t);
            Box truth = Truth(t);
            Assert.InRange(x - (cropW / 2), -1e-9, 1 - cropW + 1e-9);
            Assert.Equal(0.5, y, 6);
            total++;
            inside += truth.X >= x - (cropW / 2) - 1e-3 && truth.X + truth.W <= x + (cropW / 2) + 1e-3 ? 1 : 0;
        }

        output.WriteLine($"suavizado {smoothing}: {result.Keyframes.Count} keyframes, {result.Tracks} pistas, «dentro» {result.Inside:P1}; verdad dentro a 30 cuadros/s {(double)inside / total:P1}");
        output.WriteLine(string.Join(" ", result.Keyframes.Select(k => $"({k.Time:0.0} s, {k.X:0.000})")));
        Assert.True(result.Inside >= 0.98);
        Assert.True((double)inside / total >= 0.98);
        Assert.InRange(result.Keyframes.Count, 2, 12);
        Assert.All(result.Keyframes, k => Assert.Equal(1, k.Scale));
    }

    [Fact]
    public void A_still_subject_gives_one_keyframe()
    {
        var frames = Enumerable.Range(0, 100).Select(i => new ReframeFrame(i / 10.0, [new Box(0.6f + (0.003f * (i % 3)), 0.3f, 0.1f, 0.4f, 0.9f)])).ToList();
        ReframeResult result = Reframer.Run(Vertical, SourceWidth, SourceHeight, frames, 0.5);
        Keyframe key = Assert.Single(result.Keyframes);
        Assert.Equal(0.65, key.X, 2);
        Assert.Equal(1, result.Inside);
    }

    [Fact]
    public void Without_detections_the_crop_is_centered()
    {
        var frames = Enumerable.Range(0, 50).Select(i => new ReframeFrame(i / 10.0, [])).ToList();
        Keyframe key = Assert.Single(Reframer.Run(Vertical, SourceWidth, SourceHeight, frames, 0.5).Keyframes);
        Assert.Equal((0.5, 0.5, 1.0), (key.X, key.Y, key.Scale));
    }

    [Fact]
    public void A_wide_crop_on_a_tall_source_pans_vertically()
    {
        var frames = Enumerable.Range(0, 100).Select(i => new ReframeFrame(i / 10.0,
            [new Box(0.4f, 0.1f + (0.6f * i / 100f), 0.2f, 0.15f, 0.9f)])).ToList();
        ReframeResult result = Reframer.Run(16.0 / 9, 1080, 1920, frames, 0.5);
        Assert.All(result.Keyframes, k => Assert.Equal(0.5, k.X, 6));
        Assert.True(result.Keyframes[^1].Y > result.Keyframes[0].Y + 0.3);
        Assert.True(result.Inside >= 0.98);
    }
}
