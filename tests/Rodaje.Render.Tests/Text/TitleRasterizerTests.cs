using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Text;

/// <summary>
/// El rasterizador de títulos contra lo que se espera de cada parte del estilo: alfa premultiplicado, borde, sombra,
/// fondo, saltos de línea, ajuste, alineación y escrituras de derecha a izquierda; y su paso por el compositor.
/// </summary>
public sealed class TitleRasterizerTests(ITestOutputHelper output)
{
    private static readonly bool Strict = Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    [GpuFact]
    public void A_filled_title_is_premultiplied_with_opaque_glyphs_and_transparent_surroundings()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TitleTexture texture = titles.Rasterize(TitleImage.Content("HOLA", Style(fill: "#FF0000")))!;
        TitleImage image = TitleImage.Read(gpu, texture);

        Assert.False(texture.FontMissing);
        Assert.Equal(0, image[0, 0].A);
        Assert.True(image.Count(p => p.A == 255) > 2000, "Las letras de 96 px deberían tener interior opaco.");
        Assert.Equal(0, image.Count(p => p.R > p.A || p.G > p.A || p.B > p.A));
        Assert.Equal(0, image.Count(p => p.A > 0 && (p.G > 1 || p.B > 1)));
        Assert.True(image.Count(p => p.A is > 0 and < 255) > 100, "Los bordes de las letras deberían estar suavizados.");
        (double x, double y) = image.InkCenter();
        Assert.InRange(x, (image.Width / 2.0) - 40, (image.Width / 2.0) + 40);
        Assert.InRange(y, (image.Height / 2.0) - 30, (image.Height / 2.0) + 30);
    }

    [GpuFact]
    public void The_stroke_surrounds_the_fill_with_its_own_color()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle plain = Style(fill: "#FFFFFF");
        TextStyle stroked = Style(fill: "#FFFFFF");
        stroked.Stroke = new StyleStroke { Color = "#000000", Width = 6, Join = StrokeJoin.Round };
        TitleImage without = TitleImage.Read(gpu, titles.Rasterize(TitleImage.Content("Borde", plain))!);
        TitleTexture texture = titles.Rasterize(TitleImage.Content("Borde", stroked))!;
        TitleImage with = TitleImage.Read(gpu, texture);

        int inkWithout = without.Count(p => p.A > 128);
        int inkWith = with.Count(p => p.A > 128);
        int black = with.Count(p => p.A == 255 && p.R < 10 && p.G < 10 && p.B < 10);
        int white = with.Count(p => p.A == 255 && p.R > 245);
        output.WriteLine($"Tinta sin borde {inkWithout} px, con borde {inkWith} px ({black} negros, {white} blancos); textura {texture.Width}×{texture.Height}");
        Assert.True(inkWith > inkWithout * 1.3, "El borde de 6 px debería agrandar la silueta.");
        Assert.True(black > inkWithout / 4, "Debería haber un anillo negro opaco.");
        Assert.True(white > inkWithout / 3, "El relleno blanco sigue encima del borde.");
        Assert.True(texture.Width > without.Width);
    }

    [GpuFact]
    public void The_highlighted_word_takes_its_color_and_the_rest_keeps_the_fill()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.Highlight = new StyleHighlight { Color = "#FFD400" };
        const string Text = "uno dos tres";
        GeneratedContent plain = TitleImage.Content(Text, style);
        GeneratedContent second = new(GeneratorKinds.Text, Text, style, 0, 1920, 1080, 1, 4, 3) { ClipId = "titulo" };
        Assert.NotEqual(plain.Key, second.Key);

        TitleImage without = TitleImage.Read(gpu, titles.Rasterize(plain)!);
        TitleImage with = TitleImage.Read(gpu, titles.Rasterize(second)!);
        bool Yellow((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 245 && p.G is > 200 and < 225 && p.B < 10;
        bool White((byte B, byte G, byte R, byte A) p) => p.A == 255 && p.R > 245 && p.G > 245 && p.B > 245;
        int yellow = with.Count(Yellow), white = with.Count(White);
        output.WriteLine($"Amarillo {yellow} px, blanco {white} px (sin resaltar: {without.Count(White)} blancos)");
        Assert.Equal(0, without.Count(Yellow));
        Assert.True(yellow > 500, "«dos» debería verse amarillo.");
        Assert.True(white > yellow, "«uno» y «tres» siguen blancos.");

        // «dos» está en el medio: su tinta amarilla queda centrada entre la blanca de los lados.
        (double yellowX, _) = Center(with, Yellow);
        Assert.InRange(yellowX, (with.Width / 2.0) - 60, (with.Width / 2.0) + 60);
    }

    [GpuFact]
    public void The_shadow_is_offset_and_blurred_below_the_text()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle hard = Style(fill: "#FFFFFF");
        hard.Shadow = new StyleShadow { Color = "#FF0000", Dx = 30, Dy = 30, Blur = 0 };
        TextStyle soft = Style(fill: "#FFFFFF");
        soft.Shadow = new StyleShadow { Color = "#FF0000", Dx = 30, Dy = 30, Blur = 24 };
        TitleImage sharp = TitleImage.Read(gpu, titles.Rasterize(TitleImage.Content("I", hard))!);
        TitleImage blurred = TitleImage.Read(gpu, titles.Rasterize(TitleImage.Content("I", soft))!);

        // La sombra roja asoma abajo a la derecha del trazo blanco.
        int red = sharp.Count(p => p.A == 255 && p.R > 250 && p.G < 5);
        Assert.True(red > 200, $"La sombra dura debería verse roja y opaca ({red} px).");
        (double whiteX, double whiteY) = Center(sharp, p => p.A == 255 && p.G > 250);
        (double redX, double redY) = Center(sharp, p => p.A == 255 && p.R > 250 && p.G < 5);
        Assert.True(redX > whiteX && redY > whiteY, "La sombra va desplazada hacia abajo y a la derecha.");
        int partial = blurred.Count(p => p.A is > 5 and < 250 && p.R > p.G);
        Assert.True(partial > 2 * sharp.Count(p => p.A is > 5 and < 250 && p.R > p.G), "El desenfoque abre una penumbra.");
        Assert.True(blurred.Width > sharp.Width, "El margen crece con el desenfoque para no cortar la sombra.");
    }

    [GpuFact]
    public void The_background_covers_the_text_plus_its_padding()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.Background = new StyleBackground { Color = "#00FF00", Padding = [40, 20], Radius = 0 };
        TitleTexture texture = titles.Rasterize(TitleImage.Content("Fondo", style))!;
        TitleImage image = TitleImage.Read(gpu, texture);
        TitleImage text = TitleImage.Read(gpu, titles.Rasterize(TitleImage.Content("Fondo", Style(fill: "#FFFFFF")))!);

        int green = image.Count(p => p.A == 255 && p.G > 250 && p.R < 5);
        Assert.True(green > 0);
        // Lo que no es texto del fondo es verde: su caja mide la caja del texto más el margen a cada lado.
        int covered = image.Count(p => p.A == 255);
        output.WriteLine($"Fondo: {covered} px opacos en {texture.Width}×{texture.Height}");
        Assert.True(texture.LocalWidth >= text.Width + 80 - 4);
        Assert.True(image[image.Width / 2, 1].A == 0 || texture.LocalHeight > text.Height + 40 - 4);
        Assert.Equal(0, image[0, 0].A);
    }

    [GpuFact]
    public void Line_breaks_add_one_line_height_and_a_box_wraps_long_text()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.LineHeight = 1.25;
        TitleTexture one = titles.Rasterize(TitleImage.Content("Línea", style))!;
        TitleTexture two = titles.Rasterize(TitleImage.Content("Línea\nLínea", style))!;
        TitleTexture three = titles.Rasterize(TitleImage.Content("Línea\nLínea\nLínea", style))!;

        double step = 1.25 * 96;
        Assert.Equal(step, two.LocalHeight - one.LocalHeight, 0);
        Assert.Equal(step, three.LocalHeight - two.LocalHeight, 0);

        const string Long = "Un subtítulo largo que no cabe en una sola línea de cuatrocientos píxeles";
        TitleTexture wrapped = titles.Rasterize(TitleImage.Content(Long, style, boxWidth: 400))!;
        TitleTexture single = titles.Rasterize(TitleImage.Content(Long, style))!;
        output.WriteLine($"Sin caja {single.LocalWidth:0}×{single.LocalHeight:0}; caja de 400 px {wrapped.LocalWidth:0}×{wrapped.LocalHeight:0}");
        Assert.True(single.LocalWidth > 1500);
        Assert.True(wrapped.LocalWidth <= 400 + 8);
        Assert.True(wrapped.LocalHeight >= one.LocalHeight + (3 * step) - 2, "Debería ocupar al menos cuatro líneas.");
    }

    [GpuFact]
    public void Alignment_moves_short_lines_inside_the_box()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        double Ink(TextAlign align)
        {
            TextStyle style = Style(fill: "#FFFFFF");
            style.Align = align;
            // Desde el centro de la caja (el de la textura), en píxeles de la secuencia.
            TitleTexture texture = titles.Rasterize(TitleImage.Content("ab", style, boxWidth: 1200))!;
            return (TitleImage.Read(gpu, texture).InkCenter().X - (texture.Width / 2.0)) / texture.Scale;
        }

        double left = Ink(TextAlign.Left);
        double center = Ink(TextAlign.Center);
        double right = Ink(TextAlign.Right);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Centro de la tinta: izquierda {left:0}, centro {center:0}, derecha {right:0}"));
        Assert.InRange(center, -10, 10);
        Assert.True(left < -450 && right > 450);
    }

    [GpuFact]
    public void Right_to_left_and_mixed_scripts_rasterize_with_system_fallback()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TitleTexture hebrew = titles.Rasterize(TitleImage.Content("שלום עולם", Style(fill: "#FFFFFF")))!;
        TitleTexture arabic = titles.Rasterize(TitleImage.Content("مرحبا بالعالم", Style(fill: "#FFFFFF")))!;
        TitleTexture mixed = titles.Rasterize(TitleImage.Content("Hola ‏שלום‎ 123", Style(fill: "#FFFFFF")))!;

        foreach (TitleTexture texture in new[] { hebrew, arabic, mixed })
        {
            Assert.True(TitleImage.Read(gpu, texture).Count(p => p.A == 255) > 500);
        }

        // Con alineación a la izquierda, un párrafo hebreo sigue escribiéndose de derecha a izquierda dentro de su caja:
        // la primera palabra (la más ancha aquí) queda a la derecha.
        TextStyle left = Style(fill: "#FFFFFF");
        left.Align = TextAlign.Left;
        TitleImage wide = TitleImage.Read(gpu, titles.Rasterize(TitleImage.Content("שלוםשלוםשלום עו", left))!);
        Assert.True(wide.InkCenter().X > wide.Width / 2.0, "La palabra larga del principio debería quedar a la derecha.");
    }

    [GpuFact]
    public void A_missing_family_falls_back_and_is_reported()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.Font = new StyleFont { Family = "Fuente Que No Existe", Fallback = ["Arial"], Source = "rodaje-fuentes@1.0.0/nada" };
        GeneratedContent content = TitleImage.Content("Respaldo", style);

        TitleTexture texture = titles.Rasterize(content)!;
        Assert.True(texture.FontMissing);
        MissingFont missing = Assert.Single(titles.FindMissingFonts([content, TitleImage.Content("Otra", Style(fill: "#FFFFFF")), content]));
        Assert.Equal("Fuente Que No Existe", missing.Family);
        Assert.Equal("Arial", missing.UsedFamily);
        Assert.Equal("titulo", missing.ClipId);
        Assert.Equal("rodaje-fuentes@1.0.0/nada", missing.Source);
    }

    [GpuFact]
    public void The_font_pass_does_not_wait_for_a_rasterization_in_progress()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.Font = new StyleFont { Family = "Fuente Que No Existe", Fallback = ["Arial"] };
        GeneratedContent content = TitleImage.Content("Respaldo", style);

        // Con el candado del rasterizado tomado (como mientras el hilo de fondo dibuja 1500 emoji), la pasada responde.
        // Si esperara al candado no terminaría nunca (se suelta al salir del using), así que el tope es contra cuelgues y
        // no una medida: con 5 s fallaba en la CI de Windows, donde la primera pasada arma la colección de fuentes del
        // sistema con todos los proyectos de prueba a la vez en 4 núcleos. Va en un hilo propio para no esperar además
        // a que el pool, ocupado por las otras pruebas, la atienda.
        Task<IReadOnlyList<MissingFont>> pass;
        using (titles.RasterLock.EnterScope())
        {
            pass = Task.Factory.StartNew(() => titles.FindMissingFonts([content]), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(pass.Wait(TimeSpan.FromSeconds(60)), "la pasada de fuentes esperó al rasterizado");
        }

        Assert.Equal("Fuente Que No Existe", Assert.Single(pass.Result).Family);
    }

    [GpuFact]
    public void Private_fonts_register_without_installing_and_bad_files_are_rejected()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string real = Path.Combine(fonts, "consola.ttf");
        string broken = Path.Combine(Path.GetTempPath(), $"rodaje-fuente-rota-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(broken, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 31))]);
        try
        {
            IReadOnlyList<string> rejected = titles.RegisterFonts([real, broken, Path.Combine(fonts, "no-existe.ttf")]);
            Assert.DoesNotContain(real, rejected);
            Assert.Contains(broken, rejected);
            Assert.Equal(2, rejected.Count);

            TextStyle style = Style(fill: "#FFFFFF");
            style.Font = new StyleFont { Family = "Consolas" };
            Assert.False(titles.Rasterize(TitleImage.Content("Privada", style))!.FontMissing);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [GpuFact]
    public void A_title_larger_than_its_byte_cap_is_rasterized_smaller()
    {
        using GpuDevice gpu = GpuDevice.Create();
        const long Cap = 256 * 1024;
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false, MaxTitleBytes = Cap });
        TitleTexture texture = titles.Rasterize(TitleImage.Content("Enorme", Style(fill: "#FFFFFF", size: 400), scale: 4))!;

        Assert.True(texture.Downscaled);
        Assert.True(texture.Bytes <= Cap, $"{texture.Bytes} B pasan del tope de {Cap} B.");
        Assert.True(texture.Scale < 4);
        TitleTexture normal = titles.Rasterize(TitleImage.Content("Enorme", Style(fill: "#FFFFFF", size: 400), scale: 1.5))!;
        Assert.InRange(texture.LocalWidth, normal.LocalWidth - 4, normal.LocalWidth + 4);
    }

    [GpuFact]
    public void Equal_descriptors_share_one_texture_and_prepare_rasterizes_in_the_background()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu);
        using var changed = new SemaphoreSlim(0);
        titles.Changed += (_, _) => changed.Release();
        GeneratedContent content = TitleImage.Content("Caché", Style(fill: "#FFFFFF"));
        GeneratedContent same = TitleImage.Content("Caché", Style(fill: "#FFFFFF"));

        Assert.False(titles.Prepare(content));
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "El hilo de títulos no terminó.");
        Assert.True(titles.Prepare(same));
        Assert.Same(titles.TryGet(content), titles.TryGet(same));
        Assert.Same(titles.TryGet(content), titles.Rasterize(same));
        Assert.Equal(1, titles.Rasterized);
    }

    [GpuFact]
    public void The_cache_evicts_old_titles_but_keeps_the_ones_on_screen()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false, CacheCapBytes = 3L * 1024 * 1024 });
        GeneratedContent onScreen = TitleImage.Content("En pantalla", Style(fill: "#FFFFFF"));
        TitleTexture kept = titles.Rasterize(onScreen)!;
        for (int i = 0; i < 40; i++)
        {
            titles.BeginFrame();
            Assert.NotNull(titles.TryGet(onScreen));
            titles.Rasterize(TitleImage.Content($"Subtítulo número {i}", Style(fill: "#FFFFFF")));
        }

        Assert.True(titles.CachedBytes <= 3L * 1024 * 1024 + kept.Bytes, $"{titles.CachedBytes} B en caché.");
        Assert.Same(kept, titles.TryGet(onScreen));
    }

    [GpuFact]
    public void The_compositor_places_the_title_with_its_transform_and_counts_pending_ones()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        using var compositor = new Compositor(gpu, 1920, 1080, CompositeFormat.Rgba8Unorm) { Titles = titles };
        compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2));
        GeneratedContent content = TitleImage.Content("■", Style(fill: "#FFFFFF", size: 200));

        // Sin rasterizar todavía: el cuadro sale sin el título y se cuenta.
        CompositeResult pending = Compose(compositor, content, LayerTransform.Identity);
        Assert.Equal(1, pending.TitlesPending);
        Assert.Equal(0, pending.LayersDrawn);

        titles.Rasterize(content);
        Assert.Equal(1, Compose(compositor, content, LayerTransform.Identity).LayersDrawn);
        var centered = new RgbaImage(compositor.ReadbackRgba(), 1920, 1080);
        Assert.True(centered.At(960, 540).R > 240, "El cuadrado blanco debería estar en el centro.");

        Compose(compositor, content, LayerTransform.Identity with { X = 600, Y = -300, Opacity = 0.5 });
        var moved = new RgbaImage(compositor.ReadbackRgba(), 1920, 1080);
        Assert.True(moved.At(960, 540).R < 10, "El centro queda negro al mover el título.");
        Assert.InRange(moved.At(1560, 240).R, 120, 136);
    }

    [GpuFact]
    public void Preview_and_strict_export_compose_titles_identically()
    {
        using GpuDevice gpu = GpuDevice.Create();
        TextStyle style = Style(fill: "#FFDD00");
        style.Stroke = new StyleStroke { Color = "#202020", Width = 5 };
        style.Shadow = new StyleShadow { Color = "#00000099", Dx = 6, Dy = 8, Blur = 10 };
        style.Background = new StyleBackground { Color = "#3050A0C0", Padding = [24, 12], Radius = 12 };
        GeneratedContent content = TitleImage.Content("Vista previa = exportación\nsegunda línea", style);
        var transform = new LayerTransform(120, 300, 1.2, 8, 0.9);
        EffectValues blurValues = default;
        blurValues[0] = 6;
        var blur = new LayerEffect(EffectRegistry.Find(BuiltInEffects.Blur)!.Code, blurValues, null);

        byte[] Run(bool strict, bool withBlur)
        {
            using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
            using var compositor = new Compositor(gpu, 1920, 1080, CompositeFormat.Rgba16Float) { Titles = titles };
            compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2));
            using var frame = new CompositionFrame(Time.Zero, 1920, 1080,
                [new LayerFrame(0, "titulo", content, transform) { Effects = withBlur ? [blur] : [] }]);
            if (!strict)
            {
                titles.Rasterize(content);
            }

            CompositeResult result = strict ? compositor.RenderStrict(frame) : compositor.Render(frame);
            Assert.Equal(1, result.LayersDrawn);
            Assert.Equal(withBlur ? 1 : 0, result.EffectsApplied);
            return compositor.ReadbackRgba();
        }

        Assert.Equal(Run(strict: false, withBlur: false), Run(strict: true, withBlur: false));
        Assert.Equal(Run(strict: false, withBlur: true), Run(strict: true, withBlur: true));
    }

    [GpuFact]
    public void Rasterizing_a_1080p_title_with_stroke_and_shadow_is_fast()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        TextStyle style = Style(fill: "#FFFFFF");
        style.Stroke = new StyleStroke { Color = "#000000", Width = 6 };
        style.Shadow = new StyleShadow { Color = "#00000099", Dx = 4, Dy = 6, Blur = 8 };
        titles.Rasterize(TitleImage.Content("Calentamiento", style));

        var plain = new List<double>();
        var full = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            long start = Stopwatch.GetTimestamp();
            titles.Rasterize(TitleImage.Content($"Título simple {i}", Style(fill: "#FFFFFF")));
            plain.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = Stopwatch.GetTimestamp();
            titles.Rasterize(TitleImage.Content($"Dos líneas con borde\ny sombra número {i}", style));
            full.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        plain.Sort();
        full.Sort();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{gpu.AdapterName}: relleno solo mediana {plain[15]:0.00} ms (p90 {plain[27]:0.00}); dos líneas con borde y sombra mediana {full[15]:0.00} ms (p90 {full[27]:0.00})"));
        // Con WARP la parte de GPU corre en la CPU (122 ms forzando WARP en un equipo con GPU) y en Debug solo se informa.
        Assert.True(!MeasuredGpuFactAttribute.CanTime(gpu) || full[15] < (Strict ? 2 : 20), $"Rasterizar un título 1080p con borde y sombra tomó {full[15]:0.00} ms.");
    }

    [MeasuredGpuFact]
    public void Composing_a_ready_title_costs_little_gpu_time()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu, new TitleRasterizerOptions { BackgroundThread = false });
        using var compositor = new Compositor(gpu, 1920, 1080, CompositeFormat.Rgba16Float) { Titles = titles };
        compositor.ShadersReady.Wait(TimeSpan.FromMinutes(2));
        using var timer = new GpuTimer(gpu);
        TextStyle style = Style(fill: "#FFFFFF");
        style.Stroke = new StyleStroke { Color = "#000000", Width = 6 };
        GeneratedContent content = TitleImage.Content("Subtítulo de dos líneas\nsobre el video", style);
        titles.Rasterize(content);
        using var empty = new CompositionFrame(Time.Zero, 1920, 1080, []);
        using var frame = new CompositionFrame(Time.Zero, 1920, 1080, [new LayerFrame(0, "t", content, LayerTransform.Identity with { Y = 380 })]);

        double Median(CompositionFrame f)
        {
            var ms = new List<double>();
            for (int n = 0; n < 230; n++)
            {
                timer.Begin();
                compositor.Render(f);
                timer.End();
                if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } value && n >= 30)
                {
                    ms.Add(value);
                }
            }

            ms.Sort();
            return ms[ms.Count / 2];
        }

        double cost = Median(frame) - Median(empty);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{gpu.AdapterName}: un título 1080p cuesta {cost:0.000} ms de GPU por cuadro"));
        Assert.True(cost < (Strict ? 0.15 : 2), $"Componer un título tomó {cost:0.000} ms de GPU.");
    }

    internal static TextStyle Style(string fill, double size = 96) => new()
    {
        Size = size,
        Font = new StyleFont { Family = "Segoe UI", Weight = 700 },
        Fill = new StyleFill { Color = fill },
    };

    private static CompositeResult Compose(Compositor compositor, GeneratedContent content, LayerTransform transform)
    {
        using var frame = new CompositionFrame(Time.Zero, 1920, 1080, [new LayerFrame(0, "titulo", content, transform)]);
        return compositor.Render(frame);
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
