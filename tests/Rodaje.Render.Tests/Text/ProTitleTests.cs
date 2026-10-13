using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Text;

/// <summary>
/// Títulos profesionales contra lo que se espera de cada parte: tramos, degradado, varios bordes y sombras, mayúsculas,
/// kerning, ajuste a la caja, arco, animación por letra determinista, desenfoque por unidad, barrido de karaoke,
/// vista previa igual a exportación y lo que cuesta.
/// </summary>
public sealed class ProTitleTests(ITestOutputHelper output)
{
    private static readonly bool Strict = Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    private static bool White((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 245 && p.G > 245 && p.B > 245;

    private static bool Red((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 245 && p.G < 10 && p.B < 10;

    private static bool Blue((byte B, byte G, byte R, byte A) p) => p.A > 200 && p.B > 200 && p.R < 40 && p.G < 40;

    private static TextStyle Style(double size = 96) => TitleRasterizerTests.Style("#FFFFFF", size);

    private static GeneratedContent Content(string text, TextStyle style, IReadOnlyList<TextSpan>? spans = null, double box = 0, double boxHeight = 0,
        IReadOnlyList<(int, int)>? words = null, int width = 1920, int height = 1080, double scale = 1) =>
        new(GeneratorKinds.Text, text, style, box, width, height, scale, extras: new TextExtras(spans, boxHeight, words)) { ClipId = "titulo" };

    [GpuFact]
    public void A_span_paints_its_word_in_its_own_color()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TitleImage image = TitleImage.Read(gpu, titles.Rasterize(Content("Hola mundo", Style(), [new TextSpan { Start = 5, Length = 5, Color = "#FF0000" }]))!);
        int red = image.Count(Red);
        int white = image.Count(White);
        output.WriteLine($"Rojo {red} px, blanco {white} px");
        Assert.True(red > 1500 && white > 1500);
        Assert.True(Center(image, Red).X > image.Width / 2.0 && Center(image, White).X < image.Width / 2.0);
    }

    [GpuFact]
    public void A_bigger_span_grows_the_line_and_bold_adds_ink()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle light = Style();
        light.Font.Weight = 300;
        TitleTexture plain = titles.Rasterize(Content("Hola mundo", light))!;
        TitleTexture big = titles.Rasterize(Content("Hola mundo", light, [new TextSpan { Start = 5, Length = 5, Scale = 2 }]))!;
        TitleTexture bold = titles.Rasterize(Content("Hola mundo", light, [new TextSpan { Start = 0, Length = 10, Weight = 900 }]))!;
        Assert.True(big.LocalHeight > plain.LocalHeight * 1.5, $"{big.LocalHeight} contra {plain.LocalHeight}");
        Assert.True(TitleImage.Read(gpu, bold).Count(p => p.A > 128) > TitleImage.Read(gpu, plain).Count(p => p.A > 128) * 1.3);
    }

    [GpuFact]
    public void A_vertical_gradient_goes_from_the_first_color_at_the_top_to_the_last_at_the_bottom()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(160);
        style.Fill.Gradient = new StyleGradient { Angle = 90, Stops = [new(0, "#FFFF00"), new(1, "#FF0000")] };
        TitleImage image = TitleImage.Read(gpu, titles.Rasterize(Content("HIH", style))!);
        (int top, int bottom) = image.InkRows();
        double Green(int from, int to)
        {
            double sum = 0;
            int n = 0;
            for (int y = from; y <= to; y++)
            {
                for (int x = 0; x < image.Width; x++)
                {
                    var p = image[x, y];
                    if (p.A == 255)
                    {
                        sum += p.G;
                        n++;
                    }
                }
            }

            return sum / Math.Max(1, n);
        }

        int third = (bottom - top) / 3;
        double upper = Green(top, top + third);
        double lower = Green(bottom - third, bottom);
        output.WriteLine($"Verde arriba {upper:0}, abajo {lower:0}");
        Assert.True(upper > 170 && lower < 90, "Amarillo arriba, rojo abajo.");
        Assert.Equal(0, image.Count(p => p.A == 255 && p.B > 10));
    }

    [GpuFact]
    public void Several_strokes_stack_from_the_widest_outward()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style();
        style.Fill.Color = "#FF0000";
        style.Stroke = new StyleStroke { Color = "#000000", Width = 4 };
        style.ExtraStrokes = [new StyleStroke { Color = "#FFFFFF", Width = 12 }];
        TitleImage image = TitleImage.Read(gpu, titles.Rasterize(Content("OIO", style))!);
        int red = image.Count(Red);
        int black = image.Count(p => p.A == 255 && p.R < 10 && p.G < 10 && p.B < 10);
        int white = image.Count(White);
        output.WriteLine($"Relleno {red}, borde negro {black}, borde blanco {white}");
        Assert.True(red > 1000 && black > 500 && white > black, "Tres anillos: rojo adentro, negro y luego blanco, más ancho.");

        // A lo largo de una fila que cruza la «I» del medio: blanco, negro, rojo, negro, blanco.
        int row = (image.InkRows().Top + image.InkRows().Bottom) / 2;
        string sequence = "";
        for (int x = 0; x < image.Width; x++)
        {
            var p = image[x, row];
            char c = Red(p) ? 'r' : White(p) ? 'w' : p.A == 255 && p.R < 10 ? 'k' : ' ';
            if (c != ' ' && (sequence.Length == 0 || sequence[^1] != c))
            {
                sequence += c;
            }
        }

        Assert.Contains("wkrkw", sequence, StringComparison.Ordinal);
    }

    [GpuFact]
    public void Several_shadows_each_fall_their_own_way()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style();
        style.Shadow = new StyleShadow { Color = "#FF0000", Dx = 30 };
        style.ExtraShadows = [new StyleShadow { Color = "#0000FF", Dx = -30 }];
        TitleImage image = TitleImage.Read(gpu, titles.Rasterize(Content("I", style))!);
        double white = Center(image, White).X;
        Assert.True(Center(image, Red).X > white + 15, "La roja a la derecha.");
        Assert.True(Center(image, Blue).X < white - 15, "La azul a la izquierda.");
    }

    [GpuFact]
    public void Case_and_kerning_change_the_drawn_text_but_not_the_stored_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle upper = Style();
        upper.Typography = new StyleTypography { Case = TextCase.Upper };
        TextStyle small = Style();
        small.Typography = new StyleTypography { Case = TextCase.SmallCaps };
        int Ink(TextStyle s, string text) => TitleImage.Read(gpu, titles.Rasterize(Content(text, s))!).Count(p => p.A > 128);
        int lower = Ink(Style(), "aeoaeo");
        int capitals = Ink(upper, "aeoaeo");
        int smallCaps = Ink(small, "aeoaeo");
        output.WriteLine($"Minúsculas {lower}, mayúsculas {capitals}, versalitas {smallCaps}");
        Assert.True(capitals > lower * 1.2);
        Assert.InRange(smallCaps, capitals * 0.4, capitals * 0.75);

        TextStyle tight = Style();
        TextStyle loose = Style();
        loose.Typography = new StyleTypography { Kerning = false };
        double kerned = titles.Rasterize(Content("AVAVAVAVAV", tight))!.LocalWidth;
        double unkerned = titles.Rasterize(Content("AVAVAVAVAV", loose))!.LocalWidth;
        output.WriteLine($"Con kerning {kerned:0.0} px, sin {unkerned:0.0} px");
        Assert.True(unkerned > kerned + 20);
    }

    [GpuFact]
    public void Fit_shrinks_a_long_line_into_its_box_and_fill_grows_a_short_one()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle shrink = Style();
        shrink.Typography = new StyleTypography { Fit = TextFit.Shrink };
        TextStyle fill = Style();
        fill.Typography = new StyleTypography { Fit = TextFit.Fill };
        const string Long = "Un título demasiado largo para su caja";
        double free = titles.Rasterize(Content(Long, Style()))!.LocalWidth;
        TitleImage shrunk = TitleImage.Read(gpu, titles.Rasterize(Content(Long, shrink, box: 600, boxHeight: 200))!);
        TitleTexture grown = titles.Rasterize(Content("Hola", fill, box: 1600, boxHeight: 600))!;
        TitleTexture plain = titles.Rasterize(Content("Hola", Style(), box: 1600, boxHeight: 600))!;
        (int top, int bottom) = shrunk.InkRows();
        output.WriteLine($"Libre {free:0} px; achicado: una línea de {bottom - top} px; agrandado {TitleImage.Read(gpu, grown).InkRows()}");
        Assert.True(free > 1200);
        Assert.True(bottom - top <= 200, "Achicado, entra en el alto de la caja.");
        Assert.True(TitleImage.Read(gpu, grown).Count(p => p.A > 128) > TitleImage.Read(gpu, plain).Count(p => p.A > 128) * 4);
    }

    [GpuFact]
    public void Text_on_an_arc_rises_in_the_middle_and_turns_its_letters()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style(80);
        style.Path = new StylePath { Kind = TextPathKind.Arc, Bend = 140 };
        GeneratedContent content = Content("ARCOARCOARCOARCO", style);
        Assert.True(content.UsesUnits);
        RgbaImage arc = Compose(gpu, content, TextMotion.Rest);
        RgbaImage straight = Compose(gpu, Content("ARCOARCOARCOARCO", Style(80)), TextMotion.Rest);
        (double leftY, double middleY, double rightY) = Thirds(arc);
        (double flatLeft, double flatMiddle, _) = Thirds(straight);
        output.WriteLine($"Arco: tinta a {leftY:0} / {middleY:0} / {rightY:0} px; recto: {flatLeft:0} / {flatMiddle:0}");
        Assert.True(middleY < leftY - 30 && middleY < rightY - 30, "Con el arco hacia arriba el centro queda más alto que los lados.");
        Assert.InRange(flatMiddle - flatLeft, -5, 5);

        style.Path.Bend = -140;
        (double smileLeft, double smileMiddle, _) = Thirds(Compose(gpu, Content("ARCOARCOARCOARCO", style), TextMotion.Rest));
        Assert.True(smileMiddle > smileLeft + 30, "Con el arco hacia abajo, al revés.");
    }

    [GpuFact]
    public void Per_letter_entrance_is_deterministic_and_reveals_left_to_right()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style();
        style.In = TextAnimationPresets.Find("subir")!.In(TextUnit.Letter, 0.6);
        GeneratedContent content = Content("Letra por letra", style);

        RgbaImage start = Compose(gpu, content, new TextMotion(TextPhase.In, 0, -1, 0));
        RgbaImage half = Compose(gpu, content, new TextMotion(TextPhase.In, 0.5, -1, 0));
        RgbaImage again = Compose(gpu, content, new TextMotion(TextPhase.In, 0.5, -1, 0));
        RgbaImage done = Compose(gpu, content, new TextMotion(TextPhase.In, 1, -1, 0));
        RgbaImage rest = Compose(gpu, content, TextMotion.Rest);
        Assert.Equal(0, Ink(start, 0, 1920));
        Assert.Equal(half.Pixels, again.Pixels);
        int left = Ink(half, 0, 960);
        int right = Ink(half, 960, 1920);
        output.WriteLine($"A mitad de la entrada: tinta {left} a la izquierda y {right} a la derecha");
        Assert.True(left > right * 2, "Las primeras letras ya entraron.");
        Assert.True(MaxDifference(done, rest) <= 2, "Al terminar la entrada queda igual que en reposo.");
    }

    [GpuFact]
    public void Per_word_blur_softens_the_edges_while_it_lasts()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style(120);
        style.In = new StyleAnimation
        {
            DurationSeconds = 1,
            Unit = TextUnit.Word,
            Curves = new() { [StyleAnimation.Blur] = [new(0, 24), new(1, 0)] },
        };
        GeneratedContent content = Content("Foco suave", style);
        int Soft(RgbaImage image) => Count(image, p => p.G is > 10 and < 245);
        int blurred = Soft(Compose(gpu, content, new TextMotion(TextPhase.In, 0.1, -1, 0)));
        int sharp = Soft(Compose(gpu, content, TextMotion.Rest));
        output.WriteLine($"Píxeles de borde suave: desenfocado {blurred}, enfocado {sharp}");
        Assert.True(blurred > sharp * 3);
    }

    [GpuFact]
    public void Karaoke_sweep_paints_what_was_said_and_cuts_the_current_word()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style();
        style.Highlight = new StyleHighlight { Color = "#FFD400", Mode = HighlightMode.Sweep };
        GeneratedContent content = Content("uno dos tres", style, words: [(0, 3), (4, 3), (8, 4)]);
        Assert.True(content.UsesUnits);
        static bool Yellow((byte R, byte G, byte B, byte A) p) => p.R > 240 && p.G is > 195 and < 225 && p.B < 20;
        int YellowAt(int word, double said) => Count(Compose(gpu, content, new TextMotion(TextPhase.None, 0, word, said)), Yellow);
        int none = YellowAt(-1, 0);
        int first = YellowAt(0, 1);
        int halfSecond = YellowAt(1, 0.5);
        int second = YellowAt(1, 1);
        int all = YellowAt(2, 1);
        output.WriteLine($"Amarillo: nada {none}, «uno» {first}, mitad de «dos» {halfSecond}, «dos» {second}, todo {all}");
        Assert.Equal(0, none);
        Assert.True(first > 500 && halfSecond > first + 100 && second > halfSecond + 100 && all > second + 200);

        // Solo la palabra que suena, en un título por unidades (con entrada por letra, ya terminada).
        style.Highlight.Mode = HighlightMode.Word;
        style.In = TextAnimationPresets.Find("desvanecer")!.In();
        GeneratedContent word = Content("uno dos tres", style, words: [(0, 3), (4, 3), (8, 4)]);
        int only = Count(Compose(gpu, word, new TextMotion(TextPhase.None, 0, 1, 0.5)), Yellow);
        Assert.InRange(only, (second - first) * 0.8, (second - first) * 1.2);
    }

    [GpuFact]
    public void Preview_and_strict_export_compose_unit_titles_identically()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style();
        style.Stroke = new StyleStroke { Color = "#202020", Width = 5 };
        style.Shadow = new StyleShadow { Color = "#00000099", Dx = 6, Dy = 8, Blur = 10 };
        style.Background = new StyleBackground { Color = "#3050A0C0", Padding = [24, 12], Radius = 12 };
        style.Path = new StylePath { Bend = 40 };
        style.In = TextAnimationPresets.Find("desenfoque")!.In(TextUnit.Letter, 0.5, order: UnitOrder.Random);
        GeneratedContent content = Content("Vista previa = exportación\nsegunda línea", style);
        var motion = new TextMotion(TextPhase.In, 0.4, -1, 0);
        var transform = new LayerTransform(120, 300, 1.2, 8, 0.9);
        Assert.Equal(Compose(gpu, content, motion, transform, strict: false).Pixels, Compose(gpu, content, motion, transform, strict: true).Pixels);
    }

    [GpuFact]
    public void Three_animated_lines_per_letter_compose_fast_at_1080p_and_4k()
    {
        using GpuDevice gpu = GpuDevice.Create();
        // Las cotas de tiempo valen con código optimizado y una GPU de verdad. En Debug o con WARP (la CI de Windows,
        // 4 núcleos compartidos con otros ensamblados) componer 4K tardó de 440 a 810 ms: ahí solo se informa y se
        // componen menos cuadros.
        bool timed = MeasuredGpuFactAttribute.CanTime(gpu);
        int frames = timed ? 150 : 40;
        int warmUp = timed ? 30 : 10;
        foreach ((int width, int height) in ((int, int)[])[(1920, 1080), (3840, 2160)])
        {
            TextStyle style = Style();
            style.Stroke = new StyleStroke { Color = "#000000", Width = 5 };
            style.Shadow = new StyleShadow { Color = "#00000099", Dy = 6, Blur = 8 };
            style.In = TextAnimationPresets.Find("rebote")!.In(TextUnit.Letter, 0.6);
            GeneratedContent content = Content("Tres líneas de título\nanimadas letra por letra\ncon borde y sombra", style, width: width, height: height);
            using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
            using var compositor = new Compositor(gpu, width, height, CompositeFormat.Rgba16Float) { Titles = titles };
            compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2));
            long rasterStart = Stopwatch.GetTimestamp();
            TitleTexture texture = titles.Rasterize(content)!;
            double raster = Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
            using var timer = new GpuTimer(gpu);
            var cpu = new List<double>();
            var gpuMs = new List<double>();
            long allocated = 0;
            for (int n = 0; n < frames; n++)
            {
                using var frame = new CompositionFrame(Time.Zero, width, height,
                    [new LayerFrame(0, "t", content, LayerTransform.Identity) { TextMotion = new TextMotion(TextPhase.In, (n % 60) / 60.0, -1, 0) }]);
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                timer.Begin();
                compositor.Render(frame);
                timer.End();
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long after = GC.GetAllocatedBytesForCurrentThread();
                if (n >= warmUp)
                {
                    cpu.Add(elapsed);
                    allocated += after - before;
                    if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } value)
                    {
                        gpuMs.Add(value);
                    }
                }
                else
                {
                    timer.WaitMilliseconds(TimeSpan.FromSeconds(2));
                }
            }

            cpu.Sort();
            gpuMs.Sort();
            double cpuMedian = cpu[cpu.Count / 2];
            double gpuMedian = gpuMs.Count > 0 ? gpuMs[gpuMs.Count / 2] : 0;
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{width}×{height}, {texture.Units!.UnitCount} letras, {texture.Units.CellCount} celdas, atlas {texture.Width}×{texture.Height}: rasterizar {raster:0.0} ms una vez; componer {cpuMedian:0.00} ms de CPU y {gpuMedian:0.00} ms de GPU por cuadro (mediana); reserva {allocated / cpu.Count} B por cuadro{(timed ? "" : " (tiempos solo informativos: Debug o WARP)")}"));
            Assert.Equal(content.Text.Count(c => !char.IsWhiteSpace(c)), texture.Units.UnitCount);
            if (timed)
            {
                Assert.True(cpuMedian < (Strict ? 3 : 20), $"Componer tomó {cpuMedian:0.00} ms de CPU.");
                Assert.True(gpuMedian < (Strict ? 2 : 20), $"Componer tomó {gpuMedian:0.00} ms de GPU.");
                Assert.True(raster < (Strict ? 250 : 800), $"Rasterizar tomó {raster:0.0} ms.");
            }
        }
    }

    internal static RgbaImage Compose(GpuDevice gpu, GeneratedContent content, TextMotion motion, LayerTransform? transform = null, bool strict = true)
    {
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        using var compositor = new Compositor(gpu, content.SequenceWidth, content.SequenceHeight, CompositeFormat.Rgba8Unorm) { Titles = titles };
        compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2));
        using var frame = new CompositionFrame(Time.Zero, content.SequenceWidth, content.SequenceHeight,
            [new LayerFrame(0, "titulo", content, transform ?? LayerTransform.Identity) { TextMotion = motion }]);
        if (!strict)
        {
            titles.Rasterize(content);
        }

        CompositeResult result = strict ? compositor.RenderStrict(frame) : compositor.Render(frame);
        Assert.Equal(0, result.EffectsSkipped);
        return new RgbaImage(compositor.ReadbackRgba(), content.SequenceWidth, content.SequenceHeight);
    }

    private static int Count(RgbaImage image, Func<(byte R, byte G, byte B, byte A), bool> where)
    {
        int n = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                n += where(image.At(x, y)) ? 1 : 0;
            }
        }

        return n;
    }

    private static int Ink(RgbaImage image, int from, int to)
    {
        int n = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = from; x < to; x++)
            {
                n += image.At(x, y).G > 128 ? 1 : 0;
            }
        }

        return n;
    }

    private static int MaxDifference(RgbaImage a, RgbaImage b) => a.Pixels.Zip(b.Pixels).Max(p => Math.Abs(p.First - p.Second));

    /// <summary>Altura media de la tinta en el tercio izquierdo, el del centro y el derecho de la imagen.</summary>
    private static (double Left, double Middle, double Right) Thirds(RgbaImage image)
    {
        double Mean(int from, int to)
        {
            double sum = 0;
            int n = 0;
            for (int y = 0; y < image.Height; y++)
            {
                for (int x = from; x < to; x++)
                {
                    if (image.At(x, y).G > 128)
                    {
                        sum += y;
                        n++;
                    }
                }
            }

            return n == 0 ? double.NaN : sum / n;
        }

        (int first, int last) = InkColumns(image);
        int third = (last - first) / 3;
        return (Mean(first, first + third), Mean(first + third, last - third), Mean(last - third, last + 1));
    }

    private static (int First, int Last) InkColumns(RgbaImage image)
    {
        int first = image.Width;
        int last = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.At(x, y).G > 128)
                {
                    first = Math.Min(first, x);
                    last = Math.Max(last, x);
                }
            }
        }

        return (first, last);
    }

    private static (double X, double Y) Center(TitleImage image, Func<(byte B, byte G, byte R, byte A), bool> where)
    {
        double n = 0;
        double sx = 0;
        double sy = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (where(image[x, y]))
                {
                    n++;
                    sx += x;
                    sy += y;
                }
            }
        }

        return (sx / n, sy / n);
    }
}
