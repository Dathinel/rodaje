using System.Globalization;
using System.Text.Json;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Tests.Support;
using Rodaje.Render.Text;
using Rodaje.Text.Tests;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Text;

public sealed class SkiaTitleReferenceTests(ITestOutputHelper output)
{
    private const int Width = 960;
    private const int Height = 540;
    private const string ReferenceVariable = "RODAJE_REFERENCIAS_WARP";
    public static TheoryData<string> Cases => [.. TitleLayoutCases.All.Select(c => c.Name)];

    [EngineTheory]
    [MemberData(nameof(Cases))]
    public void Skia_matches_direct2d_layout_and_pixels(string name)
    {
        TitleLayoutCase item = TitleLayoutCases.All.Single(c => c.Name == name);
        using TestGpu gpu = TestGpu.Create();
        using var titles = new SkiaTitleRasterizer(gpu.Gpu, new TitleRasterizerOptions { BackgroundThread = false });
        Register(titles);
        GeneratedContent content = TitleImage.Content(item.Text, item.Style(), item.BoxWidth);
        TitleTexture texture = titles.Rasterize(content)!;
        string root = TitleLayoutCases.RepositoryRoot();
        LaidOutTitle expectedLayout = JsonSerializer.Deserialize<LaidOutTitle>(File.ReadAllText(TitleLayoutCases.GoldenPath(root, name)), TitleLayoutCases.Json)!;
        TitleUnit[] units = texture.Units!.Units;
        Assert.Equal(expectedLayout.Units.Select(u => (u.Start, u.End, u.Line, u.Word)), units.Select(u => (u.Start, u.End, u.Line, u.Word)));
        float advanceError = 0;
        if (!item.FallsBackPerGlyph)
        {
            for (int i = 0; i < units.Length; i++)
            {
                LaidOutUnit e = expectedLayout.Units[i];
                advanceError = Math.Max(advanceError, Math.Max(Math.Abs(e.Left - units[i].InkLeft), Math.Abs(e.Right - units[i].InkRight)));
                Assert.InRange(Math.Abs(e.Left - units[i].InkLeft), 0, 1);
                Assert.InRange(Math.Abs(e.Right - units[i].InkRight), 0, 1);
                Assert.InRange(Math.Abs(e.Top - units[i].Top), 0, 0.5f);
                Assert.InRange(Math.Abs(e.Bottom - units[i].Bottom), 0, 0.5f);
            }
        }

        byte[] actual = Compose(gpu.Gpu, titles, content);
        byte[]? expected = Reference(item);
        if (expected is null)
        {
            Assert.True(Environment.GetEnvironmentVariable(ReferenceVariable) is null, "Falta la referencia de títulos: " + name);
            output.WriteLine("Sin referencias de Windows: se comprobó la maquetación; la comparación de píxeles corre en el trabajo de referencias.");
            return;
        }
        Assert.Equal(expected.Length, actual.Length);
        long sum = 0;
        int maximum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            int difference = Math.Abs(expected[i] - actual[i]);
            sum += difference;
            maximum = Math.Max(maximum, difference);
        }
        double mean = (double)sum / expected.Length / 255;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: mean={mean:F8}; max={maximum}/255; layout={advanceError:F5}px"));
        if (!item.FallsBackPerGlyph)
        {
            // Medido: media 0,00123193 y máximo 131/255; margen para el redondeo del filtro entre motores.
            Assert.InRange(mean, 0, 0.0015);
            Assert.InRange(maximum, 0, 144);
        }
        else
        {
            Assert.InRange(mean, 0, 0.005);
            output.WriteLine("Respaldo por letra: fuentes distintas; el límite medio 0,005 detecta regresiones grandes; este caso no fija la tolerancia de antialias.");
        }
    }

    [Fact]
    public void Generate_warp_references_for_titles()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7) || Environment.GetEnvironmentVariable(ReferenceVariable) is not { } folder)
        {
            return;
        }
        Directory.CreateDirectory(folder);
        foreach (TitleLayoutCase item in TitleLayoutCases.All)
        {
            File.WriteAllBytes(Path.Combine(folder, "title-" + item.Name + ".rgba"), WindowsReference(item));
        }
    }

    private static byte[]? Reference(TitleLayoutCase item)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return WindowsReference(item);
        }
        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        string? path = folder is null ? null : Path.Combine(folder, "title-" + item.Name + ".rgba");
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private static byte[] WindowsReference(TitleLayoutCase item)
    {
        using GpuDevice device = GpuDevice.CreateWarp();
        using var titles = new TitleRasterizer(device, new TitleRasterizerOptions { BackgroundThread = false });
        Register(titles);
        return Compose(D3D11Gpu.For(device), titles, TitleImage.Content(item.Text, item.Style(), item.BoxWidth));
    }

    private static void Register(ITitleRasterizer titles) => Assert.Empty(titles.RegisterFonts(
        TitleLayoutCases.FontFiles.Select(f => Path.Combine(TitleLayoutCases.RepositoryRoot(), f))));

    private static byte[] Compose(IGpu gpu, ITitleRasterizer titles, GeneratedContent content)
    {
        using var compositor = new Compositor(gpu, Width, Height, CompositeFormat.Rgba8Unorm) { Titles = titles, TransparentBackground = true };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2)));
        using var frame = new CompositionFrame(Time.Zero, 1920, 1080, [new LayerFrame(0, "title", content, LayerTransform.Identity)]);
        CompositeResult result = compositor.RenderStrict(frame);
        Assert.Equal(1, result.LayersDrawn);
        Assert.Equal(0, result.TitlesPending);
        return compositor.ReadbackRgba();
    }
}
