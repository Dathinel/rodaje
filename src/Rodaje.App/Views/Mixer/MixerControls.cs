using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Audio;
using Rodaje.App.Theme;
using Windows.System;

namespace Rodaje.App.Views.Mixer;

/// <summary>
/// Un control que se arrastra (fader o paneo): avisa al empezar, en cada cambio y al terminar, así quien lo usa agrupa
/// el arrastre en un paso del historial. Mayús afina; doble clic vuelve al valor neutro; la rueda y las flechas mueven
/// de a un paso.
/// </summary>
internal abstract partial class DragControl : Grid
{
    private bool _dragging;
    private double _grabValue;
    private double _grabPosition;
    private double _value;

    protected DragControl()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        Background = ThemeResources.Brush("TransparentBrush");
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, _) => End(null);
        DoubleTapped += (_, _) => Commit(Neutral);
        PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            Commit(Value + (Math.Sign(delta) * Step));
            e.Handled = true;
        };
        KeyDown += OnKey;
        PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(Cursor);
    }

    public event EventHandler? DragStarted;

    public event EventHandler<double>? ValueChanging;

    public event EventHandler? DragCompleted;

    public double Value
    {
        get => _value;
        set
        {
            if (!_dragging)
            {
                SetShown(value);
            }
        }
    }

    public bool IsDragging => _dragging;

    protected abstract double Minimum { get; }

    protected abstract double Maximum { get; }

    protected abstract double Neutral { get; }

    protected abstract double Step { get; }

    protected abstract InputSystemCursorShape Cursor { get; }

    /// <summary>Lo que se arrastra, en píxeles, para recorrer de <see cref="Minimum"/> a <see cref="Maximum"/>.</summary>
    protected abstract double Travel { get; }

    /// <summary>Coordenada del puntero que cuenta (la vertical en un fader, invertida).</summary>
    protected abstract double Axis(Windows.Foundation.Point point);

    /// <summary>Del valor a la fracción de la carrera (0 a 1) y al revés.</summary>
    protected abstract double ToPosition(double value);

    protected abstract double FromPosition(double position);

    protected abstract void Render(double value);

    private void SetShown(double value)
    {
        _value = Math.Clamp(value, Minimum, Maximum);
        Render(_value);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer))
        {
            return;
        }

        Focus(FocusState.Pointer);
        _dragging = true;
        _grabValue = _value;
        _grabPosition = Axis(e.GetCurrentPoint(this).Position);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        bool fine = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        double moved = (Axis(e.GetCurrentPoint(this).Position) - _grabPosition) / Math.Max(1, Travel) * (fine ? 0.2 : 1);
        double next = Math.Clamp(FromPosition(ToPosition(_grabValue) + moved), Minimum, Maximum);
        if (next != _value)
        {
            SetShown(next);
            ValueChanging?.Invoke(this, _value);
        }

        e.Handled = true;
    }

    private void End(Pointer? pointer)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }

        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        double? next = e.Key switch
        {
            VirtualKey.Up or VirtualKey.Right => Value + Step,
            VirtualKey.Down or VirtualKey.Left => Value - Step,
            VirtualKey.Home => Neutral,
            _ => null,
        };
        if (next is { } value)
        {
            Commit(value);
            e.Handled = true;
        }
    }

    /// <summary>Un cambio suelto (rueda, teclado, doble clic) como un arrastre corto.</summary>
    private void Commit(double value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (value == _value)
        {
            return;
        }

        DragStarted?.Invoke(this, EventArgs.Empty);
        SetShown(value);
        ValueChanging?.Invoke(this, _value);
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Fader vertical en dB con la curva de una mesa (<see cref="MixerModel.FaderPosition"/>) y su escala.</summary>
internal sealed partial class FaderControl : DragControl
{
    private const double ThumbHeight = 26;
    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { Width = 4, RadiusX = 2, RadiusY = 2 };
    private readonly Rectangle _unity = new() { Height = 1 };
    private readonly Border _thumb = new() { Width = 24, Height = ThumbHeight };
    private readonly List<(TextBlock Label, Rectangle Tick, int Db)> _scale = [];
    private readonly double _max;

    public FaderControl(double maxDb = MixerModel.FaderMaxDb)
    {
        _max = maxDb;
        Width = 54;
        _track.Fill = ThemeResources.Brush("ControlBorderBrush");
        _unity.Fill = ThemeResources.Brush("TextMutedBrush");
        _thumb.Background = ThemeResources.Brush("ControlHoverBrush");
        _thumb.BorderBrush = ThemeResources.Brush("ControlBorderBrush");
        _thumb.BorderThickness = new Thickness(1);
        _thumb.CornerRadius = ThemeResources.Corner("ControlCornerRadius");
        _thumb.Child = new Rectangle { Height = 2, Margin = new Thickness(4, 0, 4, 0), Fill = ThemeResources.Brush("TextBrush") };
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_unity);
        double size = ThemeResources.Size("MicroFontSize");
        foreach (int db in MixerModel.FaderTicks.Where(d => d <= maxDb))
        {
            var label = new TextBlock
            {
                Text = db == 0 ? "0" : db > 0 ? "+" + db.ToString(CultureInfo.InvariantCulture) : "−" + (-db).ToString(CultureInfo.InvariantCulture),
                FontSize = size,
                Foreground = ThemeResources.Brush("TextMutedBrush"),
                Width = 18,
                TextAlignment = TextAlignment.Right,
            };
            var tick = new Rectangle { Width = 4, Height = 1, Fill = ThemeResources.Brush("StrokeBrush") };
            _canvas.Children.Add(label);
            _canvas.Children.Add(tick);
            _scale.Add((label, tick, db));
        }

        _canvas.Children.Add(_thumb);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render(Value);
        AutomationProperties.SetName(this, "Volumen");
        ToolTipService.SetToolTip(this, "Arrastra el volumen (Mayús: fino). Rueda o flechas: de a 0,5 dB. Doble clic: 0 dB.");
    }

    protected override double Minimum => MixerModel.FaderMinDb;

    protected override double Maximum => _max;

    protected override double Neutral => 0;

    protected override double Step => 0.5;

    protected override InputSystemCursorShape Cursor => InputSystemCursorShape.SizeNorthSouth;

    protected override double Travel => Math.Max(1, ActualHeight - ThumbHeight);

    protected override double Axis(Windows.Foundation.Point point) => -point.Y;

    protected override double ToPosition(double value) => Position(value);

    protected override double FromPosition(double position) => Db(position);

    protected override void Render(double value)
    {
        double height = ActualHeight;
        if (height <= ThumbHeight)
        {
            return;
        }

        double x = 32;
        Canvas.SetLeft(_track, x - 2);
        Canvas.SetTop(_track, ThumbHeight / 2);
        _track.Height = height - ThumbHeight;
        Canvas.SetLeft(_unity, x - 8);
        _unity.Width = 16;
        Canvas.SetTop(_unity, Y(0));
        // Con poco alto se rotulan solo las marcas que no se pisan (0 dB siempre).
        double last = double.NegativeInfinity;
        foreach ((TextBlock label, Rectangle tick, int db) in _scale)
        {
            double y = Y(db);
            bool room = y - last >= 11 || db == 0;
            label.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
            last = room ? y : last;
            Canvas.SetTop(label, y - 7);
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(tick, y);
            Canvas.SetLeft(tick, x + 8);
        }

        Canvas.SetLeft(_thumb, x - 12);
        Canvas.SetTop(_thumb, Y(value) - (ThumbHeight / 2));
        AutomationProperties.SetHelpText(this, MixerModel.FormatDb(value) + " dB");
    }

    private double Y(double db) => (ThumbHeight / 2) + ((1 - Position(db)) * (ActualHeight - ThumbHeight));

    private double Position(double db) => MixerModel.FaderPosition(db) / MixerModel.FaderPosition(_max);

    private double Db(double position) => MixerModel.FaderDb(Math.Clamp(position, 0, 1) * MixerModel.FaderPosition(_max));
}

/// <summary>Paneo o balance horizontal de −100 a 100, con la parte desde el centro resaltada.</summary>
internal sealed partial class PanControl : DragControl
{
    private readonly Canvas _canvas = new() { Height = 14 };
    private readonly Rectangle _track = new() { Height = 4, RadiusX = 2, RadiusY = 2 };
    private readonly Rectangle _fill = new() { Height = 4 };
    private readonly Rectangle _center = new() { Width = 1, Height = 10 };
    private readonly Rectangle _thumb = new() { Width = 3, Height = 12, RadiusX = 1, RadiusY = 1 };

    public PanControl(string name)
    {
        Height = 16;
        _track.Fill = ThemeResources.Brush("ControlBorderBrush");
        _fill.Fill = ThemeResources.Brush("AccentBrush");
        _center.Fill = ThemeResources.Brush("TextMutedBrush");
        _thumb.Fill = ThemeResources.Brush("TextBrush");
        _canvas.Children.Add(_track);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_center);
        _canvas.Children.Add(_thumb);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render(Value);
        AutomationProperties.SetName(this, name);
    }

    protected override double Minimum => -100;

    protected override double Maximum => 100;

    protected override double Neutral => 0;

    protected override double Step => 5;

    protected override InputSystemCursorShape Cursor => InputSystemCursorShape.SizeWestEast;

    protected override double Travel => Math.Max(1, ActualWidth - 4);

    protected override double Axis(Windows.Foundation.Point point) => point.X;

    protected override double ToPosition(double value) => (value + 100) / 200;

    protected override double FromPosition(double position) => Math.Round((position * 200) - 100);

    protected override void Render(double value)
    {
        double width = ActualWidth;
        if (width <= 4)
        {
            return;
        }

        double x = 2 + ((width - 4) * ToPosition(value));
        double center = width / 2;
        Canvas.SetTop(_track, 5);
        _track.Width = width;
        Canvas.SetTop(_fill, 5);
        Canvas.SetLeft(_fill, Math.Min(x, center));
        _fill.Width = Math.Abs(x - center);
        Canvas.SetLeft(_center, center);
        Canvas.SetTop(_center, 2);
        Canvas.SetLeft(_thumb, x - 1.5);
        Canvas.SetTop(_thumb, 1);
        AutomationProperties.SetHelpText(this, MixerModel.FormatPan(value));
    }
}
