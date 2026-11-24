using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using Rodaje.Text;
using SkiaSharp;
using Rect = Rodaje.Render.Text.TitleRect;

namespace Rodaje.Render.Text;

public sealed partial class SkiaTitleRasterizer
{
    private static TitleUnit[] Segment(GeneratedContent content, TitleLayout layout, TextUnit granularity)
    {
        TitleUnit[] units = [.. TitleUnits.Segment(layout, granularity).Select(box => new TitleUnit
        {
            Start = box.Start, End = box.End, Line = box.Line, Word = box.Word, Spoken = -1,
            InkLeft = box.Left, InkRight = box.Right, Top = box.Top, Bottom = box.Bottom,
            Center = new Vector2((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2),
            Placed = new Vector2((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2),
        })];
        for (int u = 0; u < units.Length; u++)
        {
            for (int w = 0; w < content.Words.Count; w++)
            {
                (int start, int length) = content.Words[w];
                if (start >= 0 && units[u].Start < start + length && units[u].End > start)
                {
                    units[u].Spoken = w;
                    break;
                }
            }
        }
        return units;
    }

    private static void Arc(TextStyle style, TitleLayout text, TitleUnit[] units)
    {
        if (style.Path is not { } path || Math.Abs(path.Bend) < 0.5 || units.Length == 0)
        {
            return;
        }
        float radius = (float)(Math.Max(1, text.TextWidth) / (path.Bend * Math.PI / 180));
        float first = text.Lines[0].Baseline - text.Height / 2;
        var center = new Vector2(0, first + radius);
        for (int u = 0; u < units.Length; u++)
        {
            ref TitleUnit unit = ref units[u];
            TitleLine line = text.Lines[unit.Line];
            float baseline = line.Top + line.Baseline - text.Height / 2;
            float lineRadius = radius - (baseline - first);
            if (Math.Abs(lineRadius) < 1)
            {
                continue;
            }
            float angle = unit.Center.X / lineRadius;
            float r = lineRadius + baseline - unit.Center.Y;
            unit.Placed = center + r * new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
            unit.Angle = angle;
        }
    }

    private static void RenderAtlas(Drawing drawing, TitleCell[] cells, TitleUnit[] units,
        SKBitmap bitmap, float scale, Vector2 offset)
    {
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        foreach (TitleCell cell in cells)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(cell.AtlasX, cell.AtlasY, cell.AtlasX + cell.AtlasWidth, cell.AtlasY + cell.AtlasHeight));
            canvas.SetMatrix(Matrix(CellTransform(cell, offset, scale)));
            TitleUnit unit = cell.Unit < 0 ? default : units[cell.Unit];
            switch (cell.Kind)
            {
                case TitleCellKind.Background:
                    drawing.Background(canvas);
                    break;
                case TitleCellKind.Shadow:
                    drawing.Shadows(canvas, unit.Start, unit.End);
                    break;
                case TitleCellKind.Stroke:
                    drawing.Strokes(canvas, unit.Start, unit.End);
                    break;
                default:
                    drawing.Fill(canvas, unit.Start, unit.End, cell.Kind == TitleCellKind.Highlight);
                    break;
            }
            canvas.Restore();
        }
    }

    private const int CellPadding = 16;

    private const int AtlasMipLevels = 5;

    private TitleTexture DrawUnits(GeneratedContent content, TitleLayout text, Drawing brushes)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        TextUnit granularity = Granularity(content);
        TitleUnit[] units = Segment(content, text, granularity);
        Arc(style, text, units);
        float strokeReach = StrokeReach(style, unit);
        var offset = new Vector2(text.Width / 2, text.Height / 2);

        var cells = new List<TitleCell>();
        if (style.Background is not null && brushes.BackgroundBox is { } background)
        {
            cells.Add(new TitleCell { Unit = -1, Kind = TitleCellKind.Background, Rest = background.Offset(-offset.X, -offset.Y) });
        }

        var fills = new Rect[units.Length];
        for (int u = 0; u < units.Length; u++)
        {
            // Margen para lo que la tinta sale de la caja del grupo (cursivas, acentos, colas).
            float lineHeight = units[u].Bottom - units[u].Top;
            fills[u] = new Rect(units[u].InkLeft, units[u].Top, units[u].InkRight, units[u].Bottom).Inflate((0.25f * lineHeight) + 1, (0.15f * lineHeight) + 1);
        }

        bool shadows = style.AllShadows().Any();
        for (int u = 0; u < units.Length && shadows; u++)
        {
            Rect body = fills[u].Inflate(strokeReach + 1);
            Rect cast = default;
            foreach (StyleShadow shadow in style.AllShadows())
            {
                cast = cast.Union(Cast(body, shadow, unit));
            }

            cells.Add(new TitleCell { Unit = u, Kind = TitleCellKind.Shadow, Rest = cast });
        }

        for (int u = 0; u < units.Length && brushes.HasStrokes; u++)
        {
            cells.Add(new TitleCell { Unit = u, Kind = TitleCellKind.Stroke, Rest = fills[u].Inflate(strokeReach + 1) });
        }

        for (int u = 0; u < units.Length; u++)
        {
            cells.Add(new TitleCell { Unit = u, Kind = TitleCellKind.Fill, Rest = fills[u] });
        }

        bool karaoke = style.Highlight is not null && content.Words.Count > 0;
        for (int u = 0; u < units.Length && karaoke; u++)
        {
            cells.Add(new TitleCell { Unit = u, Kind = TitleCellKind.Highlight, Rest = fills[u] });
        }

        TitleCell[] packed = [.. cells];
        int MaxTextureSide = Math.Min(16384, _gpu.Caps.MaxTextureSize);
        double scale = content.RasterScale;
        bool downscaled = false;
        (int width, int height) = Pack(packed, scale);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            long bytes = (long)width * height * 4 * 4 / 3;
            if (bytes <= MaxTitleBytes && width <= MaxTextureSide && height <= MaxTextureSide)
            {
                break;
            }

            double fit = Math.Min(Math.Sqrt((double)MaxTitleBytes / bytes), Math.Min((double)MaxTextureSide / width, (double)MaxTextureSide / height));
            scale *= Math.Min(fit, 1) * 0.95;
            (width, height) = Pack(packed, scale);
            downscaled = true;
        }

        if ((long)width * height * 4 * 4 / 3 > MaxTitleBytes || width > MaxTextureSide || height > MaxTextureSide)
        {
            throw new ArgumentException("El atlas no cabe en el tope de memoria.");
        }

        for (int i = 0; i < packed.Length; i++)
        {
            ref TitleCell cell = ref packed[i];
            float x0 = cell.AtlasX + CellPadding;
            float y0 = cell.AtlasY + CellPadding;
            cell.Uv = new Vector4(x0 / width, y0 / height, (x0 + (cell.Rest.Width * (float)scale)) / width, (y0 + (cell.Rest.Height * (float)scale)) / height);
        }

        using var bitmap = Bitmap(width, height);
        RenderAtlas(brushes, packed, units, bitmap, (float)scale, offset);
        RenderTarget? atlas = Upload(bitmap, AtlasMipLevels);
        try
        {
            Rect bounds = default;
            foreach (TitleCell cell in packed)
            {
                bounds = bounds.Union(Placed(cell, cell.Unit < 0 ? null : units[cell.Unit]));
            }

            float halfWidth = Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right));
            float halfHeight = Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom));
            var set = new TitleUnitSet(packed, units, granularity, style.In, style.Out, unit, style.Highlight, AtlasMipLevels);
            var texture = new TitleTexture(atlas, 2 * halfWidth, 2 * halfHeight, scale, text.FontMissing, downscaled) { Units = set };
            atlas = null;
            return texture;
        }
        finally
        {
            atlas?.Dispose();
        }
    }

    /// <summary>La unidad más fina que hace falta: la de cada animación por unidades, la letra en un arco y la palabra en karaoke.</summary>
    private static TextUnit Granularity(GeneratedContent content)
    {
        TextStyle style = content.Style;
        TextUnit finest = TextUnit.Block;
        foreach (StyleAnimation? animation in (ReadOnlySpan<StyleAnimation?>)[style.In, style.Out])
        {
            if (animation is { PerUnit: true } a && a.Unit > finest)
            {
                finest = a.Unit;
            }
        }

        if (style.Path is not null)
        {
            finest = TextUnit.Letter;
        }

        if (style.Highlight is not null && content.Words.Count > 0 && finest < TextUnit.Word)
        {
            finest = TextUnit.Word;
        }

        return finest;
    }

    private static Rect Placed(TitleCell cell, TitleUnit? unit)
    {
        if (unit is not { } u)
        {
            return cell.Rest;
        }

        Matrix3x2 m = Matrix3x2.CreateTranslation(-u.Center) * Matrix3x2.CreateRotation(u.Angle) * Matrix3x2.CreateTranslation(u.Placed);
        Rect r = cell.Rest;
        Rect result = default;
        foreach (Vector2 corner in (ReadOnlySpan<Vector2>)[new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom)])
        {
            Vector2 p = Vector2.Transform(corner, m);
            result = result.Union(new Rect(p.X, p.Y, p.X + 0.001f, p.Y + 0.001f));
        }

        return result;
    }

    /// <summary>Acomoda las celdas en estantes (de la más alta a la más baja), alineadas y con margen. Devuelve el tamaño del atlas.</summary>
    private static (int Width, int Height) Pack(TitleCell[] cells, double scale)
    {
        long area = 0;
        int widest = 1;
        for (int i = 0; i < cells.Length; i++)
        {
            ref TitleCell cell = ref cells[i];
            cell.AtlasWidth = Align((int)Math.Ceiling(Math.Max(1, cell.Rest.Width) * scale) + (2 * CellPadding));
            cell.AtlasHeight = Align((int)Math.Ceiling(Math.Max(1, cell.Rest.Height) * scale) + (2 * CellPadding));
            area += (long)cell.AtlasWidth * cell.AtlasHeight;
            widest = Math.Max(widest, cell.AtlasWidth);
        }

        int limit = Math.Max(widest, Align((int)Math.Ceiling(Math.Sqrt(area * 1.15))));
        int[] order = [.. Enumerable.Range(0, cells.Length).OrderByDescending(i => cells[i].AtlasHeight)];
        int x = 0;
        int y = 0;
        int row = 0;
        int width = 1;
        foreach (int i in order)
        {
            ref TitleCell cell = ref cells[i];
            if (x + cell.AtlasWidth > limit)
            {
                x = 0;
                y += row;
                row = 0;
            }

            cell.AtlasX = x;
            cell.AtlasY = y;
            x += cell.AtlasWidth;
            row = Math.Max(row, cell.AtlasHeight);
            width = Math.Max(width, x);
        }

        return (Math.Max(CellPadding, width), Math.Max(CellPadding, y + row));

        static int Align(int value) => (value + CellPadding - 1) / CellPadding * CellPadding;
    }

    private static Matrix3x2 CellTransform(TitleCell cell, Vector2 offset, float scale) =>
        Matrix3x2.CreateTranslation(-offset.X - cell.Rest.Left, -offset.Y - cell.Rest.Top)
        * Matrix3x2.CreateScale(scale)
        * Matrix3x2.CreateTranslation(cell.AtlasX + CellPadding, cell.AtlasY + CellPadding);    private static float StrokeReach(TextStyle style, double unit)
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


}
