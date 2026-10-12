using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Text;

/// <summary>
/// Títulos por unidades: cada línea, palabra o letra en su celda de un atlas con mipmaps (sombra, borde, relleno y, en
/// karaoke, relleno resaltado), para que el compositor las mueva una por una sin volver a rasterizar.
/// </summary>
public sealed unsafe partial class TitleRasterizer
{
    /// <summary>Margen entre celdas y alineación, en texeles: el mip 4 (16 texeles) no mezcla celdas vecinas.</summary>
    private const int CellPadding = 16;

    private const int AtlasMipLevels = 5;

    private TitleTexture? DrawUnits(GeneratedContent content, LaidOut text, TitleBrushes brushes, ResolvedFont font)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        TextUnit granularity = Granularity(content);
        TitleUnit[] units = Segment(content, text, granularity);
        Arc(style, text, units);
        Shape shape = Measure(content, text);
        float strokeReach = StrokeReach(style, unit);
        var offset = new Vector2(text.Width / 2, text.Height / 2);

        var cells = new List<TitleCell>();
        if (style.Background is not null && shape.Background is { } background)
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

        for (int i = 0; i < packed.Length; i++)
        {
            ref TitleCell cell = ref packed[i];
            float x0 = cell.AtlasX + CellPadding;
            float y0 = cell.AtlasY + CellPadding;
            cell.Uv = new Vector4(x0 / width, y0 / height, (x0 + (cell.Rest.Width * (float)scale)) / width, (y0 + (cell.Rest.Height * (float)scale)) / height);
        }

        RenderTarget? atlas = CreateTarget(width, height, AtlasMipLevels);
        try
        {
            RenderAtlas(content, text, brushes, packed, units, atlas, (float)scale, offset);
            Rect bounds = default;
            foreach (TitleCell cell in packed)
            {
                bounds = bounds.Union(Placed(cell, cell.Unit < 0 ? null : units[cell.Unit]));
            }

            float halfWidth = Math.Max(Math.Abs(bounds.Left), Math.Abs(bounds.Right));
            float halfHeight = Math.Max(Math.Abs(bounds.Top), Math.Abs(bounds.Bottom));
            var set = new TitleUnitSet(packed, units, granularity, style.In, style.Out, unit, style.Highlight, AtlasMipLevels);
            var texture = new TitleTexture(atlas, 2 * halfWidth, 2 * halfHeight, scale, font.Missing, downscaled) { Units = set };
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

    /// <summary>Las unidades del texto (sin espacios), con su rectángulo de los grupos de DirectWrite, palabra, línea y palabra dicha.</summary>
    private static TitleUnit[] Segment(GeneratedContent content, LaidOut text, TextUnit granularity)
    {
        IDWriteTextLayout* layout = text.Layout;
        uint clusterCount;
        layout->GetClusterMetrics(null, 0, &clusterCount);
        var clusters = new DWRITE_CLUSTER_METRICS[clusterCount];
        fixed (DWRITE_CLUSTER_METRICS* c = clusters)
        {
            Check(layout->GetClusterMetrics(c, clusterCount, &clusterCount), "GetClusterMetrics");
        }

        uint lineCount;
        layout->GetLineMetrics(null, 0, &lineCount);
        var lines = new DWRITE_LINE_METRICS[lineCount];
        fixed (DWRITE_LINE_METRICS* l = lines)
        {
            Check(layout->GetLineMetrics(l, lineCount, &lineCount), "GetLineMetrics");
        }

        var lineEnds = new int[lines.Length];
        int sum = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            sum += (int)lines[i].length;
            lineEnds[i] = sum;
        }

        var offset = new Vector2(text.Width / 2, text.Height / 2);
        var units = new List<TitleUnit>();
        int position = 0;
        int line = 0;
        int word = -1;
        bool previousBlank = true;
        int previousLine = -1;
        foreach (DWRITE_CLUSTER_METRICS cluster in clusters)
        {
            int length = cluster.length;
            while (line + 1 < lineEnds.Length && position >= lineEnds[line])
            {
                line++;
            }

            bool blank = cluster.isWhitespace != 0 || cluster.isNewline != 0;
            if (!blank)
            {
                if (previousBlank || line != previousLine)
                {
                    word++;
                }

                float x;
                float y;
                DWRITE_HIT_TEST_METRICS hit;
                Check(layout->HitTestTextPosition((uint)position, false, &x, &y, &hit), "HitTestTextPosition");
                var rect = new Rect(hit.left, hit.top, hit.left + hit.width, hit.top + hit.height).Offset(-offset.X, -offset.Y);
                int key = granularity switch
                {
                    TextUnit.Letter => -1,
                    TextUnit.Word => word,
                    TextUnit.Line => line,
                    _ => 0,
                };
                bool joins = key >= 0 && units.Count > 0 && (granularity switch
                {
                    TextUnit.Word => units[^1].Word == word,
                    TextUnit.Line => units[^1].Line == line,
                    _ => true,
                });
                if (joins)
                {
                    TitleUnit last = units[^1];
                    last.End = position + length;
                    last.InkLeft = Math.Min(last.InkLeft, rect.Left);
                    last.InkRight = Math.Max(last.InkRight, rect.Right);
                    last.Top = Math.Min(last.Top, rect.Top);
                    last.Bottom = Math.Max(last.Bottom, rect.Bottom);
                    units[^1] = last;
                }
                else
                {
                    units.Add(new TitleUnit
                    {
                        Start = position,
                        End = position + length,
                        Word = word,
                        Line = line,
                        Spoken = -1,
                        InkLeft = rect.Left,
                        InkRight = rect.Right,
                        Center = rect.Center,
                        Top = rect.Top,
                        Bottom = rect.Bottom,
                    });
                }

                previousLine = line;
            }

            previousBlank = blank;
            position += length;
        }

        TitleUnit[] result = [.. units];
        for (int u = 0; u < result.Length; u++)
        {
            result[u].Center = new Vector2((result[u].InkLeft + result[u].InkRight) / 2, (result[u].Top + result[u].Bottom) / 2);
            result[u].Placed = result[u].Center;
            for (int w = 0; w < content.Words.Count; w++)
            {
                (int start, int length) = content.Words[w];
                if (start >= 0 && result[u].Start < start + length && result[u].End > start)
                {
                    result[u].Spoken = w;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Texto en arco: cada letra va al círculo de su línea (las de abajo, más adentro) girada con él. El arco abarca
    /// <see cref="StylePath.Bend"/> grados a lo ancho del texto; positivo, el centro del círculo queda abajo.
    /// </summary>
    private static void Arc(TextStyle style, LaidOut text, TitleUnit[] units)
    {
        if (style.Path is not { } path || !(Math.Abs(path.Bend) >= 0.5) || units.Length == 0)
        {
            return;
        }

        IDWriteTextLayout* layout = text.Layout;
        uint lineCount;
        layout->GetLineMetrics(null, 0, &lineCount);
        var lines = new DWRITE_LINE_METRICS[lineCount];
        fixed (DWRITE_LINE_METRICS* l = lines)
        {
            Check(layout->GetLineMetrics(l, lineCount, &lineCount), "GetLineMetrics");
        }

        var baselines = new float[lines.Length];
        float top = -text.Height / 2;
        for (int i = 0; i < lines.Length; i++)
        {
            baselines[i] = top + lines[i].baseline;
            top += lines[i].height;
        }

        double radians = path.Bend * Math.PI / 180;
        float width = Math.Max(1, text.Metrics.width);
        float radius = (float)(width / radians);
        float first = baselines.Length > 0 ? baselines[0] : 0;
        var center = new Vector2(0, first + radius);
        for (int u = 0; u < units.Length; u++)
        {
            ref TitleUnit unit = ref units[u];
            float baseline = baselines[Math.Clamp(unit.Line, 0, baselines.Length - 1)];
            float lineRadius = radius - (baseline - first);
            if (Math.Abs(lineRadius) < 1)
            {
                continue;
            }

            float above = baseline - unit.Center.Y;
            float angle = unit.Center.X / lineRadius;
            float r = lineRadius + above;
            unit.Placed = center + (r * new Vector2(MathF.Sin(angle), -MathF.Cos(angle)));
            unit.Angle = angle;
        }
    }

    /// <summary>El rectángulo de una celda en el título en reposo, con el arco aplicado (para medir el título).</summary>
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

        return (width, Math.Max(1, y + row));

        static int Align(int value) => (value + CellPadding - 1) / CellPadding * CellPadding;
    }

    /// <summary>
    /// Dibuja el atlas: los cuerpos con sombra en una capa aparte (cada uno en su celda de sombra), las sombras desde esa
    /// capa, el fondo, y por cada unidad su borde, su relleno y su relleno resaltado. Después baja el mip 0 al atlas y genera
    /// los demás.
    /// </summary>
    private void RenderAtlas(GeneratedContent content, LaidOut text, TitleBrushes brushes, TitleCell[] cells, TitleUnit[] units, RenderTarget atlas,
        float scale, Vector2 offset)
    {
        TextStyle style = content.Style;
        double unitScale = content.UnitScale;
        RenderTarget? canvas = null;
        ID2D1Bitmap1* bitmap = null;
        ID2D1Bitmap1* layer = null;
        ID2D1SolidColorBrush* background = null;
        List<(int TextPosition, nint Geometry)> glyphs = [];
        var outlines = new nint[units.Length];
        try
        {
            canvas = CreateTarget(atlas.Width, atlas.Height);
            bitmap = TargetBitmap(canvas);
            if (brushes.HasStrokes || style.AllShadows().Any())
            {
                glyphs = GlyphOutlines.CollectGlyphs(text.Layout, (ID2D1Factory*)_d2d);
                for (int u = 0; u < units.Length; u++)
                {
                    List<nint> mine = [.. glyphs.Where(g => g.TextPosition >= units[u].Start && g.TextPosition < units[u].End).Select(g => g.Geometry)];
                    outlines[u] = (nint)Group(mine);
                }
            }

            if (style.Background is { } bg)
            {
                background = brushes.Solid(bg.Color);
            }

            _gpu.LockContext();
            try
            {
                if (style.AllShadows().Any())
                {
                    layer = LayerBitmap(canvas.Width, canvas.Height);
                    _dc->SetTarget((ID2D1Image*)layer);
                    _dc->BeginDraw();
                    var none = default(DXGI_RGBA);
                    _dc->Clear(&none);
                    foreach (TitleCell cell in cells)
                    {
                        if (cell.Kind == TitleCellKind.Shadow)
                        {
                            TitleUnit unit = units[cell.Unit];
                            D2D_MATRIX_3X2_F world = Matrix(CellTransform(cell, offset, scale));
                            brushes.Only(text.Layout, unit.Start, unit.End, highlighted: false);
                            DrawBody(text, brushes, (ID2D1GeometryGroup*)outlines[cell.Unit], &world);
                        }
                    }

                    Check(_dc->EndDraw(null, null), "EndDraw(sombras del título)");
                }

                _dc->SetTarget((ID2D1Image*)bitmap);
                _dc->BeginDraw();
                var clear = default(DXGI_RGBA);
                _dc->Clear(&clear);
                if (layer != null)
                {
                    DrawShadows(style, unitScale, layer, Vector2.Zero, scale);
                }

                foreach (TitleCell cell in cells)
                {
                    if (cell.Kind == TitleCellKind.Shadow)
                    {
                        continue;
                    }

                    D2D_MATRIX_3X2_F world = Matrix(CellTransform(cell, offset, scale));
                    D2D_MATRIX_3X2_F identity = Matrix(Matrix3x2.Identity);
                    _dc->SetTransform(&identity);
                    var clip = new D2D_RECT_F(cell.AtlasX, cell.AtlasY, cell.AtlasX + cell.AtlasWidth, cell.AtlasY + cell.AtlasHeight);
                    _dc->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
                    switch (cell.Kind)
                    {
                        case TitleCellKind.Background when background != null:
                            FillBackground(cell.Rest.Offset(offset.X, offset.Y), style.Background!, unitScale, background, &world);
                            break;
                        case TitleCellKind.Stroke when outlines[cell.Unit] != 0:
                            _dc->SetTransform(&world);
                            foreach ((nint brush, nint strokeStyle, float width) in brushes.Strokes)
                            {
                                _dc->DrawGeometry((ID2D1Geometry*)outlines[cell.Unit], (ID2D1Brush*)brush, 2 * width, (ID2D1StrokeStyle*)strokeStyle);
                            }

                            break;
                        case TitleCellKind.Fill or TitleCellKind.Highlight:
                            brushes.Only(text.Layout, units[cell.Unit].Start, units[cell.Unit].End, cell.Kind == TitleCellKind.Highlight);
                            _dc->SetTransform(&world);
                            _dc->DrawTextLayout(new D2D_POINT_2F(0, 0), text.Layout, brushes.Fill,
                                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NO_SNAP | D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
                            break;
                    }

                    _dc->PopAxisAlignedClip();
                }

                HRESULT hr = _dc->EndDraw(null, null);
                DetachShadows();
                _dc->SetTarget(null);
                var context = (ID3D11DeviceContext*)_gpu.NativeContext;
                context->ClearState();
                Check(hr, "EndDraw(atlas del título)");
                context->CopySubresourceRegion((ID3D11Resource*)atlas.Texture, 0, 0, 0, 0, (ID3D11Resource*)canvas.Texture, 0, null);
                context->GenerateMips(atlas.Srv);
            }
            finally
            {
                _gpu.UnlockContext();
            }
        }
        finally
        {
            foreach (nint outline in outlines)
            {
                if (outline != 0)
                {
                    ((IUnknown*)outline)->Release();
                }
            }

            GlyphOutlines.Release(glyphs);
            Com.Release(ref background);
            Com.Release(ref layer);
            Com.Release(ref bitmap);
            canvas?.Dispose();
        }
    }

    /// <summary>Del espacio del layout a los texeles de la celda: su rectángulo en reposo cae en su lugar del atlas.</summary>
    private static Matrix3x2 CellTransform(TitleCell cell, Vector2 offset, float scale) =>
        Matrix3x2.CreateTranslation(-offset.X - cell.Rest.Left, -offset.Y - cell.Rest.Top)
        * Matrix3x2.CreateScale(scale)
        * Matrix3x2.CreateTranslation(cell.AtlasX + CellPadding, cell.AtlasY + CellPadding);
}
