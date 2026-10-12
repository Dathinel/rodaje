using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Scopes;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Los monitores contra una referencia en la CPU hecha con los mismos píxeles leídos de la composición: barras SMPTE al 75 %
/// (forma de onda, desfile, vectorscopio con sus objetivos) y rampas (histograma y forma de onda exactos).
/// </summary>
public sealed class ScopeTests
{
    private const int Width = 1024;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    /// <summary>Las siete barras de SMPTE RP 219 al 75 %: blanco al 75 %, amarillo, cian, verde, magenta, rojo y azul.</summary>
    private static readonly (double R, double G, double B)[] Bars =
    [
        (0.75, 0.75, 0.75), (0.75, 0.75, 0), (0, 0.75, 0.75), (0, 0.75, 0), (0.75, 0, 0.75), (0.75, 0, 0), (0, 0, 0.75),
    ];

    [GpuFact]
    public void Bars_land_on_their_levels_and_on_the_vectorscope_targets()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var image = new YuvImage(Width, Height, false);
        int barWidth = 146;
        for (int i = 0; i < Bars.Length; i++)
        {
            (double r, double g, double b) = Bars[i];
            int x = i * barWidth;
            image.Fill(x, 0, i == Bars.Length - 1 ? Width - x : barWidth, Height, Encode(r, g, b));
        }

        (ScopeCounts counts, byte[] pixels) = Measure(gpu, image);
        AssertMatchesPixels(counts, pixels);

        // Forma de onda: en el centro de cada barra, todos los píxeles de la columna en el nivel de su luma.
        for (int i = 0; i < Bars.Length; i++)
        {
            int column = ((i * barWidth) + (barWidth / 2)) * ScopeRenderer.Columns / Width;
            (byte r, byte g, byte b, _) = At(pixels, (i * barWidth) + (barWidth / 2), Height / 2);
            int luma = (int)Math.Round(((0.2126 * r) + (0.7152 * g) + (0.0722 * b)));
            uint near = counts.Wave(3, luma, column) + counts.Wave(3, Math.Max(0, luma - 1), column) + counts.Wave(3, Math.Min(255, luma + 1), column);
            Assert.Equal(TotalInColumn(counts, column), near);
            Assert.Equal(TotalInColumn(counts, column), counts.Wave(0, r, column));
        }

        // Vectorscopio: cada barra de color cae a menos de 1,5 celdas de su objetivo al 75 %; el blanco, en el centro.
        IReadOnlyList<VectorTarget> targets = ScopeGraticule.Targets(0.75);
        foreach ((string label, int bar) in new[] { ("Yl", 1), ("Cy", 2), ("G", 3), ("Mg", 4), ("R", 5), ("B", 6) })
        {
            VectorTarget target = targets.Single(t => t.Label == label);
            (int x, int y) = Peak(counts, target.X, target.Y, 6);
            Assert.True(Math.Abs(x - target.X) <= 1.5 && Math.Abs(y - target.Y) <= 1.5,
                $"La barra {label} cae en ({x}, {y}); el objetivo está en ({target.X:0.0}, {target.Y:0.0}).");
            Assert.True(counts.Vector(x, y) > Height * barWidth / 4, $"La barra {label} está repartida: {counts.Vector(x, y)} en su celda.");
        }

        (int cx, int cy) = Peak(counts, 128, 128, 2);
        Assert.Equal((128, 128), (cx, cy));
    }

    [GpuFact]
    public void A_gray_ramp_fills_the_histogram_evenly_and_rises_across_the_waveform()
    {
        using GpuDevice gpu = GpuDevice.Create();
        var image = new YuvImage(Width, Height, false);
        for (int x = 0; x < Width; x += 4)
        {
            int code = 16 + (int)Math.Round(219.0 * x / (Width - 4));
            image.Fill(x, 0, 4, Height, new YuvCode(code, 128, 128));
        }

        (ScopeCounts counts, byte[] pixels) = Measure(gpu, image);
        AssertMatchesPixels(counts, pixels);

        // Rampa de gris: R, G, B y la luma iguales en cada columna, y la luma sube de izquierda a derecha.
        int previous = -1;
        for (int column = 0; column < ScopeRenderer.Columns; column += 8)
        {
            int level = Enumerable.Range(0, 256).MaxBy(l => counts.Wave(3, l, column));
            Assert.True(level >= previous, $"La forma de onda baja en la columna {column}.");
            previous = level;
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.Equal(counts.Wave(3, level, column), counts.Wave(channel, level, column));
            }
        }

        Assert.InRange(previous, 245, 255);
        uint used = (uint)Enumerable.Range(0, 256).Count(l => counts.Histogram(3, l) > 0);
        Assert.InRange(used, 200u, 256u);
    }

    [GpuFact]
    public void The_scope_image_arrives_without_waiting_and_shows_the_trace()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var compositor = new Compositor(gpu, 256, 128);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        YuvCode gray = Encode(0.5, 0.5, 0.5);
        using (CompositionFrame frame = Layers.Frame(256, 128, Layers.From(gpu, YuvImage.Solid(256, 128, false, gray), Bt709)))
        {
            compositor.Render(frame);
        }

        using var scopes = new ScopeRenderer(gpu);
        Assert.True(scopes.Ready.Wait(TimeSpan.FromSeconds(60)));
        Assert.True(scopes.Submit(compositor, ScopeKind.Waveform));
        ScopeImage? image = null;
        for (int i = 0; i < 200 && image is null; i++)
        {
            image = scopes.TryRead();
            if (image is null)
            {
                Thread.Sleep(5);
            }
        }

        Assert.NotNull(image);
        Assert.Equal(ScopeKind.Waveform, image.Kind);
        int row = (int)ScopeGraticule.Row(50);
        byte traceGreen = Enumerable.Range(row - 2, 5).Max(r => image.Bgra[(((r * ScopeRenderer.ImageWidth) + 100) * 4) + 1]);
        byte emptyGreen = image.Bgra[(((20 * ScopeRenderer.ImageWidth) + 100) * 4) + 1];
        Assert.True(traceGreen > 200 && emptyGreen < 20, $"Trazo {traceGreen}, vacío {emptyGreen}.");
        Assert.Null(scopes.TryRead());
    }

    private static (ScopeCounts Counts, byte[] Pixels) Measure(GpuDevice gpu, YuvImage image)
    {
        using var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        using CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, image, Bt709));
        compositor.Render(frame);
        using var scopes = new ScopeRenderer(gpu);
        return (scopes.MeasureNow(compositor), compositor.ReadbackRgba());
    }

    /// <summary>Histograma y forma de onda de R, G y B contra los mismos píxeles leídos en 8 bits (iguales salvo redondeos).</summary>
    private static void AssertMatchesPixels(ScopeCounts counts, byte[] pixels)
    {
        var histogram = new int[3, 256];
        var wave = new int[3, 256, ScopeRenderer.Columns];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (byte r, byte g, byte b, _) = At(pixels, x, y);
                int column = x * ScopeRenderer.Columns / Width;
                histogram[0, r]++;
                histogram[1, g]++;
                histogram[2, b]++;
                wave[0, r, column]++;
                wave[1, g, column]++;
                wave[2, b, column]++;
            }
        }

        long differences = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            for (int level = 0; level < 256; level++)
            {
                differences += Math.Abs(histogram[channel, level] - counts.Histogram(channel, level));
                for (int column = 0; column < ScopeRenderer.Columns; column += 16)
                {
                    differences += Math.Abs(wave[channel, level, column] - counts.Wave(channel, level, column));
                }
            }
        }

        Assert.True(differences <= Width * Height * 3 / 200, $"{differences} cuentas distintas de la referencia.");
        Assert.Equal((uint)(Width * Height), (uint)Enumerable.Range(0, 256).Sum(l => counts.Histogram(3, l)));
    }

    private static uint TotalInColumn(ScopeCounts counts, int column) =>
        (uint)Enumerable.Range(0, 256).Sum(l => counts.Wave(3, l, column));

    private static (int X, int Y) Peak(ScopeCounts counts, double x, double y, int reach)
    {
        (int X, int Y) best = ((int)Math.Round(x), (int)Math.Round(y));
        for (int dy = -reach; dy <= reach; dy++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int px = Math.Clamp((int)Math.Round(x) + dx, 0, 255), py = Math.Clamp((int)Math.Round(y) + dy, 0, 255);
                if (counts.Vector(px, py) > counts.Vector(best.X, best.Y))
                {
                    best = (px, py);
                }
            }
        }

        return best;
    }

    private static (byte R, byte G, byte B, byte A) At(byte[] pixels, int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    private static YuvCode Encode(double r, double g, double b) =>
        ReferenceColor.Encode((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255), Bt709, false);
}
