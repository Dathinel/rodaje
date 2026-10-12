using Xunit.Abstractions;

namespace Rodaje.Module.Vision.Tests;

/// <summary>
/// Calidad de la máscara: un recorte con alfa real compuesto sobre fondos sintéticos (la alfa es la verdad). Criterio de la
/// fase 7: IoU ≥ 0,9 con MODNet; BiRefNet_lite se mide. Y el filtro temporal: con el sujeto quieto y ruido de sensor, el
/// borde tiembla al menos la mitad que sin filtro.
/// </summary>
public sealed class MattingTests(ITestOutputHelper output) : IDisposable
{
    private readonly VisionModels _models = new(TestImages.Models);

    public void Dispose() => _models.Dispose();

    internal static byte[] Matte(Matting matting, TemporalFilter? filter, string? session, byte[] frame, int width, int height, bool high, ComputeDevice device = ComputeDevice.Gpu)
    {
        MatteResult matte = matting.Run(new BgraFrame(frame, width, height, width * 4), high, device);
        float[] alpha = filter is null ? matte.Alpha : filter.Apply(session, false, matte);
        var result = new byte[width * height];
        Imaging.AlphaToGray8(alpha, matte.Width, matte.Height, result, width, height);
        return result;
    }

    internal static IEnumerable<(string Name, byte[] Frame, byte[] Truth, int Width, int Height)> Cases()
    {
        Cutout portrait = TestImages.Portrait!, body = TestImages.FullBody!;
        int seed = 1;
        foreach (Scenes.Background background in Enum.GetValues<Scenes.Background>())
        {
            (int w, int h) = (960, 540);
            byte[] bg = Scenes.MakeBackground(background, w, h, seed++);
            (byte[] f1, byte[] t1) = Scenes.Compose(bg, w, h, portrait, 260, 80, 460);
            yield return ($"retrato/{background}", f1, t1, w, h);
            (byte[] f2, byte[] t2) = Scenes.Compose(bg, w, h, body, 600, 20, 500);
            yield return ($"cuerpo/{background}", f2, t2, w, h);
        }
    }

    [Fact]
    public void Masks_match_the_true_alpha()
    {
        if (!TestImages.Ready)
        {
            output.WriteLine(TestImages.Missing);
            return;
        }

        var matting = new Matting(_models);
        var modnet = new List<double>();
        var birefnet = new List<double>();
        foreach ((string name, byte[] frame, byte[] truth, int w, int h) in Cases())
        {
            double a = Scenes.Iou(Matte(matting, null, null, frame, w, h, false), truth);
            double b = Scenes.Iou(Matte(matting, null, null, frame, w, h, true), truth);
            modnet.Add(a);
            birefnet.Add(b);
            output.WriteLine($"{name}: MODNet {a:0.000}, BiRefNet_lite {b:0.000}");
        }

        output.WriteLine($"Media: MODNet {modnet.Average():0.000} (mín. {modnet.Min():0.000}); BiRefNet_lite {birefnet.Average():0.000} (mín. {birefnet.Min():0.000})");
        Assert.True(modnet.Average() >= 0.9, $"IoU medio de MODNet {modnet.Average():0.000} < 0,9");
        Assert.True(birefnet.Average() >= 0.9, $"IoU medio de BiRefNet_lite {birefnet.Average():0.000} < 0,9");
    }

    [Fact]
    public void Temporal_filter_halves_edge_flicker()
    {
        if (!TestImages.Ready)
        {
            output.WriteLine(TestImages.Missing);
            return;
        }

        (int w, int h) = (960, 540);
        byte[] bg = Scenes.MakeBackground(Scenes.Background.Blobs, w, h, 3);
        (byte[] clean, byte[] truth) = Scenes.Compose(bg, w, h, TestImages.Portrait!, 260, 80, 460);
        var matting = new Matting(_models);
        var filter = new TemporalFilter();
        byte[]? previousRaw = null, previousFiltered = null;
        double raw = 0, filtered = 0;
        const int Frames = 24;
        for (int i = 0; i < Frames; i++)
        {
            byte[] frame = (byte[])clean.Clone();
            Scenes.AddNoise(frame, 8, i);
            byte[] a = Matte(matting, null, null, frame, w, h, false);
            byte[] b = Matte(matting, filter, "clip", frame, w, h, false);
            if (previousRaw is not null)
            {
                raw += Flicker(previousRaw, a);
                filtered += Flicker(previousFiltered!, b);
            }

            Assert.True(Scenes.Iou(b, truth) >= 0.85);
            (previousRaw, previousFiltered) = (a, b);
        }

        output.WriteLine($"Parpadeo medio del borde (niveles de gris por píxel y cuadro): sin filtro {raw / (Frames - 1):0.0000}, con filtro {filtered / (Frames - 1):0.0000} ({raw / Math.Max(filtered, 1e-9):0.0}× menos)");
        Assert.True(filtered * 2 <= raw, "El filtro temporal no reduce el parpadeo a la mitad.");
    }

    private static double Flicker(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / a.Length;
    }
}
