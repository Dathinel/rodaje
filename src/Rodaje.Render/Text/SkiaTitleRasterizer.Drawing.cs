using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Text;
using SkiaSharp;
using Rect = Rodaje.Render.Text.TitleRect;

namespace Rodaje.Render.Text;

public sealed partial class SkiaTitleRasterizer
{
    private TitleTexture? Draw(GeneratedContent content)
    {
        float size = (float)(content.Style.Size * content.UnitScale);
        if (content.Kind != GeneratorKinds.Text || _fonts is null || !(size > 0) || !float.IsFinite(size))
        {
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        TitleLayout layout = TitleLayout.Create(content, _fonts);
        using var drawing = new Drawing(content, layout);
        TitleTexture result = content.UsesUnits ? DrawUnits(content, layout, drawing) : DrawBlock(content, layout, drawing);
        Rasterized++;
        LastRasterMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    private TitleTexture DrawBlock(GeneratedContent content, TitleLayout layout, Drawing drawing)
    {
        Rect bounds = drawing.Bounds;
        Vector2 center = new(layout.Width / 2, layout.Height / 2);
        float halfWidth = Math.Max(Math.Abs(bounds.Left - center.X), Math.Abs(bounds.Right - center.X));
        float halfHeight = Math.Max(Math.Abs(bounds.Top - center.Y), Math.Abs(bounds.Bottom - center.Y));
        double scale = content.RasterScale;
        (int Width, int Height) Size() => (Math.Max(1, (int)Math.Ceiling(2 * halfWidth * scale) + 2),
            Math.Max(1, (int)Math.Ceiling(2 * halfHeight * scale) + 2));
        (int width, int height) = Size();
        bool downscaled = false;
        int maxSide = Math.Min(16384, _gpu.Caps.MaxTextureSize);
        while ((long)width * height * 4 > MaxTitleBytes || width > maxSide || height > maxSide)
        {
            double fit = Math.Min(Math.Sqrt((double)MaxTitleBytes / ((long)width * height * 4)),
                Math.Min((double)maxSide / width, (double)maxSide / height));
            scale *= fit * 0.98;
            if (scale < 1e-8 || MaxTitleBytes < 16)
            {
                throw new ArgumentException("El título no cabe en el tope de memoria.");
            }
            (width, height) = Size();
            downscaled = true;
        }

        using var bitmap = Bitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(Matrix(Matrix3x2.CreateScale((float)scale)
                * Matrix3x2.CreateTranslation(width / 2f - center.X * (float)scale, height / 2f - center.Y * (float)scale)));
            drawing.Background(canvas);
            drawing.Shadows(canvas, 0, layout.Drawn.Length);
            drawing.Body(canvas, 0, layout.Drawn.Length);
        }

        return new TitleTexture(Upload(bitmap), width / scale, height / scale, scale, layout.FontMissing, downscaled);
    }

    private unsafe RenderTarget Upload(SKBitmap bitmap, int mipLevels = 1)
    {
        GpuTextureUsage usage = GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget
            | (mipLevels > 1 ? GpuTextureUsage.GenerateMips : 0);
        var pixels = new ReadOnlySpan<byte>((void*)bitmap.GetPixels(), bitmap.ByteCount);
        var desc = new GpuTextureDesc(bitmap.Width, bitmap.Height, GpuFormat.Bgra8Unorm, usage, mipLevels);
        // El contenido inicial solo va con un mip (D3D11): con más, se escribe el mip 0 y se generan los demás.
        GpuTexture texture = mipLevels > 1 ? _gpu.CreateTexture(desc) : _gpu.CreateTexture(desc, pixels);
        try
        {
            if (mipLevels > 1)
            {
                using IGpuFrame frame = _gpu.BeginFrame();
                frame.Write(texture, pixels, bitmap.RowBytes);
                frame.GenerateMips(texture);
            }

            long bytes = (long)bitmap.Width * bitmap.Height * 4;
            return new RenderTarget(texture, mipLevels > 1 ? bytes * 4 / 3 : bytes);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private static SKBitmap Bitmap(int width, int height) => new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static SKMatrix Matrix(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    private static SKRect Rectangle(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static SKColor Color(string? color)
    {
        if (color is not { Length: 7 or 9 } || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return SKColors.White;
        }
        if (color.Length == 7)
        {
            value = (value << 8) | 255;
        }
        return new SKColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private sealed class Drawing : IDisposable
    {
        private readonly GeneratedContent _content;
        private readonly List<(int Start, SKPath Path)> _paths = [];
        private readonly SKPaint _fill = new() { IsAntialias = true };
        private readonly SKShader? _gradient;
        private readonly Rect _box;
        public Rect Bounds { get; }
        public Rect? BackgroundBox { get; }
        public bool HasStrokes => _content.Style.AllStrokes().Any(s => s.Width > 0 && double.IsFinite(s.Width));

        public Drawing(GeneratedContent content, TitleLayout layout)
        {
            _content = content;
            _box = new Rect(layout.Lines.Min(l => l.Left), 0, layout.Lines.Min(l => l.Left) + layout.TextWidth, layout.Height);
            var fonts = new Dictionary<ShapingFont, (SKTypeface Face, SKFont Font)>();
            try
            {
                foreach (TitleGlyph glyph in layout.Glyphs)
                {
                    if (!fonts.TryGetValue(glyph.Font, out var font))
                    {
                        SKTypeface face = SKTypeface.FromFile(glyph.Font.Path, glyph.Font.Index)
                            ?? throw new ArgumentException("Skia no pudo abrir la fuente: " + glyph.Font.Path);
                        font = (face, new SKFont(face, layout.Size) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true });
                        fonts.Add(glyph.Font, font);
                    }
                    font.Font.Size = glyph.Size;
                    using SKPath? path = font.Font.GetGlyphPath(checked((ushort)glyph.Index));
                    if (path is not null)
                    {
                        using var placed = new SKPathBuilder();
                        placed.AddPath(path, glyph.X, glyph.Y);
                        _paths.Add((glyph.Start, placed.Detach()));
                    }
                }

                foreach (TitleCluster cluster in layout.Clusters)
                {
                    if (!cluster.IsNewline && TextSpans.At(content.Spans, cluster.Start)?.Underline == true
                        && fonts.TryGetValue(cluster.Font, out var font))
                    {
                        font.Font.Size = cluster.Size;
                        SKFontMetrics metrics = font.Font.Metrics;
                        TitleLine line = layout.Lines[cluster.Line];
                        float y = line.Top + line.Baseline + (metrics.UnderlinePosition ?? cluster.Size * 0.1f);
                        using var underline = new SKPathBuilder();
                        underline.AddRect(new SKRect(cluster.X, y, cluster.X + cluster.Advance,
                            y + Math.Max(0.5f, metrics.UnderlineThickness ?? cluster.Size * 0.05f)));
                        _paths.Add((cluster.Start, underline.Detach()));
                    }
                }

                Rect ink = default;
                foreach (var glyph in _paths)
                {
                    SKRect r = glyph.Path.TightBounds;
                    ink = ink.Union(new Rect(r.Left, r.Top, r.Right, r.Bottom));
                }
                if (ink.IsEmpty)
                {
                    ink = _box;
                }
                float stroke = StrokeReach(content.Style, content.UnitScale);
                Rect outlined = stroke > 0 ? ink.Inflate(stroke + 1) : ink;
                Rect bounds = _box.Union(outlined);
                if (content.Style.Background is { } bg)
                {
                    BackgroundBox = _box.Inflate((float)(Math.Max(0, bg.Padding.ElementAtOrDefault(0)) * content.UnitScale),
                        (float)(Math.Max(0, bg.Padding.ElementAtOrDefault(1)) * content.UnitScale));
                    bounds = bounds.Union(BackgroundBox.Value);
                }
                foreach (StyleShadow shadow in content.Style.AllShadows())
                {
                    bounds = bounds.Union(Cast(outlined, shadow, content.UnitScale));
                }
                Bounds = bounds;
                _fill.Color = Color(content.Style.Fill.Color);
                if (content.Style.Fill.Gradient is { Stops.Count: >= 2 } gradient)
                {
                    _gradient = Gradient(gradient);
                    _fill.Color = SKColors.White;
                    _fill.Shader = _gradient;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                foreach (var font in fonts.Values)
                {
                    font.Font.Dispose();
                    font.Face.Dispose();
                }
            }
        }

        public void Background(SKCanvas canvas)
        {
            if (_content.Style.Background is { } bg && BackgroundBox is { } box)
            {
                using var paint = new SKPaint { IsAntialias = true, Color = Color(bg.Color) };
                float radius = (float)(Math.Max(0, bg.Radius) * _content.UnitScale);
                canvas.DrawRoundRect(Rectangle(box), radius, radius, paint);
            }
        }

        public void Body(SKCanvas canvas, int start, int end)
        {
            Strokes(canvas, start, end);
            Fill(canvas, start, end, false);
        }

        public void Strokes(SKCanvas canvas, int start, int end)
        {
            using var builder = new SKPathBuilder();
            foreach (var glyph in _paths.Where(g => g.Start >= start && g.Start < end))
            {
                builder.AddPath(glyph.Path);
            }
            using SKPath path = builder.Detach();
            foreach (StyleStroke stroke in _content.Style.AllStrokes().Where(s => s.Width > 0 && double.IsFinite(s.Width)).OrderByDescending(s => s.Width))
            {
                using var paint = new SKPaint
                {
                    IsAntialias = true, Color = Color(stroke.Color), Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)(2 * stroke.Width * _content.UnitScale), StrokeMiter = 2, StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = stroke.Join switch { StrokeJoin.Miter => SKStrokeJoin.Miter, StrokeJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Round },
                };
                canvas.DrawPath(path, paint);
            }
        }

        public void Fill(SKCanvas canvas, int start, int end, bool highlighted)
        {
            using var solid = new SKPaint { IsAntialias = true };
            foreach (var glyph in _paths.Where(g => g.Start >= start && g.Start < end))
            {
                string? color = null;
                foreach (TextSpan span in _content.Spans)
                {
                    if (span.Color is not null && glyph.Start >= span.Start && glyph.Start < span.End)
                    {
                        color = span.Color;
                    }
                }
                if (_content.Style.Highlight is { } h && (highlighted || (!_content.UsesUnits && glyph.Start >= _content.HighlightStart
                    && glyph.Start < _content.HighlightStart + _content.HighlightLength)))
                {
                    color = h.Color;
                }
                solid.Color = Color(color);
                canvas.DrawPath(glyph.Path, color is null ? _fill : solid);
            }
        }

        public void Shadows(SKCanvas canvas, int start, int end)
        {
            foreach (StyleShadow shadow in _content.Style.AllShadows())
            {
                float sigma = Sigma(shadow, _content.UnitScale);
                using SKImageFilter filter = SKImageFilter.CreateDropShadowOnly((float)(shadow.Dx * _content.UnitScale),
                    (float)(shadow.Dy * _content.UnitScale), sigma, sigma, Color(shadow.Color));
                using var paint = new SKPaint { ImageFilter = filter };
                canvas.SaveLayer(paint);
                Body(canvas, start, end);
                canvas.Restore();
            }
        }

        private SKShader Gradient(StyleGradient gradient)
        {
            var stops = gradient.Stops.Take(StyleGradient.MaxStops).ToArray();
            SKColor[] colors = [.. stops.Select(s => Color(s.Color))];
            float[] positions = [.. stops.Select(s => (float)Math.Clamp(s.Position, 0, 1))];
            Vector2 center = _box.Center;
            if (gradient.Kind == GradientKind.Radial)
            {
                SKMatrix matrix = Matrix(Matrix3x2.CreateScale(Math.Max(1, _box.Width / 2), Math.Max(1, _box.Height / 2))
                    * Matrix3x2.CreateTranslation(center));
                return SKShader.CreateRadialGradient(SKPoint.Empty, 1, colors, positions, SKShaderTileMode.Clamp, matrix);
            }
            double radians = gradient.Angle * Math.PI / 180;
            Vector2 direction = new((float)Math.Cos(radians), (float)Math.Sin(radians));
            float half = (Math.Abs(direction.X) * _box.Width + Math.Abs(direction.Y) * _box.Height) / 2;
            Vector2 from = center - direction * half, to = center + direction * half;
            return SKShader.CreateLinearGradient(new SKPoint(from.X, from.Y), new SKPoint(to.X, to.Y), colors, positions, SKShaderTileMode.Clamp);
        }

        public void Dispose()
        {
            foreach (var glyph in _paths)
            {
                glyph.Path.Dispose();
            }
            _paths.Clear();
            _fill.Dispose();
            _gradient?.Dispose();
        }
    }
}
