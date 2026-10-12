using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>El ratón dibujado de la página de atajos.</summary>
internal sealed partial class MouseView : UserControl
{
    private const string Hint = "Pasa el ratón por un botón para ver qué hace; haz clic para cambiarlo.";

    // Posición de cada parte (x, y, ancho, alto, esquinas) y de su rótulo (x, y, a la izquierda o a la derecha) en el lienzo de 470 × 270.
    private static readonly PartGeometry[] Geometry =
    [
        new(MouseInput.Left, 170, 20, 63, 100, new CornerRadius(62, 4, 4, 4), 0, 30, (195, 60)),
        new(MouseInput.Right, 237, 20, 63, 100, new CornerRadius(4, 62, 4, 4), 320, 162, (280, 95)),
        new(MouseInput.Wheel, 223, 34, 24, 52, new CornerRadius(12), 320, 0, (247, 50)),
        new(MouseInput.Tilt, 249, 46, 14, 28, new CornerRadius(7), 320, 54, (263, 60)),
        new(MouseInput.Middle, 226, 92, 18, 18, new CornerRadius(9), 320, 108, (244, 101)),
        new(MouseInput.X2, 160, 128, 14, 36, new CornerRadius(5), 0, 118, (167, 146)),
        new(MouseInput.X1, 160, 170, 14, 36, new CornerRadius(5), 0, 176, (167, 188)),
    ];

    private readonly List<PartVisual> _parts = [];
    private ShortcutRegistry? _registry;
    private MouseContext _context = MouseContext.Timeline;
    private KeyModifiers _layer;
    private MouseInput? _selected;
    private bool _syncing;

    public MouseView()
    {
        InitializeComponent();
        Build();
        InfoText.Text = Hint;
    }

    /// <summary>Se pulsó una parte del ratón.</summary>
    public event EventHandler<MouseInput>? PartSelected;

    /// <summary>La persona cambió Ctrl, Mayús o Alt en el dibujo.</summary>
    public event EventHandler<KeyModifiers>? LayerChanged;

    public event EventHandler<MouseContext>? ContextChanged;

    public MouseContext Context => _context;

    public KeyModifiers Layer => _layer;

    public void Show(ShortcutRegistry registry)
    {
        _registry = registry;
        foreach (PartVisual part in _parts)
        {
            Paint(part);
        }
    }

    public void SetLayer(KeyModifiers layer)
    {
        _layer = layer & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt);
        _syncing = true;
        CtrlToggle.IsChecked = _layer.HasFlag(KeyModifiers.Control);
        ShiftToggle.IsChecked = _layer.HasFlag(KeyModifiers.Shift);
        AltToggle.IsChecked = _layer.HasFlag(KeyModifiers.Alt);
        _syncing = false;
        Repaint();
    }

    public void SetContext(MouseContext context)
    {
        _context = context;
        _syncing = true;
        (context switch { MouseContext.Viewer => ViewerTab, MouseContext.Library => LibraryTab, _ => TimelineTab }).IsChecked = true;
        _syncing = false;
        Repaint();
    }

    public void Select(MouseInput? input)
    {
        _selected = input;
        Repaint();
    }

    /// <summary>El botón dibujado de una parte (para la autoprueba).</summary>
    internal Button PartButton(MouseInput input) => _parts.First(p => p.Geometry.Input == input).Button;

    private void Build()
    {
        var body = new Border
        {
            Width = 130,
            Height = 240,
            CornerRadius = new CornerRadius(64, 64, 58, 58),
            Background = GroupBrushes.Resource("BarBrush"),
            BorderBrush = GroupBrushes.Resource("StrokeBrush"),
            BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(body, 170);
        Canvas.SetTop(body, 20);
        Board.Children.Add(body);

        foreach (PartGeometry g in Geometry)
        {
            double fromX = g.LabelX == 0 ? 152 : 318;
            double fromY = g.LabelY + 18;
            Board.Children.Add(new Line
            {
                X1 = fromX,
                Y1 = fromY,
                X2 = g.Anchor.X,
                Y2 = g.Anchor.Y,
                Stroke = GroupBrushes.Resource("StrokeBrush"),
                StrokeThickness = 1,
            });
        }

        foreach (PartGeometry g in Geometry)
        {
            var name = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
            var action = new TextBlock { FontSize = ThemeResources.Size("BodyFontSize"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(name);
            stack.Children.Add(action);
            if (g.LabelX == 0)
            {
                name.HorizontalAlignment = HorizontalAlignment.Right;
                action.HorizontalAlignment = HorizontalAlignment.Right;
            }

            var callout = new Button { Style = (Style)Resources["CalloutStyle"], Width = 150, Content = stack };
            Canvas.SetLeft(callout, g.LabelX);
            Canvas.SetTop(callout, g.LabelY);

            var button = new Button
            {
                Style = (Style)Resources["MousePartStyle"],
                Width = g.Width,
                Height = g.Height,
                CornerRadius = g.Corners,
                Content = g.Input == MouseInput.Tilt ? new TextBlock { Text = "‹›", FontSize = ThemeResources.Size("MicroFontSize") } : null,
            };
            Canvas.SetLeft(button, g.X);
            Canvas.SetTop(button, g.Y);

            var part = new PartVisual(g, button, callout, name, action);
            button.Click += (_, _) => OnPartClick(part);
            callout.Click += (_, _) => OnPartClick(part);
            button.PointerEntered += (_, _) => Describe(part);
            callout.PointerEntered += (_, _) => Describe(part);
            button.GotFocus += (_, _) => Describe(part);
            Board.Children.Add(callout);
            _parts.Add(part);
        }

        // Las partes van encima del cuerpo y de las líneas; la inclinación y el central, encima de los botones grandes.
        foreach (PartVisual part in _parts)
        {
            Board.Children.Add(part.Button);
        }
    }

    private void Repaint()
    {
        foreach (PartVisual part in _parts)
        {
            Paint(part);
        }
    }

    private void Paint(PartVisual part)
    {
        var chord = new MouseChord(part.Geometry.Input, _layer);
        string? fixedLabel = MouseGestures.Fixed(_context, chord);
        string? actionId = _registry?.Mouse.Get(_context, chord);
        bool selected = _selected == part.Geometry.Input;
        string text = fixedLabel ?? (actionId is null || _registry is null ? BindingSlots.NoAction : BindingSlots.ShortLabelOf(_registry, actionId));

        part.Name.Text = KeyNames.FormatModifiers(_layer) + MouseNames.Display(part.Geometry.Input);
        part.Action.Text = text;
        part.Action.Foreground = actionId is not null && _registry is not null
            ? GroupBrushes.For(BindingSlots.GroupOf(_registry, actionId))
            : GroupBrushes.Resource("TextMutedBrush");
        part.Button.Background = GroupBrushes.Resource(selected ? "AccentSubtleBrush" : fixedLabel is not null ? "BarBrush" : "ControlBrush");
        part.Button.BorderBrush = selected ? GroupBrushes.Resource("AccentBrush")
            : actionId is not null && _registry is not null ? GroupBrushes.For(BindingSlots.GroupOf(_registry, actionId))
            : GroupBrushes.Resource("StrokeBrush");
        part.Button.BorderThickness = new Thickness(selected || actionId is not null ? 2 : 1);
        part.Callout.BorderBrush = GroupBrushes.Resource(selected ? "AccentBrush" : "TransparentBrush");

        string name = $"{part.Name.Text} en {MouseNames.Display(_context).ToLowerInvariant()}: {text}";
        AutomationProperties.SetName(part.Button, name);
        AutomationProperties.SetName(part.Callout, name);
        ToolTipService.SetToolTip(part.Button, $"{part.Name.Text} ({MouseNames.Hint(part.Geometry.Input)}): {text}");
    }

    private void Describe(PartVisual part)
    {
        var chord = new MouseChord(part.Geometry.Input, _layer);
        string? fixedLabel = MouseGestures.Fixed(_context, chord);
        string? actionId = _registry?.Mouse.Get(_context, chord);
        InfoText.Text = fixedLabel is not null
            ? $"{chord}: {fixedLabel} (no se cambia)."
            : actionId is null || _registry is null
                ? $"{chord} ({MouseNames.Hint(part.Geometry.Input)}): sin acción en {MouseNames.Display(_context).ToLowerInvariant()}. Haz clic para darle una."
                : $"{chord} ({MouseNames.Hint(part.Geometry.Input)}): {BindingSlots.LabelOf(_registry, actionId)}.";
    }

    private void OnPartClick(PartVisual part)
    {
        Describe(part);
        PartSelected?.Invoke(this, part.Geometry.Input);
    }

    private void OnContextChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _context = ReferenceEquals(sender, ViewerTab) ? MouseContext.Viewer : ReferenceEquals(sender, LibraryTab) ? MouseContext.Library : MouseContext.Timeline;
        Repaint();
        ContextChanged?.Invoke(this, _context);
    }

    private void OnModifierClick(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _layer = (CtrlToggle.IsChecked == true ? KeyModifiers.Control : KeyModifiers.None)
            | (ShiftToggle.IsChecked == true ? KeyModifiers.Shift : KeyModifiers.None)
            | (AltToggle.IsChecked == true ? KeyModifiers.Alt : KeyModifiers.None);
        Repaint();
        LayerChanged?.Invoke(this, _layer);
    }

    private sealed record PartGeometry(MouseInput Input, double X, double Y, double Width, double Height, CornerRadius Corners, double LabelX, double LabelY, (double X, double Y) Anchor);

    private sealed class PartVisual(PartGeometry geometry, Button button, Button callout, TextBlock name, TextBlock action)
    {
        public PartGeometry Geometry { get; } = geometry;

        public Button Button { get; } = button;

        public Button Callout { get; } = callout;

        public TextBlock Name { get; } = name;

        public TextBlock Action { get; } = action;
    }
}
