using Xunit.Abstractions;

namespace Rodaje.Module.Vision.Tests;

/// <summary>YuNet encuentra la cara y RT-DETRv2 la persona en una escena compuesta con los dos recortes de prueba.</summary>
public sealed class DetectionTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 960, Height = 540;
    private readonly VisionModels _models = new(TestImages.Models);

    public void Dispose() => _models.Dispose();

    /// <summary>Retrato a la izquierda (altura 460) y cuerpo entero a la derecha (altura 500), sobre manchas de color.</summary>
    internal static (BgraFrame Frame, Box Portrait, Box Body, Box Face) Scene()
    {
        byte[] background = Scenes.MakeBackground(Scenes.Background.Gradient, Width, Height, 5);
        (byte[] withPortrait, byte[] portraitTruth) = Scenes.Compose(background, Width, Height, TestImages.Portrait!, 40, 80, 460);
        (byte[] frame, byte[] bodyTruth) = Scenes.Compose(withPortrait, Width, Height, TestImages.FullBody!, 640, 20, 500);

        // La cara del retrato ocupa, en el recorte de 512×512, más o menos x 195..335 e y 30..215.
        float s = 460f / 512;
        var face = new Box((40 + (195 * s)) / Width, (80 + (30 * s)) / Height, 140 * s / Width, 185 * s / Height, 1);
        return (new BgraFrame(frame, Width, Height, Width * 4), Bounds(portraitTruth), Bounds(bodyTruth), face);
    }

    private static Box Bounds(byte[] truth)
    {
        int x0 = Width, y0 = Height, x1 = 0, y1 = 0;
        for (int i = 0; i < truth.Length; i++)
        {
            if (truth[i] >= 128)
            {
                (int x, int y) = (i % Width, i / Width);
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x + 1), Math.Max(y1, y + 1));
            }
        }

        return new Box((float)x0 / Width, (float)y0 / Height, (float)(x1 - x0) / Width, (float)(y1 - y0) / Height, 1);
    }

    [Fact]
    public void YuNet_finds_the_face()
    {
        if (!TestImages.Ready)
        {
            output.WriteLine(TestImages.Missing);
            return;
        }

        (BgraFrame frame, _, _, Box face) = Scene();
        (List<Box> boxes, string device) = new FaceDetector(_models).Detect(frame, ComputeDevice.Gpu);
        output.WriteLine($"YuNet ({device}): {string.Join("; ", boxes.Select(b => $"{b.X:0.000},{b.Y:0.000} {b.W:0.000}×{b.H:0.000} p={b.P:0.00}"))}");
        Box best = Assert.Single(boxes, b => Box.Iou(b, face) > 0.3);
        output.WriteLine($"IoU con la cara esperada: {Box.Iou(best, face):0.00}");
    }

    [Fact]
    public void RtDetr_finds_the_people()
    {
        if (!TestImages.Ready)
        {
            output.WriteLine(TestImages.Missing);
            return;
        }

        (BgraFrame frame, Box portrait, Box body, _) = Scene();
        (List<Box>[] found, string device) = new PersonDetector(_models).Detect([frame, frame], ComputeDevice.Gpu);
        List<Box> boxes = found[0];
        output.WriteLine($"RT-DETRv2 ({device}): {string.Join("; ", boxes.Select(b => $"{b.X:0.000},{b.Y:0.000} {b.W:0.000}×{b.H:0.000} p={b.P:0.00}"))}");
        double portraitIou = boxes.Max(b => Box.Iou(b, portrait)), bodyIou = boxes.Max(b => Box.Iou(b, body));
        output.WriteLine($"IoU con el retrato {portraitIou:0.00}, con el cuerpo entero {bodyIou:0.00}");
        Assert.Equal(2, boxes.Count);
        Assert.True(portraitIou >= 0.7 && bodyIou >= 0.7);
        Assert.Equal(boxes.Count, found[1].Count);
    }
}
