using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>El teclado dibujado de la página de atajos.</summary>
internal sealed partial class KeyboardView : UserControl
{
    private const double Unit = 50;

    // Las teclas se dibujan algo más altas que anchas para que quepan dos líneas con la acción.
    private const double UnitY = 58;
    private const double Gap = 4;
    private const string Hint = "Pasa el ratón por una tecla para ver qué hace; haz clic para cambiarla y ver sus acciones en la lista.";

    private readonly List<KeyVisual> _keys = [];
    private KeyModifiers _layer;
    private int? _selected;

    public KeyboardView()
    {
        InitializeComponent();
        Board.Width = KeyboardGeometry.Width * Unit;
        Board.Height = KeyboardGeometry.Height * UnitY;
        InfoText.Text = Hint;
        UpdateLayerText();
    }

    /// <summary>Se pulsó una tecla: la página la abre para cambiarla y filtra la lista por ella.</summary>
    public event EventHandler<int>? KeySelected;

    /// <summary>La persona cambió Ctrl, Mayús o Alt en el dibujo.</summary>
    public event EventHandler<KeyModifiers>? LayerChanged;

    /// <summary>Muestra las combinaciones con esos modificadores (los que se aprietan de verdad o los del ratón).</summary>
    public void SetLayer(KeyModifiers layer)
    {
        layer &= KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt;
        if (layer == _layer)
        {
            return;
        }

        _layer = layer;
        UpdateLayerText();
        foreach (KeyVisual key in _keys)
        {
            Paint(key);
        }
    }

    /// <summary>El botón dibujado de una tecla (para la autoprueba).</summary>
    internal Button? KeyButton(int virtualKey) => _keys.Find(k => k.State.VirtualKey == virtualKey && k.State.Cap.Modifier == KeyModifiers.None)?.Button;

    /// <summary>Pone las teclas al día (estilo, cambios, distribución activa).</summary>
    public void Show(IReadOnlyList<KeyCapState> states, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (_keys.Count != states.Count)
        {
            Build(states);
        }

        for (int i = 0; i < states.Count; i++)
        {
            _keys[i].State = states[i];
            Paint(_keys[i]);
        }

        Legend.Children.Clear();
        foreach (string group in groups)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            item.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = GroupBrushes.For(group), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = group, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
            Legend.Children.Add(item);
        }
    }

    /// <summary>Marca la tecla por la que se filtra la lista (o ninguna).</summary>
    public void Select(int? virtualKey)
    {
        _selected = virtualKey;
        foreach (KeyVisual key in _keys)
        {
            Paint(key);
        }
    }

    private void Build(IReadOnlyList<KeyCapState> states)
    {
        Board.Children.Clear();
        _keys.Clear();
        foreach (KeyCapState state in states)
        {
            KeyCap cap = state.Cap;
            var label = new TextBlock { FontSize = ThemeResources.Size("CaptionFontSize"), TextTrimming = TextTrimming.CharacterEllipsis };
            var action = new TextBlock
            {
                FontSize = ThemeResources.Size("MicroFontSize"),
                TextWrapping = TextWrapping.WrapWholeWords,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var bar = new Rectangle { Height = 3, Margin = new Thickness(-3, 1, -3, 0), Fill = GroupBrushes.Resource("TransparentBrush") };
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(label);
            Grid.SetRow(action, 1);
            content.Children.Add(action);
            Grid.SetRow(bar, 2);
            content.Children.Add(bar);

            var button = new Button
            {
                Style = (Style)Resources["KeyCapButtonStyle"],
                Width = (cap.Width * Unit) - Gap,
                Height = (cap.Height * UnitY) - Gap,
                Content = content,
            };
            Canvas.SetLeft(button, cap.X * Unit);
            Canvas.SetTop(button, cap.Y * UnitY);
            var key = new KeyVisual(button, label, action, bar) { State = state };
            button.Click += (_, _) => OnKeyClick(key);
            button.PointerEntered += (_, _) => Describe(key);
            button.GotFocus += (_, _) => Describe(key);
            Board.Children.Add(button);
            _keys.Add(key);
        }
    }

    private void Paint(KeyVisual key)
    {
        KeyCapState state = key.State;
        KeyCap cap = state.Cap;
        bool layerKey = cap.Modifier != KeyModifiers.None && _layer.HasFlag(cap.Modifier);
        KeyBinding? binding = state.For(_layer);
        bool selected = _selected is { } vk && vk == state.VirtualKey && !cap.IsInert;

        key.Label.Text = state.Label;
        key.Label.Foreground = GroupBrushes.Resource(layerKey ? "AccentBrush" : binding is null && state.Bindings.Count == 0 ? "TextMutedBrush" : "TextBrush");
        key.Action.Text = binding?.Action.ShortLabel ?? "";
        key.Action.Foreground = binding is null ? GroupBrushes.Resource("TextMutedBrush") : GroupBrushes.For(binding.Action.Group);
        key.Bar.Fill = binding is null ? GroupBrushes.Resource("TransparentBrush") : GroupBrushes.For(binding.Action.Group);
        key.Button.Background = GroupBrushes.Resource(layerKey || selected ? "AccentSubtleBrush" : cap.IsInert ? "BarBrush" : "ControlBrush");
        key.Button.BorderBrush = GroupBrushes.Resource(selected ? "AccentBrush" : "StrokeBrush");
        key.Button.BorderThickness = new Thickness(selected ? 2 : 1);

        string name = cap.Modifier != KeyModifiers.None
            ? $"{state.Label}: {(layerKey ? "quitar" : "ver")} las combinaciones con {state.Label}"
            : $"{state.Label}: {(binding is null ? "sin acción" : binding.Action.Label)}";
        AutomationProperties.SetName(key.Button, name);
        string? tip = state.Bindings.Count == 0 ? null : string.Join("\n", state.Bindings.Select(b => $"{b.Chord}  →  {b.Action.Label}"));
        ToolTipService.SetToolTip(key.Button, tip);
    }

    private void OnKeyClick(KeyVisual key)
    {
        KeyCapState state = key.State;
        if (state.Cap.Modifier != KeyModifiers.None)
        {
            _layer ^= state.Cap.Modifier;
            UpdateLayerText();
            foreach (KeyVisual other in _keys)
            {
                Paint(other);
            }

            Describe(key);
            LayerChanged?.Invoke(this, _layer);
            return;
        }

        if (state.Cap.IsInert)
        {
            InfoText.Text = $"«{state.Label}» no se usa para atajos.";
            return;
        }

        KeySelected?.Invoke(this, state.VirtualKey);
    }

    private void Describe(KeyVisual key)
    {
        KeyCapState state = key.State;
        if (state.Cap.Modifier != KeyModifiers.None)
        {
            InfoText.Text = $"Pulsa «{state.Label}» en el dibujo para ver qué hacen las teclas junto con {state.Label}.";
        }
        else if (state.Bindings.Count == 0)
        {
            InfoText.Text = $"«{state.Label}»: sin acción.";
        }
        else
        {
            InfoText.Text = string.Join("   ·   ", state.Bindings.Select(b => $"{b.Chord}: {b.Action.Label}"));
        }
    }

    private void UpdateLayerText() =>
        LayerText.Text = _layer == KeyModifiers.None
            ? "Teclas solas. Pulsa Ctrl, Mayús o Alt en el dibujo para ver sus combinaciones."
            : $"Mostrando {KeyNames.FormatModifiers(_layer)}tecla. Vuelve a pulsar el modificador para quitarlo.";

    private sealed class KeyVisual(Button button, TextBlock label, TextBlock action, Rectangle bar)
    {
        public Button Button { get; } = button;

        public TextBlock Label { get; } = label;

        public TextBlock Action { get; } = action;

        public Rectangle Bar { get; } = bar;

        public required KeyCapState State { get; set; }
    }
}
