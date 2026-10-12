using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Theme;
using Rodaje.Render.Scopes;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Panel de monitores: elige el monitor, toma del motor la imagen más nueva unas 30 veces por segundo mientras está a la
/// vista y dibuja la retícula (niveles al 0, 25, 50, 75 y 100 %; objetivos al 75 % y línea de piel en el vectorscopio).
/// </summary>
internal sealed partial class ScopesPanel : UserControl
{
    private readonly DispatcherQueueTimer _timer;
    private readonly ToggleButton[] _buttons;
    private PlayerEngine? _engine;
    private WriteableBitmap? _wide;
    private WriteableBitmap? _square;
    private byte[]? _squarePixels;
    private ScopeKind _kind = ScopeKind.Waveform;

    public ScopesPanel()
    {
        InitializeComponent();
        _buttons = [LumaButton, RgbButton, ParadeButton, VectorButton, HistogramButton];
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) => Poll();
        Loaded += (_, _) => Apply();
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Se pidió cerrar el panel con su botón.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Cambió el monitor elegido.</summary>
    public event EventHandler? KindChanged;

    public ScopeKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Apply();
        }
    }

    public void Attach(PlayerEngine engine) => _engine = engine;

    /// <summary>El monitor de la última imagen que llegó de la GPU y se muestra; nulo hasta la primera.</summary>
    public ScopeKind? ShownKind { get; private set; }

    /// <summary>Enciende o apaga la medición en el motor (solo se mide con el panel abierto).</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            _engine?.SetScope(_kind);
            _timer.Start();
        }
        else
        {
            _engine?.SetScope(null);
            _timer.Stop();
        }
    }

    public static string DisplayName(ScopeKind kind) => kind switch
    {
        ScopeKind.WaveformRgb => "Forma de onda RGB",
        ScopeKind.Parade => "Desfile RGB",
        ScopeKind.Vectorscope => "Vectorscopio",
        ScopeKind.Histogram => "Histograma",
        _ => "Forma de onda (luma)",
    };

    private void Apply()
    {
        for (int i = 0; i < _buttons.Length; i++)
        {
            _buttons[i].IsChecked = (ScopeKind)i == _kind;
        }

        bool square = _kind == ScopeKind.Vectorscope;
        ScopeImage.Source = square ? _square : _wide;
        Caption.Text = _kind switch
        {
            ScopeKind.Vectorscope => "Los cuadros marcan los colores puros al 75 %; la línea, el tono de la piel.",
            ScopeKind.Histogram => "Cuántos píxeles hay en cada nivel, de negro (izquierda) a blanco (derecha).",
            ScopeKind.Parade => "Rojo, verde y azul por separado: en un gris neutro, las tres formas quedan a la misma altura.",
            _ => "Cada columna del monitor es una columna de la imagen; la altura es el nivel, de 0 a 100 %.",
        };
        LayoutFrame();
        _engine?.SetScope(_timer.IsRunning ? _kind : null);
    }

    private void OnKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse(tag, out ScopeKind kind))
        {
            Kind = kind;
            KindChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnAreaSizeChanged(object sender, SizeChangedEventArgs e) => LayoutFrame();

    private void Poll()
    {
        if (_engine?.TakeScope() is not { } image)
        {
            return;
        }

        if (image.Kind == ScopeKind.Vectorscope)
        {
            _square ??= new WriteableBitmap(256, 256);
            _squarePixels ??= new byte[256 * 256 * 4];
            for (int y = 0; y < 256; y++)
            {
                Array.Copy(image.Bgra, y * image.Width * 4, _squarePixels, y * 256 * 4, 256 * 4);
            }

            _squarePixels.CopyTo(_square.PixelBuffer);
            _square.Invalidate();
        }
        else
        {
            _wide ??= new WriteableBitmap(image.Width, image.Height);
            image.Bgra.CopyTo(_wide.PixelBuffer);
            _wide.Invalidate();
        }

        if (image.Kind == _kind)
        {
            ShownKind = image.Kind;
            WriteableBitmap? shown = _kind == ScopeKind.Vectorscope ? _square : _wide;
            if (!ReferenceEquals(ScopeImage.Source, shown))
            {
                ScopeImage.Source = shown;
            }
        }
    }

    /// <summary>El vectorscopio va en un cuadrado centrado; el resto llena el área. La retícula se rehace con el tamaño.</summary>
    private void LayoutFrame()
    {
        double width = Math.Max(0, ScopeArea.ActualWidth);
        double height = Math.Max(0, ScopeArea.ActualHeight);
        if (_kind == ScopeKind.Vectorscope)
        {
            double side = Math.Min(width, height);
            ScopeFrame.Width = side;
            ScopeFrame.Height = side;
        }
        else
        {
            ScopeFrame.Width = width;
            ScopeFrame.Height = Math.Min(height, Math.Max(80, width * 0.6));
        }

        DrawGraticule(Math.Max(0, ScopeFrame.Width - 2), Math.Max(0, ScopeFrame.Height - 2));
    }

    private void DrawGraticule(double width, double height)
    {
        Graticule.Children.Clear();
        if (width < 20 || height < 20)
        {
            return;
        }

        Brush line = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        Brush text = ThemeResources.Brush("TextMutedBrush");
        if (_kind == ScopeKind.Vectorscope)
        {
            double scale = width / 256;
            AddEllipse(128 * scale, 128 * scale, 120 * scale, line);
            AddLine(128 * scale, 4 * scale, 128 * scale, 252 * scale, line, 0.5);
            AddLine(4 * scale, 128 * scale, 252 * scale, 128 * scale, line, 0.5);
            (double sx, double sy) = ScopeGraticule.SkinLine(124);
            Line skin = AddLine(128 * scale, 128 * scale, sx * scale, sy * scale, ThemeResources.Brush("AccentBrush"), 1);
            skin.StrokeDashArray = [4, 3];
            foreach (VectorTarget target in ScopeGraticule.Targets(0.75))
            {
                var box = new Rectangle { Width = 10, Height = 10, Stroke = line, StrokeThickness = 1 };
                Canvas.SetLeft(box, (target.X * scale) - 5);
                Canvas.SetTop(box, (target.Y * scale) - 5);
                Graticule.Children.Add(box);
                AddText(target.Label, (target.X * scale) + 7, (target.Y * scale) - 7, text);
            }

            return;
        }

        foreach (int level in ScopeGraticule.Levels)
        {
            double y = ScopeGraticule.Row(level) / ScopeRenderer.ImageHeight * height;
            if (_kind == ScopeKind.Histogram)
            {
                double x = level / 100.0 * width;
                AddLine(x, 0, x, height, line, 0.5);
                AddText(level.ToString(System.Globalization.CultureInfo.InvariantCulture), Math.Min(x + 3, width - 22), 2, text);
                continue;
            }

            AddLine(0, y, width, y, line, level is 0 or 100 ? 1 : 0.5);
            AddText(level.ToString(System.Globalization.CultureInfo.InvariantCulture), 3, Math.Clamp(y - 14, 0, height - 14), text);
        }

        if (_kind == ScopeKind.Parade)
        {
            for (int i = 1; i < 3; i++)
            {
                AddLine(width * i / 3, 0, width * i / 3, height, line, 1);
            }
        }
    }

    private Line AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };
        Graticule.Children.Add(line);
        return line;
    }

    private void AddEllipse(double cx, double cy, double radius, Brush brush)
    {
        var ellipse = new Ellipse { Width = 2 * radius, Height = 2 * radius, Stroke = brush, StrokeThickness = 1 };
        Canvas.SetLeft(ellipse, cx - radius);
        Canvas.SetTop(ellipse, cy - radius);
        Graticule.Children.Add(ellipse);
    }

    private void AddText(string value, double x, double y, Brush brush)
    {
        var block = new TextBlock { Text = value, Foreground = brush, FontSize = ThemeResources.Size("MicroFontSize") };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        Graticule.Children.Add(block);
    }
}
