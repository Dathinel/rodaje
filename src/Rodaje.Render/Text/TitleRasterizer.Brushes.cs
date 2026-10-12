using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Text;

public sealed unsafe partial class TitleRasterizer
{
    /// <summary>
    /// Los pinceles de un título: el relleno (color o degradado sobre la caja del texto), el color de cada tramo, el de la
    /// palabra resaltada, uno transparente para dibujar una sola unidad del layout y los bordes, del más ancho al más angosto.
    /// </summary>
    private sealed class TitleBrushes : IDisposable
    {
        private readonly TitleRasterizer _owner;
        private readonly GeneratedContent _content;
        private readonly List<nint> _owned = [];
        private readonly List<(int Start, int End, nint Brush)> _spans = [];
        private readonly List<(nint Brush, nint Style, float Width)> _strokes = [];

        public TitleBrushes(TitleRasterizer owner, GeneratedContent content, LaidOut text)
        {
            _owner = owner;
            _content = content;
            TextStyle style = content.Style;
            try
            {
                Fill = style.Fill.Gradient is { Stops.Count: >= 2 } gradient ? Gradient(gradient, text) : (ID2D1Brush*)Solid(style.Fill.Color);
                Own((nint)Fill);
                Transparent = Solid("#00000000");
                Own((nint)Transparent);
                Highlight = style.Highlight is { } h ? Solid(h.Color) : null;
                Own((nint)Highlight);
                foreach (TextSpan span in content.Spans)
                {
                    if (span.Color is { } color)
                    {
                        ID2D1SolidColorBrush* brush = Solid(color);
                        Own((nint)brush);
                        _spans.Add((span.Start, span.End, (nint)brush));
                    }
                }

                foreach (StyleStroke s in style.AllStrokes().Where(s => s.Width > 0 && double.IsFinite(s.Width)).OrderByDescending(s => s.Width))
                {
                    ID2D1SolidColorBrush* brush = Solid(s.Color);
                    Own((nint)brush);
                    ID2D1StrokeStyle* strokeStyle = StrokeStyle(s.Join);
                    Own((nint)strokeStyle);
                    _strokes.Add(((nint)brush, (nint)strokeStyle, (float)(s.Width * content.UnitScale)));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public ID2D1Brush* Fill { get; }

        public ID2D1SolidColorBrush* Transparent { get; }

        public ID2D1SolidColorBrush* Highlight { get; }

        public bool HasStrokes => _strokes.Count > 0;

        /// <summary>Los bordes en el orden en que se dibujan: pincel, estilo de trazo y ancho en píxeles de la secuencia.</summary>
        public IReadOnlyList<(nint Brush, nint Style, float Width)> Strokes => _strokes;

        /// <summary>Un pincel de un color; quien lo pide lo suelta.</summary>
        public ID2D1SolidColorBrush* Solid(string color)
        {
            DXGI_RGBA rgba = TitleColor.Parse(color);
            ID2D1SolidColorBrush* brush;
            Check(_owner._dc->CreateSolidColorBrush(&rgba, null, &brush), "CreateSolidColorBrush");
            return brush;
        }

        /// <summary>El bloque entero: los tramos con su color y, en karaoke por variantes, la palabra resaltada.</summary>
        public void Plain(IDWriteTextLayout* layout, int highlightStart, int highlightLength)
        {
            foreach ((int start, int end, nint brush) in _spans)
            {
                SetEffect(layout, start, end, (ID2D1Brush*)brush);
            }

            if (Highlight != null && highlightLength > 0)
            {
                // La palabra que suena con otro pincel; el borde y la sombra no cambian.
                SetEffect(layout, highlightStart, highlightStart + highlightLength, (ID2D1Brush*)Highlight);
            }
        }

        /// <summary>
        /// Solo <c>[start, end)</c> se ve (el resto, transparente, ocupa su lugar): para dibujar una unidad con la forma y el
        /// interletrado del texto entero. Con <paramref name="highlighted"/>, toda la unidad en el color resaltado.
        /// </summary>
        public void Only(IDWriteTextLayout* layout, int start, int end, bool highlighted)
        {
            SetEffect(layout, 0, _content.Text.Length, (ID2D1Brush*)Transparent);
            SetEffect(layout, start, end, highlighted && Highlight != null ? (ID2D1Brush*)Highlight : Fill);
            if (!highlighted)
            {
                foreach ((int spanStart, int spanEnd, nint brush) in _spans)
                {
                    int from = Math.Max(start, spanStart);
                    int to = Math.Min(end, spanEnd);
                    if (to > from)
                    {
                        SetEffect(layout, from, to, (ID2D1Brush*)brush);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (nint brush in _owned)
            {
                if (brush != 0)
                {
                    ((IUnknown*)brush)->Release();
                }
            }

            _owned.Clear();
        }

        private void Own(nint item) => _owned.Add(item);

        private static void SetEffect(IDWriteTextLayout* layout, int start, int end, ID2D1Brush* brush)
        {
            if (end > start)
            {
                Check(layout->SetDrawingEffect((IUnknown*)brush, Range(start, end - start)), "SetDrawingEffect");
            }
        }

        /// <summary>Degradado sobre la caja del texto: lineal a lo largo del ángulo (cubre la caja de punta a punta) o radial desde el centro.</summary>
        private ID2D1Brush* Gradient(StyleGradient gradient, LaidOut text)
        {
            ID2D1DeviceContext* dc = _owner._dc;
            DWRITE_TEXT_METRICS m = text.Metrics;
            var center = new Vector2(m.left + (m.width / 2), m.top + (m.height / 2));
            int count = Math.Min(gradient.Stops.Count, StyleGradient.MaxStops);
            D2D1_GRADIENT_STOP* stops = stackalloc D2D1_GRADIENT_STOP[count];
            for (int i = 0; i < count; i++)
            {
                stops[i] = new D2D1_GRADIENT_STOP { position = (float)Math.Clamp(gradient.Stops[i].Position, 0, 1), color = TitleColor.Parse(gradient.Stops[i].Color) };
            }

            ID2D1GradientStopCollection* collection;
            Check(dc->CreateGradientStopCollection(stops, (uint)count, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &collection),
                "CreateGradientStopCollection");
            try
            {
                if (gradient.Kind == GradientKind.Radial)
                {
                    var radial = new D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES
                    {
                        center = new D2D_POINT_2F(center.X, center.Y),
                        radiusX = Math.Max(1, m.width / 2),
                        radiusY = Math.Max(1, m.height / 2),
                    };
                    ID2D1RadialGradientBrush* brush;
                    Check(dc->CreateRadialGradientBrush(&radial, null, collection, &brush), "CreateRadialGradientBrush");
                    return (ID2D1Brush*)brush;
                }

                double radians = gradient.Angle * Math.PI / 180;
                var direction = new Vector2((float)Math.Cos(radians), (float)Math.Sin(radians));
                float half = ((Math.Abs(direction.X) * m.width) + (Math.Abs(direction.Y) * m.height)) / 2;
                Vector2 from = center - (direction * half);
                Vector2 to = center + (direction * half);
                var linear = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES
                {
                    startPoint = new D2D_POINT_2F(from.X, from.Y),
                    endPoint = new D2D_POINT_2F(to.X, to.Y),
                };
                ID2D1LinearGradientBrush* line;
                Check(dc->CreateLinearGradientBrush(&linear, null, collection, &line), "CreateLinearGradientBrush");
                return (ID2D1Brush*)line;
            }
            finally
            {
                collection->Release();
            }
        }

        private ID2D1StrokeStyle* StrokeStyle(StrokeJoin join)
        {
            var properties = new D2D1_STROKE_STYLE_PROPERTIES
            {
                startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                lineJoin = join switch
                {
                    StrokeJoin.Miter => D2D1_LINE_JOIN.D2D1_LINE_JOIN_MITER_OR_BEVEL,
                    StrokeJoin.Bevel => D2D1_LINE_JOIN.D2D1_LINE_JOIN_BEVEL,
                    _ => D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
                },
                miterLimit = 2,
                dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_SOLID,
            };
            ID2D1StrokeStyle* strokeStyle;
            Check(((ID2D1Factory*)_owner._d2d)->CreateStrokeStyle(&properties, null, 0, &strokeStyle), "CreateStrokeStyle");
            return strokeStyle;
        }
    }
}
