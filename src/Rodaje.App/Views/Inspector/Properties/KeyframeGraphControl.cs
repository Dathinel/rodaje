using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services;
using Rodaje.App.Theme;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>
/// Editor de gráficas de un parámetro animado: la curva del valor (o de su velocidad) entre el primer y el último
/// keyframe, los keyframes como rombos, el cursor y los dos manejadores Bézier del tramo que sale del keyframe elegido.
/// Arrastrar un rombo cambia su valor (y, en horizontal, su instante, pegado a los cuadros); arrastrar un manejador
/// vuelve el tramo Bézier. Cada arrastre es un paso del historial y Esc lo revierte. Dibujado en C# con formas de XAML.
/// </summary>
internal sealed partial class KeyframeGraphControl : Grid
{
    private const double Pad = 10;
    private const double HitRadius = 8;
    private const double KeyRadius = 4.5;

    private readonly Canvas _canvas = new();
    private readonly Path _grid = new() { StrokeThickness = 1 };
    private readonly Polyline _curve = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _keys = new() { StrokeThickness = 1.5 };
    private readonly Path _handleLines = new() { StrokeThickness = 1 };
    private readonly Path _handles = new() { StrokeThickness = 1.5 };
    private readonly Rectangle _playhead = new() { Width = 1 };
    private readonly TextBlock _readout = new() { FontSize = ThemeResources.Size("CaptionFontSize"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 8, 0), IsHitTestVisible = false };
    private readonly TextBlock _high = new() { FontSize = ThemeResources.Size("MicroFontSize"), IsHitTestVisible = false };
    private readonly TextBlock _low = new() { FontSize = ThemeResources.Size("MicroFontSize"), IsHitTestVisible = false };
    private readonly TextBlock _empty = new() { FontSize = ThemeResources.Size("BodyFontSize"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0), IsHitTestVisible = false };

    private EditorSession? _session;
    private ClipPropertyEditor? _editor;
    private Clip? _clip;
    private EffectInstance? _effect;
    private string _name = "";
    private ParameterSpec? _spec;
    private GraphMode _mode = GraphMode.Value;
    private int _selected = -1;
    private (double Low, double High)? _frozenRange;
    private Drag? _drag;

    public KeyframeGraphControl()
    {
        Height = 150;
        Background = Brush("BackgroundBrush");
        BorderBrush = Brush("ControlBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        _grid.Stroke = Brush("StrokeBrush");
        _curve.Stroke = Brush("KeyframeBrush");
        _keys.Fill = Brush("KeyframeBrush");
        _keys.Stroke = Brush("BackgroundBrush");
        _handleLines.Stroke = Brush("TextMutedBrush");
        _handles.Fill = Brush("BackgroundBrush");
        _handles.Stroke = Brush("AccentBrush");
        _playhead.Fill = Brush("AccentBrush");
        _readout.Foreground = Brush("TextBrush");
        _high.Foreground = _low.Foreground = Brush("TextMutedBrush");
        _empty.Foreground = Brush("TextMutedBrush");
        _canvas.Children.Add(_grid);
        _canvas.Children.Add(_high);
        _canvas.Children.Add(_low);
        _canvas.Children.Add(_playhead);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_handleLines);
        _canvas.Children.Add(_keys);
        _canvas.Children.Add(_handles);
        _canvas.Clip = new RectangleGeometry();
        Children.Add(_canvas);
        Children.Add(_readout);
        Children.Add(_empty);
        SizeChanged += (_, _) => Draw();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => EndDrag(null);
        AutomationProperties.SetName(this, "Gráfica de keyframes");
        ToolTipService.SetToolTip(this, "Arrastra un rombo para cambiar su valor (a los lados, su instante) y los manejadores para dar forma a la curva hasta el siguiente.");
    }

    /// <summary>Cambió algo con un arrastre de la gráfica (para refrescar el panel al soltar).</summary>
    public event EventHandler? Edited;

    public GraphMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Draw();
        }
    }

    public void Attach(EditorSession session, ClipPropertyEditor editor)
    {
        _session = session;
        _editor = editor;
        session.ProjectEdited += (_, e) =>
        {
            if (e.Kind == EditKind.GestureCancelled && _drag is not null)
            {
                _drag = null;
                _frozenRange = null;
                ReleasePointerCaptures();
            }
        };
    }

    /// <summary>El parámetro que se dibuja (o nada, si es nulo).</summary>
    public void Show(Clip? clip, EffectInstance? effect, ParameterSpec? spec)
    {
        if (_drag is not null)
        {
            // Durante el arrastre se sigue dibujando lo nuevo, con el rango quieto.
            (_clip, _effect) = (clip, effect);
            Draw();
            return;
        }

        if (spec?.Name != _name || clip?.Id != _clip?.Id)
        {
            _selected = -1;
        }

        _clip = clip;
        _effect = effect;
        _spec = spec;
        _name = spec?.Name ?? "";
        Draw();
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private Parameter? Parameter => _effect is not null && _effect.Parameters.TryGetValue(_name, out Parameter? p) && p.Keyframes.Count >= 2 ? p : null;

    /// <summary>X o Y de la posición se dibujan sobre la trayectoria, con el otro eje.</summary>
    private (Parameter? Partner, bool PartnerIsY) Partner()
    {
        string? other = _name switch
        {
            BuiltInEffects.TransformParameters.X => BuiltInEffects.TransformParameters.Y,
            BuiltInEffects.TransformParameters.Y => BuiltInEffects.TransformParameters.X,
            _ => null,
        };
        return other is not null && _effect?.Type == BuiltInEffects.Transform && _effect.Parameters.TryGetValue(other, out Parameter? p)
            ? (p, other == BuiltInEffects.TransformParameters.Y)
            : (null, true);
    }

    private double PlotWidth => Math.Max(1, ActualWidth - (2 * Pad) - 2);

    private double PlotHeight => Math.Max(1, Height - (2 * Pad) - 2);

    private double Display(double model) => _spec?.ToDisplay(model) ?? model;

    private void Draw()
    {
        double width = ActualWidth - 2;
        if (width <= 0)
        {
            return;
        }

        ((RectangleGeometry)_canvas.Clip).Rect = new Rect(0, 0, width, Height - 2);
        if (Parameter is not { } parameter || _spec is null)
        {
            _empty.Text = _spec is null
                ? "Ningún parámetro animado. Pon dos keyframes con el rombo de una fila para ver su curva aquí."
                : $"«{_spec.Label}» necesita al menos dos keyframes para tener curva.";
            _empty.Visibility = Visibility.Visible;
            _curve.Points = [];
            _keys.Data = null;
            _handles.Data = null;
            _handleLines.Data = null;
            _grid.Data = null;
            _high.Text = _low.Text = "";
            _playhead.Visibility = Visibility.Collapsed;
            return;
        }

        _empty.Visibility = Visibility.Collapsed;
        (Parameter? partner, bool partnerIsY) = Partner();
        IReadOnlyList<GraphPoint> points = KeyframeGraph.Sample(parameter, _mode, partner, partnerIsY);
        List<Keyframe> keys = parameter.Keyframes;
        double total = Math.Max(1e-6, (keys[^1].Time - keys[0].Time).ToSeconds());
        (double low, double high) = _frozenRange ?? KeyframeGraph.Range(points, _mode == GraphMode.Value ? Math.Abs(keys.Max(k => k.Value) - keys.Min(k => k.Value)) * 0.2 + 1e-6 : 1e-6);
        double X(double seconds) => Pad + (seconds / total * PlotWidth);
        double Y(double value) => Pad + ((high - value) / (high - low) * PlotHeight);

        var grid = new GeometryGroup();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(Pad + (i * PlotHeight / 4)) + 0.5;
            grid.Children.Add(new LineGeometry { StartPoint = new Point(Pad, y), EndPoint = new Point(Pad + PlotWidth, y) });
        }

        _grid.Data = grid;
        string unit = _mode == GraphMode.Value ? _spec.Unit : _spec.Unit + "/s";
        _high.Text = Number(Display(high)) + " " + unit;
        _low.Text = Number(Display(low)) + " " + unit;
        Canvas.SetLeft(_high, Pad + 2);
        Canvas.SetTop(_high, Pad);
        Canvas.SetLeft(_low, Pad + 2);
        Canvas.SetTop(_low, Pad + PlotHeight - 14);

        var line = new PointCollection();
        foreach (GraphPoint p in points)
        {
            line.Add(new Point(X(p.Seconds), Y(p.Value)));
        }

        _curve.Points = line;
        Time origin = keys[0].Time;
        var diamonds = new GeometryGroup();
        for (int i = 0; i < keys.Count; i++)
        {
            double value = _mode == GraphMode.Value ? KeyframeGraph.ValueAt(parameter, partner, partnerIsY, keys[i].Time) : 0;
            double y = _mode == GraphMode.Value ? Y(value) : Pad + PlotHeight;
            diamonds.Children.Add(Diamond(X((keys[i].Time - origin).ToSeconds()), y, i == _selected ? KeyRadius + 1.5 : KeyRadius));
        }

        _keys.Data = diamonds;
        var handles = new GeometryGroup();
        var handleLines = new GeometryGroup();
        if (_mode == GraphMode.Value && _selected >= 0 && _selected + 1 < keys.Count && keys[_selected].Interpolation != Interpolation.Hold)
        {
            (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(keys[_selected], keys[_selected + 1], origin);
            var a = new Point(X((keys[_selected].Time - origin).ToSeconds()), Y(keys[_selected].Value));
            var b = new Point(X((keys[_selected + 1].Time - origin).ToSeconds()), Y(keys[_selected + 1].Value));
            var h1 = new Point(X(outgoing.Seconds), Y(outgoing.Value));
            var h2 = new Point(X(incoming.Seconds), Y(incoming.Value));
            handleLines.Children.Add(new LineGeometry { StartPoint = a, EndPoint = h1 });
            handleLines.Children.Add(new LineGeometry { StartPoint = b, EndPoint = h2 });
            handles.Children.Add(new EllipseGeometry { Center = h1, RadiusX = 4, RadiusY = 4 });
            handles.Children.Add(new EllipseGeometry { Center = h2, RadiusX = 4, RadiusY = 4 });
        }

        _handles.Data = handles;
        _handleLines.Data = handleLines;

        if (_clip is { } clip && _editor is { } editor && editor.IsPlayheadInside(clip))
        {
            double seconds = (editor.KeyframeTimeAtPlayhead(clip) - origin).ToSeconds();
            _playhead.Height = Height - 2;
            Canvas.SetLeft(_playhead, Math.Round(X(Math.Clamp(seconds, 0, total))));
            _playhead.Visibility = seconds >= 0 && seconds <= total ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _playhead.Visibility = Visibility.Collapsed;
        }
    }

    private static PathGeometry Diamond(double x, double y, double r)
    {
        var figure = new PathFigure { StartPoint = new Point(x, y - r), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(x + r, y) });
        figure.Segments.Add(new LineSegment { Point = new Point(x, y + r) });
        figure.Segments.Add(new LineSegment { Point = new Point(x - r, y) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static string Number(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−');

    /// <summary>Qué hay bajo el puntero: un manejador (0 salida, 1 entrada) o un keyframe.</summary>
    private (int Key, int Handle)? Hit(Point p)
    {
        if (Parameter is not { } parameter || _mode != GraphMode.Value)
        {
            return null;
        }

        List<Keyframe> keys = parameter.Keyframes;
        Time origin = keys[0].Time;
        double total = Math.Max(1e-6, (keys[^1].Time - origin).ToSeconds());
        (double low, double high) = CurrentRange(parameter);
        double X(double seconds) => Pad + (seconds / total * PlotWidth);
        double Y(double value) => Pad + ((high - value) / (high - low) * PlotHeight);
        if (_selected >= 0 && _selected + 1 < keys.Count && keys[_selected].Interpolation != Interpolation.Hold)
        {
            (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(keys[_selected], keys[_selected + 1], origin);
            if (Near(p, X(outgoing.Seconds), Y(outgoing.Value)))
            {
                return (_selected, 0);
            }

            if (Near(p, X(incoming.Seconds), Y(incoming.Value)))
            {
                return (_selected, 1);
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            if (Near(p, X((keys[i].Time - origin).ToSeconds()), Y(keys[i].Value)))
            {
                return (i, -1);
            }
        }

        return null;
    }

    private static bool Near(Point p, double x, double y) => ((p.X - x) * (p.X - x)) + ((p.Y - y) * (p.Y - y)) <= HitRadius * HitRadius;

    private (double Low, double High) CurrentRange(Parameter parameter)
    {
        if (_frozenRange is { } frozen)
        {
            return frozen;
        }

        (Parameter? partner, bool partnerIsY) = Partner();
        List<Keyframe> keys = parameter.Keyframes;
        return KeyframeGraph.Range(KeyframeGraph.Sample(parameter, _mode, partner, partnerIsY),
            Math.Abs(keys.Max(k => k.Value) - keys.Min(k => k.Value)) * 0.2 + 1e-6);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(_canvas).Position;
        if (_session is not { } session || _clip is not { } clip || _effect is not { } effect || Parameter is not { } parameter
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Hit(p) is not { } hit)
        {
            return;
        }

        if (hit.Handle < 0 && hit.Key != _selected)
        {
            _selected = hit.Key;
        }

        Keyframe key = parameter.Keyframes[hit.Key];
        session.BeginGesture(hit.Handle < 0 ? "Mover keyframe" : "Cambiar curva");
        _frozenRange = CurrentRange(parameter);
        _drag = new Drag(clip.Id, effect.Id, _name, hit.Key, hit.Handle, key.Time, EffectCommands.TimelineOfKeyframe(clip, key.Time), p, parameter.Keyframes[0].Time);
        CapturePointer(e.Pointer);
        e.Handled = true;
        Draw();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag || _session is not { } session || Parameter is not { } parameter || _frozenRange is not { } range)
        {
            return;
        }

        Point p = e.GetCurrentPoint(_canvas).Position;
        List<Keyframe> keys = parameter.Keyframes;
        double total = Math.Max(1e-6, (keys[^1].Time - keys[0].Time).ToSeconds());
        double seconds = (p.X - Pad) / PlotWidth * total;
        double value = range.High - ((p.Y - Pad) / PlotHeight * (range.High - range.Low));
        int index = keys.FindIndex(k => k.Time == drag.Current);
        if (index < 0)
        {
            return;
        }

        if (drag.Handle >= 0)
        {
            if (index + 1 < keys.Count)
            {
                BezierEase ease = KeyframeGraph.Drag(keys[index], keys[index + 1], drag.Origin, drag.Handle == 0, new GraphPoint(seconds, value));
                session.Execute(Edits.SetKeyframeEase(session.SequenceId, drag.ClipId, drag.EffectId, drag.Name, drag.Current, ease));
            }
        }
        else
        {
            double model = _spec is { } spec ? spec.ToModel(spec.ToDisplay(value)) : value;
            Time at = drag.Current;
            bool vertical = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            if (!vertical && session.Sequence.FindClip(drag.ClipId) is { } found)
            {
                // A los lados, pegado a los cuadros de la secuencia y sin pasar a los vecinos.
                Rational rate = session.Sequence.FrameRate;
                double dx = (p.X - drag.Press.X) / PlotWidth * total;
                Time timeline = Time.FromFrames((drag.Timeline + Time.FromSeconds(dx)).ToFrames(rate), rate);
                Time moved = EffectCommands.KeyframeTime(found.Clip, Time.Max(found.Clip.Start, timeline));
                bool free = (index == 0 || moved > keys[index - 1].Time) && (index + 1 == keys.Count || moved < keys[index + 1].Time);
                if (moved != at && free && session.Execute(MoveWithPartner(drag, at, moved)))
                {
                    at = moved;
                    _drag = drag with { Current = moved };
                }
            }

            session.Execute(Edits.SetKeyframe(session.SequenceId, drag.ClipId, drag.EffectId, drag.Name, at, model));
            _readout.Text = $"{Number(_spec?.ToDisplay(model) ?? model)} {_spec?.Unit}";
        }

        e.Handled = true;
    }

    /// <summary>
    /// Mueve el keyframe y, si es X o Y de la posición y el otro eje tiene uno en el mismo instante, también ese: así la
    /// trayectoria (que pide los dos en los mismos instantes) no se rompe.
    /// </summary>
    private SequenceEditCommand MoveWithPartner(Drag drag, Time from, Time to)
    {
        (Parameter? partner, bool partnerIsY) = Partner();
        string? other = partner is not null && partner.Keyframes.Exists(k => k.Time == from)
            ? (partnerIsY ? BuiltInEffects.TransformParameters.Y : BuiltInEffects.TransformParameters.X)
            : null;
        return new SequenceEditCommand("Mover keyframe", _session!.SequenceId, ctx =>
        {
            Rodaje.Core.Editing.EffectEdits.MoveKeyframe(ctx, drag.ClipId, drag.EffectId, drag.Name, from, to);
            if (other is not null)
            {
                Rodaje.Core.Editing.EffectEdits.MoveKeyframe(ctx, drag.ClipId, drag.EffectId, other, from, to);
            }
        }, mergeKey: $"grafica|{drag.ClipId}|{drag.Name}");
    }

    private void EndDrag(Pointer? pointer)
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        _frozenRange = null;
        _readout.Text = "";
        if (pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }

        _session?.EndGesture();
        Edited?.Invoke(this, EventArgs.Empty);
        Draw();
    }

    private sealed record Drag(string ClipId, string EffectId, string Name, int Key, int Handle, Time Current, Time Timeline, Point Press, Time Origin);
}
