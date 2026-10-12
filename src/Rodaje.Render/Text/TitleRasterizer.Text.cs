using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Render.Text;

/// <summary>El texto armado con DirectWrite (tramos, mayúsculas, tipografía y ajuste a la caja) y el dibujo del bloque entero.</summary>
public sealed unsafe partial class TitleRasterizer
{
    /// <summary>Rasteriza con el candado <see cref="_raster"/> tomado. Nula si no hay nada que dibujar.</summary>
    private TitleTexture? Draw(GeneratedContent content)
    {
        if (content.Kind != GeneratorKinds.Text || _fonts is null)
        {
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        TextStyle style = content.Style;
        float size = (float)(style.Size * content.UnitScale);
        if (!(size > 0) || !float.IsFinite(size))
        {
            return null;
        }

        ResolvedFont font = _fonts.Resolve(style.Font);
        using LaidOut text = LayOut(content, font, size);
        using var brushes = new TitleBrushes(this, content, text);
        TitleTexture? texture = content.UsesUnits ? DrawUnits(content, text, brushes, font) : DrawBlock(content, text, brushes, font);
        if (texture is not null)
        {
            Rasterized++;
            LastRasterMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        return texture;
    }

    /// <summary>El layout del texto a <paramref name="size"/> (píxeles de la secuencia), achicado o agrandado para la caja si el estilo lo pide.</summary>
    private LaidOut LayOut(GeneratedContent content, ResolvedFont font, float size)
    {
        StyleTypography? typography = content.Style.Typography;
        string text = content.Text;
        bool[] lowered = new bool[text.Length];
        string drawn = TextCasing.Apply(text, typography?.Case ?? TextCase.None, lowered);
        float factor = 1;
        if (typography is { Fit: not TextFit.None } && (content.BoxWidth > 0 || content.BoxHeight > 0))
        {
            bool Fits(float f)
            {
                using LaidOut trial = Create(content, font, drawn, lowered, size * f, f);
                DWRITE_TEXT_METRICS m = trial.Metrics;
                return (content.BoxWidth <= 0 || m.width <= content.BoxWidth + 0.5) && (content.BoxHeight <= 0 || m.height <= content.BoxHeight + 0.5);
            }

            bool grow = typography.Fit == TextFit.Fill;
            if (grow || !Fits(1))
            {
                float lo = 0.02f;
                float hi = grow ? 16 : 1;
                if (Fits(lo))
                {
                    for (int i = 0; i < 16; i++)
                    {
                        float mid = (lo + hi) / 2;
                        (lo, hi) = Fits(mid) ? (mid, hi) : (lo, mid);
                    }
                }

                factor = lo;
            }
        }

        return Create(content, font, drawn, lowered, size * factor, factor);
    }

    private LaidOut Create(GeneratedContent content, ResolvedFont font, string drawn, bool[] lowered, float size, float factor)
    {
        TextStyle style = content.Style;
        IDWriteTextFormat* format = null;
        IDWriteTextLayout* layout = null;
        IDWriteTextLayout1* spaced = null;
        IDWriteTypography* typography = null;
        try
        {
            var weight = (DWRITE_FONT_WEIGHT)Math.Clamp(style.Font.Weight, 1, 999);
            DWRITE_FONT_STYLE slant = style.Font.Italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;
            fixed (char* family = font.Family)
            fixed (char* locale = "es-es")
            {
                Check(_dwrite->CreateTextFormat(family, font.Collection, weight, slant, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale,
                    &format), "CreateTextFormat");
            }

            Check(format->SetTextAlignment(style.Align switch
            {
                TextAlign.Left => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
                TextAlign.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
            }), "SetTextAlignment");
            bool wrap = content.BoxWidth > 0;
            Check(format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP),
                "SetWordWrapping");
            double lineHeight = double.IsFinite(style.LineHeight) && style.LineHeight > 0 ? style.LineHeight : 1.2;
            bool mixedSizes = content.Spans.Any(s => s.Scale is { } scale && scale != 1) || lowered.Contains(true);
            if (mixedSizes)
            {
                // Con letras de varios tamaños cada línea mide lo que su letra más grande: interlineado proporcional.
                float proportion = (float)(lineHeight / 1.2);
                Check(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD.DWRITE_LINE_SPACING_METHOD_PROPORTIONAL, proportion, proportion),
                    "SetLineSpacing(proporcional)");
            }
            else
            {
                float spacing = (float)(lineHeight * size);
                Check(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD.DWRITE_LINE_SPACING_METHOD_UNIFORM, spacing, spacing * 0.8f), "SetLineSpacing");
            }

            const float Unbounded = 1_000_000;
            fixed (char* chars = drawn)
            {
                Check(_dwrite->CreateTextLayout(chars, (uint)drawn.Length, format, wrap ? (float)content.BoxWidth : Unbounded, Unbounded, &layout),
                    "CreateTextLayout");
            }

            float letterSpacing = (float)(style.LetterSpacing * content.UnitScale * factor);
            if (letterSpacing != 0 && float.IsFinite(letterSpacing) && drawn.Length > 0
                && layout->QueryInterface(__uuidof<IDWriteTextLayout1>(), (void**)&spaced).SUCCEEDED)
            {
                Check(((IDWriteTextLayout1*)spaced)->SetCharacterSpacing(0, letterSpacing, 0, Range(0, drawn.Length)), "SetCharacterSpacing");
            }

            ApplySpans(layout, content, lowered, size);
            if (style.Typography is { } t && (!t.Kerning || t.Features.Count > 0) && drawn.Length > 0)
            {
                Check(_dwrite->CreateTypography(&typography), "CreateTypography");
                if (!t.Kerning)
                {
                    Check(typography->AddFontFeature(new DWRITE_FONT_FEATURE { nameTag = Tag("kern"), parameter = 0 }), "AddFontFeature(kern)");
                }

                foreach (string feature in t.Features.Where(StyleTypography.IsFeature))
                {
                    bool off = feature[0] == '-';
                    Check(typography->AddFontFeature(new DWRITE_FONT_FEATURE { nameTag = Tag(off ? feature[1..] : feature), parameter = off ? 0u : 1u }),
                        "AddFontFeature");
                }

                Check(layout->SetTypography(typography, Range(0, drawn.Length)), "SetTypography");
            }

            DWRITE_TEXT_METRICS measured;
            Check(layout->GetMetrics(&measured), "GetMetrics");
            float layoutWidth = wrap ? (float)content.BoxWidth : Math.Max(1, measured.widthIncludingTrailingWhitespace);
            float layoutHeight = Math.Max(1, measured.height);
            Check(layout->SetMaxWidth(layoutWidth), "SetMaxWidth");
            Check(layout->SetMaxHeight(layoutHeight), "SetMaxHeight");
            Check(layout->GetMetrics(&measured), "GetMetrics");
            var result = new LaidOut(layout, layoutWidth, layoutHeight, measured, drawn, size);
            layout = null;
            return result;
        }
        finally
        {
            Com.Release(ref typography);
            Com.Release(ref spaced);
            Com.Release(ref layout);
            Com.Release(ref format);
        }
    }

    /// <summary>Peso, cursiva, subrayado y tamaño de los tramos (el color va con los pinceles); y las versalitas al 75 %.</summary>
    private static void ApplySpans(IDWriteTextLayout* layout, GeneratedContent content, bool[] lowered, float size)
    {
        int length = content.Text.Length;
        foreach (TextSpan span in content.Spans)
        {
            int start = Math.Clamp(span.Start, 0, length);
            int end = Math.Clamp(span.End, start, length);
            if (end <= start)
            {
                continue;
            }

            DWRITE_TEXT_RANGE range = Range(start, end - start);
            if (span.Weight is { } weight)
            {
                Check(layout->SetFontWeight((DWRITE_FONT_WEIGHT)Math.Clamp(weight, 1, 999), range), "SetFontWeight");
            }

            if (span.Italic is { } italic)
            {
                Check(layout->SetFontStyle(italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, range),
                    "SetFontStyle");
            }

            if (span.Underline is { } underline)
            {
                Check(layout->SetUnderline(underline, range), "SetUnderline");
            }
        }

        // El tamaño de cada carácter: el del tramo por el de las versalitas; se aplica por trozos iguales.
        int i = 0;
        while (i < length)
        {
            double scale = SizeAt(content.Spans, lowered, i);
            int j = i + 1;
            while (j < length && SizeAt(content.Spans, lowered, j) == scale)
            {
                j++;
            }

            if (scale != 1)
            {
                Check(layout->SetFontSize((float)(size * scale), Range(i, j - i)), "SetFontSize");
            }

            i = j;
        }
    }

    private static double SizeAt(IReadOnlyList<TextSpan> spans, bool[] lowered, int index)
    {
        double scale = TextSpans.At(spans, index)?.Scale ?? 1;
        return Math.Clamp(scale, TextSpans.MinScale, TextSpans.MaxScale) * (lowered[index] ? TextCasing.SmallCapsScale : 1);
    }

    private static DWRITE_TEXT_RANGE Range(int start, int length) => new() { startPosition = (uint)start, length = (uint)length };

    private static DWRITE_FONT_FEATURE_TAG Tag(string tag) =>
        (DWRITE_FONT_FEATURE_TAG)(tag[0] | ((uint)tag[1] << 8) | ((uint)tag[2] << 16) | ((uint)tag[3] << 24));

    /// <summary>El bloque entero en una textura, como siempre: fondo, sombras, bordes del más ancho al más angosto y el relleno.</summary>
    private TitleTexture? DrawBlock(GeneratedContent content, LaidOut text, TitleBrushes brushes, ResolvedFont font)
    {
        Shape shape = Measure(content, text);
        double scale = content.RasterScale;
        (int width, int height) = TextureSize(shape, scale);
        bool downscaled = false;
        long bytes = (long)width * height * 4;
        if (bytes > MaxTitleBytes || width > MaxTextureSide || height > MaxTextureSide)
        {
            double fit = Math.Min(Math.Sqrt((double)MaxTitleBytes / bytes),
                Math.Min((double)MaxTextureSide / width, (double)MaxTextureSide / height));
            scale *= fit * 0.98;
            (width, height) = TextureSize(shape, scale);
            downscaled = true;
        }

        RenderTarget? target = CreateTarget(width, height);
        try
        {
            Matrix3x2 toTexture = Matrix3x2.CreateScale((float)scale)
                * Matrix3x2.CreateTranslation((width / 2f) - (shape.Center.X * (float)scale), (height / 2f) - (shape.Center.Y * (float)scale));
            brushes.Plain(text.Layout, content.HighlightStart, content.HighlightLength);
            RenderBlock(content, text, brushes, shape, target, toTexture, (float)scale);
            var texture = new TitleTexture(target, width / scale, height / scale, scale, font.Missing, downscaled);
            target = null;
            return texture;
        }
        finally
        {
            target?.Dispose();
        }
    }

    /// <summary>Rectángulos del título en píxeles de la secuencia, con el origen en la esquina de la caja del texto.</summary>
    private static Shape Measure(GeneratedContent content, LaidOut text)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        DWRITE_OVERHANG_METRICS overhang;
        Check(text.Layout->GetOverhangMetrics(&overhang), "GetOverhangMetrics");
        DWRITE_TEXT_METRICS metrics = text.Metrics;
        var ink = new Rect(-overhang.left, -overhang.top, text.Width + overhang.right, text.Height + overhang.bottom);
        var box = new Rect(metrics.left, metrics.top, metrics.left + metrics.width, metrics.top + metrics.height);
        ink = ink.IsEmpty ? box : ink;
        float stroke = StrokeReach(style, unit);
        Rect bounds = ink.Union(box);
        Rect outlined = stroke > 0 ? ink.Inflate(stroke + 1) : ink;
        bounds = bounds.Union(outlined);

        Rect? background = null;
        if (style.Background is { } b)
        {
            float px = (float)((b.Padding.Count > 0 ? b.Padding[0] : 0) * unit);
            float py = (float)((b.Padding.Count > 1 ? b.Padding[1] : 0) * unit);
            background = box.Inflate(Math.Max(0, px), Math.Max(0, py));
            bounds = bounds.Union(background.Value);
        }

        foreach (StyleShadow shadow in style.AllShadows())
        {
            bounds = bounds.Union(Cast(outlined, shadow, unit));
        }

        var center = new Vector2(text.Width / 2, text.Height / 2);
        float halfWidth = Math.Max(Math.Abs(bounds.Left - center.X), Math.Abs(bounds.Right - center.X));
        float halfHeight = Math.Max(Math.Abs(bounds.Top - center.Y), Math.Abs(bounds.Bottom - center.Y));
        return new Shape(center, halfWidth, halfHeight, background, box);
    }

    /// <summary>Lo más que sale un borde del contorno de la letra (en inglete, el doble).</summary>
    private static float StrokeReach(TextStyle style, double unit)
    {
        float reach = 0;
        foreach (StyleStroke s in style.AllStrokes())
        {
            if (s.Width > 0 && double.IsFinite(s.Width))
            {
                reach = Math.Max(reach, (float)(s.Width * unit) * (s.Join == StrokeJoin.Miter ? 2 : 1));
            }
        }

        return reach;
    }

    /// <summary>Lo que cubre una sombra de lo que cubre <paramref name="body"/>: desplazado y con tres sigmas de margen.</summary>
    private static Rect Cast(Rect body, StyleShadow shadow, double unit) =>
        body.Offset((float)(shadow.Dx * unit), (float)(shadow.Dy * unit)).Inflate((3 * Sigma(shadow, unit)) + 1);

    private static float Sigma(StyleShadow shadow, double unit) => (float)(Math.Clamp(double.IsFinite(shadow.Blur) ? shadow.Blur : 0, 0, 64) * unit / 2);

    private static (int Width, int Height) TextureSize(Shape shape, double scale) =>
        (Math.Max(1, (int)Math.Ceiling(2 * shape.HalfWidth * scale) + 2), Math.Max(1, (int)Math.Ceiling(2 * shape.HalfHeight * scale) + 2));

    private RenderTarget CreateTarget(int width, int height, int mipLevels = 1)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = (uint)mipLevels,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            MiscFlags = mipLevels > 1 ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(título)");
        ID3D11ShaderResourceView* srv = null;
        HRESULT hr = _device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv);
        if (hr.FAILED)
        {
            texture->Release();
            Check(hr, "CreateShaderResourceView(título)");
        }

        long bytes = RenderTarget.BytesFor(width, height, desc.Format);
        return new RenderTarget(texture, null, srv, width, height, desc.Format, mipLevels > 1 ? bytes * 4 / 3 : bytes);
    }

    /// <summary>El mapa de bits de Direct2D que dibuja sobre la textura.</summary>
    private ID2D1Bitmap1* TargetBitmap(RenderTarget target)
    {
        IDXGISurface* surface = null;
        try
        {
            Check(target.Texture->QueryInterface(__uuidof<IDXGISurface>(), (void**)&surface), "QueryInterface(IDXGISurface)");
            D2D1_BITMAP_PROPERTIES1 properties = BitmapProperties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            ID2D1Bitmap1* bitmap;
            Check(_dc->CreateBitmapFromDxgiSurface(surface, &properties, &bitmap), "CreateBitmapFromDxgiSurface");
            return bitmap;
        }
        finally
        {
            Com.Release(ref surface);
        }
    }

    private static D2D1_BITMAP_PROPERTIES1 BitmapProperties(D2D1_BITMAP_OPTIONS options) => new()
    {
        pixelFormat = new D2D1_PIXEL_FORMAT
        {
            format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
        },
        dpiX = 96,
        dpiY = 96,
        bitmapOptions = options,
    };

    private ID2D1Bitmap1* LayerBitmap(int width, int height)
    {
        D2D1_BITMAP_PROPERTIES1 properties = BitmapProperties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET);
        ID2D1Bitmap1* layer;
        Check(_dc->CreateBitmap(new D2D_SIZE_U((uint)width, (uint)height), null, 0, &properties, &layer), "CreateBitmap(capa del título)");
        return layer;
    }

    /// <summary>
    /// El dibujo del bloque: borde y relleno en una capa aparte si hay sombras (cada sombra sale de su alfa sin dibujar dos
    /// veces el contorno), el fondo redondeado, las sombras en orden y la capa encima.
    /// </summary>
    private void RenderBlock(GeneratedContent content, LaidOut text, TitleBrushes brushes, Shape shape, RenderTarget target, Matrix3x2 toTexture,
        float scale)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        ID2D1Bitmap1* bitmap = null;
        ID2D1Bitmap1* layer = null;
        ID2D1SolidColorBrush* background = null;
        var outlines = new List<nint>();
        ID2D1GeometryGroup* outline = null;
        try
        {
            bitmap = TargetBitmap(target);
            D2D_MATRIX_3X2_F world = Matrix(toTexture);
            if (brushes.HasStrokes)
            {
                outlines = GlyphOutlines.Collect(text.Layout, (ID2D1Factory*)_d2d, Matrix3x2.Identity);
                outline = Group(outlines);
            }

            if (style.Background is { } bg && shape.Background is not null)
            {
                background = brushes.Solid(bg.Color);
            }

            bool shadows = style.AllShadows().Any();
            _gpu.LockContext();
            try
            {
                if (shadows)
                {
                    layer = LayerBitmap(target.Width, target.Height);
                    _dc->SetTarget((ID2D1Image*)layer);
                    _dc->BeginDraw();
                    var none = default(DXGI_RGBA);
                    _dc->Clear(&none);
                    DrawBody(text, brushes, outline, &world);
                    Check(_dc->EndDraw(null, null), "EndDraw(capa del título)");
                }

                _dc->SetTarget((ID2D1Image*)bitmap);
                _dc->BeginDraw();
                var clear = default(DXGI_RGBA);
                _dc->Clear(&clear);
                if (background != null)
                {
                    FillBackground(shape.Background!.Value, style.Background!, unit, background, &world);
                }

                if (layer != null)
                {
                    DrawShadows(style, unit, layer, Vector2.Zero, scale);
                    D2D_MATRIX_3X2_F identity = Matrix(Matrix3x2.Identity);
                    _dc->SetTransform(&identity);
                    var origin = new D2D_POINT_2F(0, 0);
                    _dc->DrawImage((ID2D1Image*)layer, &origin, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
                        D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
                }
                else
                {
                    DrawBody(text, brushes, outline, &world);
                }

                HRESULT hr = _dc->EndDraw(null, null);
                DetachShadows();
                _dc->SetTarget(null);

                // Direct2D deja su estado en el contexto inmediato, que comparten el compositor y FFmpeg.
                ((ID3D11DeviceContext*)_gpu.NativeContext)->ClearState();
                Check(hr, "EndDraw(título)");
            }
            finally
            {
                _gpu.UnlockContext();
            }
        }
        finally
        {
            Com.Release(ref outline);
            GlyphOutlines.Release(outlines);
            Com.Release(ref layer);
            Com.Release(ref background);
            Com.Release(ref bitmap);
        }
    }

    private ID2D1GeometryGroup* Group(List<nint> geometries)
    {
        if (geometries.Count == 0)
        {
            return null;
        }

        ID2D1GeometryGroup* group;
        fixed (nint* items = CollectionsMarshal.AsSpan(geometries))
        {
            Check(((ID2D1Factory*)_d2d)->CreateGeometryGroup(D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING, (ID2D1Geometry**)items, (uint)geometries.Count,
                &group), "CreateGeometryGroup");
        }

        return group;
    }

    private void FillBackground(Rect box, StyleBackground style, double unit, ID2D1SolidColorBrush* brush, D2D_MATRIX_3X2_F* world)
    {
        float radius = (float)(Math.Max(0, style.Radius) * unit);
        var rounded = new D2D1_ROUNDED_RECT
        {
            rect = new D2D_RECT_F(box.Left, box.Top, box.Right, box.Bottom),
            radiusX = radius,
            radiusY = radius,
        };
        _dc->SetTransform(world);
        _dc->FillRoundedRectangle(&rounded, (ID2D1Brush*)brush);
    }

    /// <summary>
    /// Los bordes (del más ancho al más angosto, cada uno al doble de su ancho porque la mitad queda bajo el relleno) y el
    /// relleno encima, con el transform del layout ya armado; los pinceles ya llevan los tramos.
    /// </summary>
    private void DrawBody(LaidOut text, TitleBrushes brushes, ID2D1GeometryGroup* outline, D2D_MATRIX_3X2_F* world)
    {
        _dc->SetTransform(world);
        if (outline != null)
        {
            foreach ((nint brush, nint strokeStyle, float width) in brushes.Strokes)
            {
                _dc->DrawGeometry((ID2D1Geometry*)outline, (ID2D1Brush*)brush, 2 * width, (ID2D1StrokeStyle*)strokeStyle);
            }
        }

        _dc->DrawTextLayout(new D2D_POINT_2F(0, 0), text.Layout, brushes.Fill,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NO_SNAP | D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    }

    /// <summary>Cada sombra del estilo, en orden, desde el alfa de <paramref name="layer"/> desplazada además por <paramref name="origin"/>.</summary>
    private void DrawShadows(TextStyle style, double unit, ID2D1Bitmap1* layer, Vector2 origin, float scale)
    {
        D2D_MATRIX_3X2_F identity = Matrix(Matrix3x2.Identity);
        _dc->SetTransform(&identity);
        int index = 0;
        foreach (StyleShadow cast in style.AllShadows())
        {
            ID2D1Effect* effect = ShadowEffectAt(index++);
            float sigma = Sigma(cast, unit) * scale;
            DXGI_RGBA color = TitleColor.Parse(cast.Color);
            effect->SetInput(0, (ID2D1Image*)layer, false);
            Check(effect->SetValue(0, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&sigma, sizeof(float)), "SetValue(desviación)");
            Check(effect->SetValue(1, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_VECTOR4, (byte*)&color, (uint)sizeof(DXGI_RGBA)), "SetValue(color)");
            ID2D1Image* blurred;
            effect->GetOutput(&blurred);
            var offset = new D2D_POINT_2F(origin.X + (float)(cast.Dx * unit * scale), origin.Y + (float)(cast.Dy * unit * scale));
            _dc->DrawImage(blurred, &offset, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR, D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
            blurred->Release();
        }
    }

    /// <summary>Un efecto de sombra por sombra del estilo: Direct2D evalúa el grafo al terminar, así que no se comparte entre sombras.</summary>
    private ID2D1Effect* ShadowEffectAt(int index)
    {
        if (_shadows[index] == 0)
        {
            Guid clsid = ShadowEffect;
            ID2D1Effect* created;
            Check(_dc->CreateEffect(&clsid, &created), "CreateEffect(sombra)");
            _shadows[index] = (nint)created;
        }

        return (ID2D1Effect*)_shadows[index];
    }

    private void DetachShadows()
    {
        foreach (nint effect in _shadows)
        {
            if (effect != 0)
            {
                ((ID2D1Effect*)effect)->SetInput(0, null, false);
            }
        }
    }

    private void ReleaseShadows()
    {
        for (int i = 0; i < _shadows.Length; i++)
        {
            if (_shadows[i] != 0)
            {
                ((IUnknown*)_shadows[i])->Release();
                _shadows[i] = 0;
            }
        }
    }

    /// <summary>El texto armado: el layout de DirectWrite, su caja (el origen del título), sus medidas y el texto con las mayúsculas aplicadas.</summary>
    private sealed class LaidOut(IDWriteTextLayout* layout, float width, float height, DWRITE_TEXT_METRICS metrics, string drawn, float size) : IDisposable
    {
        private IDWriteTextLayout* _layout = layout;

        public IDWriteTextLayout* Layout => _layout;

        public float Width { get; } = width;

        public float Height { get; } = height;

        public DWRITE_TEXT_METRICS Metrics { get; } = metrics;

        public string Drawn { get; } = drawn;

        public float Size { get; } = size;

        public void Dispose() => Com.Release(ref _layout);
    }

    /// <summary>El bloque medido: su centro, la mitad del rectángulo que cubre todo, el fondo y la caja del texto.</summary>
    private readonly record struct Shape(Vector2 Center, float HalfWidth, float HalfHeight, Rect? Background, Rect Box);
}
