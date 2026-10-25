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
using Rodaje.App.Shell;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Shortcuts;
using Windows.System;
using Rodaje.App.Views.Timeline;
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
/// La curva de velocidad del clip llega como un parámetro sintético (<see cref="SpeedGraph"/>), lineal y sin manejadores.
/// </summary>
internal sealed partial class KeyframeGraphControl : Grid, IFocusedShortcutTarget
{
    private const double Pad = 10;
    private const double KeyRadius = 6;

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
    private readonly List<TextBlock> _timeLabels = [];
    private readonly TextBlock _empty = new() { FontSize = ThemeResources.Size("BodyFontSize"), TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0), IsHitTestVisible = false };

    private EditorServices? _services;
    private readonly HashSet<Time> _selection = [];
    private KeyframeClipboard? _clipboard;
    private readonly Rectangle _marquee = new() { StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private GraphPoint? _boxStart;
    private HashSet<Time> _boxSelection = [];
    private KeyframeGraphGeometry? _boxGeometry;
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
        Height = 240;
        IsTabStop = true;
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
        _marquee.Stroke = Brush("AccentBrush");
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
        _canvas.Children.Add(_marquee);
        _canvas.Clip = new RectangleGeometry();
        Children.Add(_canvas);
        Children.Add(_readout);
        Children.Add(_empty);
        SizeChanged += (_, _) => Draw();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => { EndBox(); EndDrag(e.Pointer); ReleasePointerCapture(e.Pointer); };
        PointerCaptureLost += (_, _) => { EndBox(); EndDrag(null); };
        PointerWheelChanged += OnWheel;
        var menu = new MenuFlyout();
        foreach ((string label, Interpolation interpolation) in KeyframeEase.Options)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => SetInterpolation(interpolation);
            menu.Items.Add(item);
        }

        menu.Opening += (_, _) =>
        {
            foreach (MenuFlyoutItemBase item in menu.Items)
            {
                item.IsEnabled = _selection.Count > 0 && _drag is null && _effect?.Id != SpeedGraph.EffectId;
            }
        };
        ContextFlyout = menu;
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

    public void Attach(EditorServices services, ClipPropertyEditor editor)
    {
        _services = services;
        EditorSession session = services.Session;
        _session = session;
        _editor = editor;
        session.Timeline.PropertyChanged += (_, _) => Draw();
        session.TransportChanged += (_, _) => Draw();
        session.ProjectEdited += (_, e) =>
        {
            if (e.Kind == EditKind.GestureCancelled && _drag is not null)
            {
                _selection.Clear();
                _selection.UnionWith(_drag.Original.Select(k => k.Time));
                _drag = null;
                _frozenRange = null;
                _readout.Text = "";
                ReleasePointerCaptures();
            }
        };
    }

    /// <summary>El parámetro que se dibuja (o nada, si es nulo).</summary>
    public void Show(Clip? clip, EffectInstance? effect, ParameterSpec? spec)
    {
        if (_drag is not null && (clip?.Id != _clip?.Id || effect?.Id != _effect?.Id || spec?.Name != _name))
        {
            _session?.CancelGesture();
        }

        if (_drag is not null)
        {
            // Durante el arrastre se sigue dibujando lo nuevo, con el rango quieto.
            (_clip, _effect) = (clip, effect);
            Draw();
            return;
        }

        if (spec?.Name != _name || clip?.Id != _clip?.Id || effect?.Id != _effect?.Id)
        {
            _selected = -1;
            _selection.Clear();
        }

        _clip = clip;
        _effect = effect;
        _spec = spec;
        _name = spec?.Name ?? "";
        _selection.IntersectWith(Parameter?.Keyframes.Select(k => k.Time) ?? []);
        _selected = Parameter?.Keyframes.FindIndex(k => _selection.Contains(k.Time)) ?? -1;
        Draw();
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private Parameter? Parameter => _effect is not null && _effect.Parameters.TryGetValue(_name, out Parameter? p) && p.Keyframes.Count >= 1 ? p : null;

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

    private double PlotHeight => Math.Max(1, Height - (2 * Pad) - 22);

    private KeyframeGraphGeometry Geometry(Parameter parameter)
    {
        (double low, double high) = CurrentRange(parameter);
        return new KeyframeGraphGeometry(_clip!, _session!.Timeline.PixelsPerSecond, _session.Timeline.ScrollX,
            Math.Max(1, ActualWidth - 2), Height - 22, low, high);
    }

    public void FitTime()
    {
        if (_clip is { } clip && _session is { } session && _drag is null)
        {
            ViewFrame frame = KeyframeGraphGeometry.FitTime(clip, ActualWidth - 2);
            session.Timeline.PixelsPerSecond = frame.PixelsPerSecond;
            session.Timeline.ScrollX = frame.ScrollX;
        }
    }

    private double Display(double model) => _spec?.ToDisplay(model) ?? model;

    private void Draw()
    {
        double width = ActualWidth - 2;
        if (width <= 0)
        {
            return;
        }

        ((RectangleGeometry)_canvas.Clip).Rect = new Rect(0, 0, width, Height - 2);
        foreach (TextBlock label in _timeLabels)
        {
            _canvas.Children.Remove(label);
        }

        _timeLabels.Clear();
        if (Parameter is not { } parameter || _spec is null || !IsDrawable(parameter))
        {
            _empty.Text = _spec is null
                ? "Elige un clip con parámetros animables."
                : $"«{_spec.Label}» no tiene keyframes. Añádelos con el rombo del inspector.";
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
        KeyframeGraphGeometry geometry = _drag?.Geometry ?? Geometry(parameter);
        double low = geometry.Low;
        double high = geometry.High;
        double X(double seconds) => geometry.Pixel(keys[0].Time + Time.FromSeconds(seconds), 0).Seconds;
        double Y(double value) => geometry.Pixel(keys[0].Time, value).Value;

        var grid = new GeometryGroup();
        for (int i = 0; i <= 4; i++)
        {
            double y = Math.Round(Pad + (i * PlotHeight / 4)) + 0.5;
            grid.Children.Add(new LineGeometry { StartPoint = new Point(Pad, y), EndPoint = new Point(Pad + PlotWidth, y) });
        }

        Rational frameRate = _session!.Sequence.FrameRate;
        RulerStep step = RulerScale.ChooseStep(geometry.PixelsPerSecond, frameRate);
        foreach (RulerTick tick in geometry.TimeTicks(frameRate))
        {
            double x = Pad + tick.X;
            grid.Children.Add(new LineGeometry { StartPoint = new Point(x, Pad), EndPoint = new Point(x, Pad + PlotHeight) });
            if (tick.IsMajor)
            {
                var label = new TextBlock
                {
                    Text = RulerScale.FormatLabel(tick.Time, step, frameRate),
                    FontSize = ThemeResources.Size("MicroFontSize"),
                    Foreground = Brush("TextMutedBrush"),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, Height - 20);
                _canvas.Children.Add(label);
                _timeLabels.Add(label);
            }
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
            diamonds.Children.Add(Diamond(X((keys[i].Time - origin).ToSeconds()), y, _selection.Contains(keys[i].Time) ? KeyRadius + 2 : KeyRadius));
        }

        _keys.Data = diamonds;
        var handles = new GeometryGroup();
        var handleLines = new GeometryGroup();
        for (int i = 0; _mode == GraphMode.Value && i + 1 < keys.Count; i++)
        {
            if (!_selection.Contains(keys[i].Time) || keys[i].Interpolation != Interpolation.Bezier)
            {
                continue;
            }

            (GraphPoint outgoing, GraphPoint incoming) = KeyframeGraph.Handles(keys[i], keys[i + 1], origin);
            var a = new Point(X((keys[i].Time - origin).ToSeconds()), Y(keys[i].Value));
            var b = new Point(X((keys[i + 1].Time - origin).ToSeconds()), Y(keys[i + 1].Value));
            var h1 = new Point(X(outgoing.Seconds), Y(outgoing.Value));
            var h2 = new Point(X(incoming.Seconds), Y(incoming.Value));
            handleLines.Children.Add(new LineGeometry { StartPoint = a, EndPoint = h1 });
            handleLines.Children.Add(new LineGeometry { StartPoint = b, EndPoint = h2 });
            handles.Children.Add(new EllipseGeometry { Center = h1, RadiusX = 5, RadiusY = 5 });
            handles.Children.Add(new EllipseGeometry { Center = h2, RadiusX = 5, RadiusY = 5 });
        }

        _handles.Data = handles;
        _handleLines.Data = handleLines;

        if (_clip is { } clip && _editor is { } editor && editor.IsPlayheadInside(clip))
        {
            double seconds = (editor.KeyframeTimeAtPlayhead(clip) - origin).ToSeconds();
            _playhead.Height = Height - 2;
            Canvas.SetLeft(_playhead, Math.Round(X(seconds)));
            _playhead.Visibility = X(seconds) >= Pad && X(seconds) <= ActualWidth - Pad ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _playhead.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Si la curva se puede dibujar: valores y rango finitos (el validador ya los exige; un NaN en los puntos de una
    /// forma de XAML no debe llegar a la composición).
    /// </summary>
    private bool IsDrawable(Parameter parameter)
    {
        if (!parameter.Keyframes.TrueForAll(k => double.IsFinite(k.Value)))
        {
            return false;
        }

        (double low, double high) = CurrentRange(parameter);
        return double.IsFinite(low) && double.IsFinite(high) && high > low;
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

    private (double Low, double High) CurrentRange(Parameter parameter)
    {
        if (_frozenRange is { } frozen)
        {
            return frozen;
        }

        (Parameter? partner, bool partnerIsY) = Partner();
        return KeyframeGraphGeometry.Fit(parameter, KeyframeGraph.Sample(parameter, _mode, partner, partnerIsY));
    }

    private static GraphPoint Pixel(Point point) => new(point.X, point.Y);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is not { } session || _clip is not { } clip || _effect is not { } effect || Parameter is not { } parameter
            || _mode != GraphMode.Value)
        {
            return;
        }

        var button = e.GetCurrentPoint(this).Properties;
        GraphPoint p = Pixel(e.GetCurrentPoint(_canvas).Position);
        KeyframeGraphGeometry geometry = Geometry(parameter);
        if (button.IsRightButtonPressed)
        {
            // El menú de interpolación actúa sobre la selección: el rombo pulsado la sustituye si no estaba en ella.
            if (geometry.Hit(parameter.Keyframes, _selection, p, 8) is { Handle: < 0 } under && !_selection.Contains(parameter.Keyframes[under.Key].Time))
            {
                _selection.Clear();
                _selection.Add(parameter.Keyframes[under.Key].Time);
                _selected = under.Key;
                Draw();
            }

            return;
        }

        if (!button.IsLeftButtonPressed)
        {
            return;
        }

        Focus(FocusState.Pointer);
        bool add = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        if (geometry.Hit(parameter.Keyframes, _selection, p, 8) is not { } hit)
        {
            _boxStart = p;
            _boxSelection = add ? [.. _selection] : [];
            _boxGeometry = geometry;
            if (!add)
            {
                _selection.Clear();
            }

            CapturePointer(e.Pointer);
            e.Handled = true;
            Draw();
            return;
        }

        Keyframe key = parameter.Keyframes[hit.Key];
        if (hit.Handle < 0)
        {
            if (add && _selection.Remove(key.Time))
            {
                _selected = -1;
                Draw();
                e.Handled = true;
                return;
            }

            if (!add && !_selection.Contains(key.Time))
            {
                _selection.Clear();
            }

            _selection.Add(key.Time);
            _selected = hit.Key;
        }

        _frozenRange = CurrentRange(parameter);
        _drag = new Drag(clip.Id, effect.Id, _name, hit.Handle, key.Time, p, parameter.Keyframes[0].Time,
            geometry, [.. parameter.Keyframes.Where(k => _selection.Contains(k.Time))], Time.Zero, 0,
            effect.Id == SpeedGraph.EffectId ? SequenceSnapshot.Capture(session.Sequence) : null,
            effect.Id == SpeedGraph.EffectId ? session.Sequence.FindClip(clip.Id)?.Clip.Clone() : null);
        session.BeginGesture(hit.Handle < 0 ? "Mover keyframes" : "Cambiar curva");
        CapturePointer(e.Pointer);
        e.Handled = true;
        Draw();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        GraphPoint p = Pixel(e.GetCurrentPoint(_canvas).Position);
        if (_boxStart is { } start && _boxGeometry is { } box && Parameter is { } boxed)
        {
            _selection.Clear();
            _selection.UnionWith(_boxSelection);
            _selection.UnionWith(box.InRectangle(boxed.Keyframes, start, p));
            _selected = boxed.Keyframes.FindIndex(k => _selection.Contains(k.Time));
            Canvas.SetLeft(_marquee, Math.Min(start.Seconds, p.Seconds));
            Canvas.SetTop(_marquee, Math.Min(start.Value, p.Value));
            _marquee.Width = Math.Abs(p.Seconds - start.Seconds);
            _marquee.Height = Math.Abs(p.Value - start.Value);
            _marquee.Visibility = Visibility.Visible;
            e.Handled = true;
            Draw();
            return;
        }

        if (_drag is not { } drag || _session is not { } session || Parameter is not { } parameter || _spec is not { } spec)
        {
            return;
        }

        if (drag.Handle >= 0)
        {
            int index = parameter.Keyframes.FindIndex(k => k.Time == drag.Anchor);
            if (index >= 0 && index + 1 < parameter.Keyframes.Count)
            {
                BezierEase ease = KeyframeGraph.Drag(parameter.Keyframes[index], parameter.Keyframes[index + 1], drag.Origin,
                    drag.Handle == 0, drag.Geometry.FromPixel(p, drag.Origin));
                session.Execute(Edits.SetKeyframeEase(session.SequenceId, drag.ClipId, drag.EffectId, drag.Name, drag.Anchor, ease));
            }
        }
        else
        {
            double dx = p.Seconds - drag.Press.Seconds;
            double anchorX = drag.Geometry.Pixel(drag.Anchor, 0).Seconds;
            Time delta = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) ? Time.Zero
                : drag.Geometry.ClampDelta(drag.Original, drag.Geometry.TimeAt(anchorX + dx, session.Sequence.FrameRate) - drag.Anchor);
            double dv = drag.Geometry.ValueAt(p.Value) - drag.Geometry.ValueAt(drag.Press.Value);
            if (delta == drag.Delta && dv == drag.ValueDelta)
            {
                return;
            }

            (Parameter? partner, bool partnerIsY) = Partner();
            string? other = partner is null ? null : partnerIsY ? BuiltInEffects.TransformParameters.Y : BuiltInEffects.TransformParameters.X;
            if (session.TryExecute(KeyframeGraphEdits.Move(session.SequenceId, drag.ClipId, drag.EffectId, drag.Name,
                drag.Original, drag.Delta, delta, dv, spec, other, session.SpeedScope, drag.Start)))
            {
                drag = drag with { Delta = delta, ValueDelta = dv };
                _drag = drag;
            }

            Clip? now = session.Sequence.FindClip(drag.ClipId)?.Clip;
            // Guardar la rampa de velocidad cambia la duración del clip y con ella el instante de todos sus puntos.
            IEnumerable<Time> Moved(IEnumerable<Time> times) => drag.Before is { } before && now is not null ? SpeedGraph.Follow(before, now, times) : times;
            _selection.Clear();
            _selection.UnionWith(Moved(drag.Original.Select(k => k.Time + drag.Delta)));
            (_clip, _effect) = GraphParameters.Resolve(now, drag.EffectId);
            _selected = Parameter?.Keyframes.FindIndex(k => _selection.Contains(k.Time)) ?? -1;
            Time anchor = Moved([drag.Anchor + drag.Delta]).First();
            _readout.Text = Parameter?.Keyframes.Find(k => k.Time == anchor) is { } moved ? $"{Number(Display(moved.Value))} {spec.Unit}" : "";
        }

        e.Handled = true;
        Draw();
    }

    private void EndBox()
    {
        _boxStart = null;
        _boxGeometry = null;
        _marquee.Visibility = Visibility.Collapsed;
    }

    public bool TryHandleShortcut(KeyChord chord)
    {
        VirtualKey key = (VirtualKey)chord.Key;
        bool ctrl = chord.Modifiers == KeyModifiers.Control;
        bool reserved = (ctrl && key is VirtualKey.C or VirtualKey.V or VirtualKey.A)
            || (chord.Modifiers == KeyModifiers.None && key is VirtualKey.Delete or VirtualKey.Back);
        if (!reserved)
        {
            return false;
        }

        if (_session is not { } session || _clip is not { } clip || _effect is not { } effect || _spec is null || _drag is not null)
        {
            return true;
        }

        if (ctrl && key == VirtualKey.C && Parameter is { } parameter && _selection.Count > 0)
        {
            _clipboard = KeyframeClipboard.Copy(parameter, _selection);
        }
        else if (ctrl && key == VirtualKey.V && _clipboard is { } clipboard && _editor!.IsPlayheadInside(clip))
        {
            Time at = _editor.KeyframeTimeAtPlayhead(clip);
            Clip? before = effect.Id == SpeedGraph.EffectId ? session.Sequence.FindClip(clip.Id)?.Clip.Clone() : null;
            if (session.Execute(KeyframeGraphEdits.Paste(session.SequenceId, clip.Id, effect.Id, _name, clipboard, at, session.SpeedScope)))
            {
                IEnumerable<Time> pasted = clipboard.PasteAt(at).Select(k => k.Time);
                _selection.Clear();
                _selection.UnionWith(before is not null && session.Sequence.FindClip(clip.Id)?.Clip is { } after ? SpeedGraph.Follow(before, after, pasted) : pasted);
            }
        }
        else if (key is VirtualKey.Delete or VirtualKey.Back && _selection.Count > 0)
        {
            if (session.Execute(KeyframeGraphEdits.Delete(session.SequenceId, clip.Id, effect.Id, _name, _selection, session.SpeedScope)))
            {
                _selection.Clear();
                _selected = -1;
            }
        }
        else if (ctrl && key == VirtualKey.A && Parameter is { } all)
        {
            _selection.UnionWith(all.Keyframes.Select(k => k.Time));
        }
        else
        {
            return true;
        }

        Edited?.Invoke(this, EventArgs.Empty);
        Draw();
        return true;
    }

    private void SetInterpolation(Interpolation interpolation)
    {
        if (_session is not { } session || _clip is not { } clip || _effect is not { } effect || _selection.Count == 0 || _drag is not null
            || effect.Id == SpeedGraph.EffectId)
        {
            return;
        }

        Time[] times = [.. _selection];
        session.Execute(new SequenceEditCommand("Cambiar interpolación", session.SequenceId, ctx =>
        {
            foreach (Time time in times)
            {
                Rodaje.Core.Editing.EffectEdits.SetKeyframeInterpolation(ctx, clip.Id, effect.Id, _name, time, interpolation);
            }
        }));
        Draw();
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_services is not { } services || _drag is not null)
        {
            return;
        }

        var point = e.GetCurrentPoint(_canvas);
        bool tilt = point.Properties.IsHorizontalMouseWheel;
        string? gesture = MouseShortcutHost.Resolve(services.ShortcutRegistry, MouseContext.Timeline,
            tilt ? MouseInput.Tilt : MouseInput.Wheel, e.KeyModifiers);
        // Sin pistas que bajar, la rueda que bajaría por ellas desplaza el panel que contiene la gráfica.
        gesture = MouseGestures.EffectiveTimelineWheel(gesture, false, tilt, false);
        int signed = tilt ? point.Properties.MouseWheelDelta : -point.Properties.MouseWheelDelta;
        TimelineState state = services.Session.Timeline;
        switch (gesture)
        {
            case MouseGestures.Zoom:
                double anchor = point.Position.X - Pad;
                double seconds = (anchor + state.ScrollX) / state.PixelsPerSecond;
                state.PixelsPerSecond *= Math.Pow(1.2, -signed / 120.0);
                state.ScrollX = (seconds * state.PixelsPerSecond) - anchor;
                break;
            case MouseGestures.ScrollTime:
                state.ScrollX += signed;
                break;
            case MouseGestures.Frames:
                MouseShortcutHost.StepFrames(services.Shortcuts, MouseShortcutHost.Steps(point.Properties));
                break;
            default:
                return;
        }

        e.Handled = true;
        Draw();
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

    private sealed record Drag(string ClipId, string EffectId, string Name, int Handle, Time Anchor,
        GraphPoint Press, Time Origin, KeyframeGraphGeometry Geometry, IReadOnlyList<Keyframe> Original, Time Delta, double ValueDelta,
        SequenceSnapshot? Start, Clip? Before);
}
