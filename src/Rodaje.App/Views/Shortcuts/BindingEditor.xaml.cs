using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Theme;

namespace Rodaje.App.Views.Shortcuts;

/// <summary>Lo que el editor de una tecla o un botón le pide a la página.</summary>
internal interface IBindingEditorHost
{
    ShortcutRegistry Registry { get; }

    ShortcutCustomizer Customizer { get; }

    void ShowChange(ShortcutChange change);

    /// <summary>Asigna con la pregunta de conflicto si hace falta. <paramref name="toSlot"/>: desde la lista de la combinación.</summary>
    Task AssignKeyAsync(string actionId, KeyChord chord, KeyChord? replacing, bool toSlot);

    /// <summary>Asigna en el ratón con la pregunta de conflicto si hace falta; con <paramref name="from"/>, la acción se mueve.</summary>
    Task AssignMouseAsync(MouseContext context, MouseChord chord, string actionId, MouseChord? from);

    /// <summary>Graba una combinación de teclado nueva para la acción, en lugar de <paramref name="replacing"/>.</summary>
    void BeginKeyCapture(BindingEditor editor, string actionId, KeyChord replacing);

    void CancelKeyCapture();

    /// <summary>Mientras se graba con el ratón, las teclas no lanzan atajos (Esc cancela la grabación, no cierra la página).</summary>
    void HoldShortcuts(bool hold);

    void CloseEditor();
}

/// <summary>El panel que abre una tecla o un botón del dibujo para cambiar lo que hace con cada modificador.</summary>
internal sealed partial class BindingEditor : UserControl
{
    private IBindingEditorHost? _host;
    private int? _key;
    private MouseContext _context;
    private MouseInput? _mouse;
    private (string ActionId, MouseChord From)? _mouseCapture;
    private bool _keyCapture;

    public BindingEditor()
    {
        InitializeComponent();
    }

    /// <summary>La tecla abierta, si es una tecla.</summary>
    public int? Key => _key;

    /// <summary>El botón abierto y su zona, si es del ratón.</summary>
    public (MouseContext Context, MouseInput Input)? MousePart => _mouse is { } input ? (_context, input) : null;

    public bool IsCapturing => _keyCapture || _mouseCapture is not null;

    public void Attach(IBindingEditorHost host) => _host = host;

    public void ShowKey(int virtualKey)
    {
        EndCapture();
        _key = virtualKey;
        _mouse = null;
        Refresh();
    }

    public void ShowMouse(MouseContext context, MouseInput input)
    {
        EndCapture();
        _key = null;
        _context = context;
        _mouse = input;
        Refresh();
    }

    /// <summary>Vuelve a leer lo que hace cada combinación (tras un cambio, otro estilo o la distribución).</summary>
    public void Refresh()
    {
        if (_host is null)
        {
            return;
        }

        ShortcutRegistry registry = _host.Registry;
        IReadOnlyList<BindingSlot> slots;
        if (_key is { } vk)
        {
            string name = KeyNames.KeyName(vk);
            TitleText.Text = $"Tecla «{name}»";
            SubtitleText.Text = $"En todo el editor, estilo {ShortcutPresetNames.Display(registry.Preset)}. Elige otra acción para una combinación, graba otra combinación para su acción o quítala.";
            ResetPartButton.Content = "Restablecer tecla";
            ToolTipService.SetToolTip(ResetPartButton, $"«{name}» vuelve a sus acciones de fábrica con todos los modificadores");
            ResetContextButton.Visibility = Visibility.Collapsed;
            slots = BindingSlots.ForKey(registry, vk);
        }
        else if (_mouse is { } input)
        {
            string where = MouseNames.Display(_context);
            TitleText.Text = $"{MouseNames.Display(input)} · {where}";
            SubtitleText.Text = $"{char.ToUpperInvariant(MouseNames.Hint(input)[0])}{MouseNames.Hint(input)[1..]}. Lo que hace en {where.ToLowerInvariant()}, estilo {ShortcutPresetNames.Display(registry.Preset)}.";
            ResetPartButton.Content = "Restablecer botón";
            ToolTipService.SetToolTip(ResetPartButton, $"«{MouseNames.Display(input)}» vuelve a lo de fábrica en {where.ToLowerInvariant()}");
            ResetContextButton.Content = $"Restablecer {where.ToLowerInvariant()}";
            ToolTipService.SetToolTip(ResetContextButton, $"Todo el ratón de {where.ToLowerInvariant()} vuelve a lo de fábrica");
            ResetContextButton.Visibility = Visibility.Visible;
            slots = BindingSlots.ForMouse(registry, _context, input);
        }
        else
        {
            return;
        }

        UndoButton.IsEnabled = _host.Customizer.CanUndo;
        Rows.Children.Clear();
        foreach (BindingSlot slot in slots)
        {
            Rows.Children.Add(BuildRow(slot));
        }
    }

    /// <summary>El texto mientras se graba una combinación de teclado (lo pone la página).</summary>
    public void SetCaptureText(string text) => CaptureText.Text = text;

    public void EndCapture()
    {
        bool wasKey = _keyCapture;
        if (_mouseCapture is not null)
        {
            _host?.HoldShortcuts(false);
        }

        _keyCapture = false;
        _mouseCapture = null;
        CapturePad.Visibility = Visibility.Collapsed;
        if (wasKey)
        {
            _host?.CancelKeyCapture();
        }
    }

    /// <summary>La página terminó de grabar (aceptada o cancelada) sin que este panel lo pidiera.</summary>
    public void KeyCaptureEnded()
    {
        _keyCapture = false;
        CapturePad.Visibility = Visibility.Collapsed;
    }

    /// <summary>Elige una acción para una combinación sin abrir la lista (para la autoprueba).</summary>
    internal Task ChooseAsync(KeyModifiers modifiers, string actionId) =>
        _key is { } vk ? _host!.AssignKeyAsync(actionId, new KeyChord(vk, modifiers), null, toSlot: true)
        : _mouse is { } input ? _host!.AssignMouseAsync(_context, new MouseChord(input, modifiers), actionId, null)
        : Task.CompletedTask;

    /// <summary>Restablece la tecla o el botón abierto (para la autoprueba).</summary>
    internal void ResetPart() => OnResetPartClick(this, new RoutedEventArgs());

    private Grid BuildRow(BindingSlot slot)
    {
        var row = new Grid
        {
            ColumnSpacing = 8,
            MinHeight = 34,
            BorderBrush = GroupBrushes.Resource("StrokeBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var combo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        combo.Children.Add(new Border
        {
            Background = GroupBrushes.Resource("ControlBrush"),
            BorderBrush = GroupBrushes.Resource("StrokeBrush"),
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = ThemeResources.Corner("ControlCornerRadius"),
            Padding = new Thickness(6, 1, 6, 1),
            Child = new TextBlock { Text = slot.Text, FontSize = ThemeResources.Size("CaptionFontSize"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
        });
        if (slot.Warning is { } warning)
        {
            var sign = new TextBlock { FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["IconFontFamily"], Text = "", Foreground = GroupBrushes.Resource("MeterHighBrush"), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(sign, warning);
            AutomationProperties.SetName(sign, warning);
            combo.Children.Add(sign);
        }

        row.Children.Add(combo);

        var action = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        action.Children.Add(new Rectangle
        {
            Width = 3,
            Height = 18,
            Fill = slot.Group is { } group ? GroupBrushes.For(group) : GroupBrushes.Resource("TransparentBrush"),
        });
        action.Children.Add(new TextBlock
        {
            Text = slot.Label,
            Style = (Style)Application.Current.Resources["BodyTextStyle"],
            Foreground = GroupBrushes.Resource(slot.ActionId is null ? "TextMutedBrush" : "TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (slot.Customized)
        {
            action.Children.Add(new TextBlock { Text = "cambiada", Style = (Style)Application.Current.Resources["CaptionTextStyle"], Foreground = GroupBrushes.Resource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        }

        if (slot.Locked is { } locked)
        {
            ToolTipService.SetToolTip(action, locked);
        }

        Grid.SetColumn(action, 1);
        row.Children.Add(action);
        AutomationProperties.SetName(row, $"{slot.Text}: {slot.Label}");

        if (slot.IsLocked)
        {
            var reason = new TextBlock { Text = "No se cambia", Style = (Style)Application.Current.Resources["CaptionTextStyle"], VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(reason, slot.Locked);
            Grid.SetColumn(reason, 2);
            row.Children.Add(reason);
            return row;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var choose = new Button { Content = slot.ActionId is null ? "Elegir acción…" : "Cambiar…" };
        choose.Flyout = BuildPicker(slot, choose);
        AutomationProperties.SetName(choose, $"Elegir la acción de {slot.Text}");
        buttons.Children.Add(choose);

        if (slot.ActionId is { } id)
        {
            var record = new Button { Content = "Grabar" };
            ToolTipService.SetToolTip(record, $"Pulsa otra combinación para «{slot.Label}»");
            AutomationProperties.SetName(record, $"Grabar otra combinación para {slot.Label}");
            record.Click += (_, _) => BeginCapture(slot, id);
            buttons.Children.Add(record);

            var remove = new Button { Style = (Style)Application.Current.Resources["ToolButtonStyle"], Content = "" };
            ToolTipService.SetToolTip(remove, $"Quitar: «{slot.Text}» deja de hacer «{slot.Label}»");
            AutomationProperties.SetName(remove, $"Quitar {slot.Text}");
            remove.Click += (_, _) => Remove(slot);
            buttons.Children.Add(remove);
        }

        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);
        return row;
    }

    private Flyout BuildPicker(BindingSlot slot, Button owner)
    {
        var search = new TextBox { PlaceholderText = "Busca una acción («deshacer», «zoom»)", Width = 340 };
        AutomationProperties.SetName(search, $"Buscar la acción para {slot.Text}");
        var list = new ListView { Width = 340, MaxHeight = 300, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        AutomationProperties.SetName(list, "Acciones");
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(search);
        panel.Children.Add(list);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        void Fill()
        {
            list.Items.Clear();
            if (_host is null)
            {
                return;
            }

            IReadOnlyList<BindingChoice> choices = _key is not null
                ? BindingSlots.KeyChoices(_host.Registry, search.Text)
                : BindingSlots.MouseChoices(_host.Registry, _context, new MouseChord(_mouse!.Value, slot.Modifiers), search.Text);
            foreach (BindingChoice choice in choices)
            {
                var item = new Grid { ColumnSpacing = 8, Tag = choice.Id, Padding = new Thickness(0, 4, 0, 4) };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                item.Children.Add(new Rectangle { Width = 3, Fill = GroupBrushes.For(choice.Group) });
                var text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = choice.Id == slot.ActionId ? $"{choice.Label} (actual)" : choice.Label,
                    Style = (Style)Application.Current.Resources["BodyTextStyle"],
                });
                text.Children.Add(new TextBlock { Text = choice.Group, Style = (Style)Application.Current.Resources["CaptionTextStyle"] });
                Grid.SetColumn(text, 1);
                item.Children.Add(text);
                AutomationProperties.SetName(item, $"{choice.Label}, {choice.Group}");
                ToolTipService.SetToolTip(item, string.IsNullOrEmpty(choice.Description) ? null : choice.Description);
                list.Items.Add(item);
            }
        }

        search.TextChanged += (_, _) => Fill();
        flyout.Opening += (_, _) =>
        {
            search.Text = "";
            Fill();
        };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);
        list.ItemClick += async (_, e) =>
        {
            if (e.ClickedItem is not FrameworkElement { Tag: string id } || _host is null)
            {
                return;
            }

            flyout.Hide();
            if (_key is { } vk)
            {
                await _host.AssignKeyAsync(id, new KeyChord(vk, slot.Modifiers), null, toSlot: true);
            }
            else if (_mouse is { } input)
            {
                await _host.AssignMouseAsync(_context, new MouseChord(input, slot.Modifiers), id, null);
            }

            owner.Focus(FocusState.Programmatic);
        };
        return flyout;
    }

    private void BeginCapture(BindingSlot slot, string actionId)
    {
        if (_host is null)
        {
            return;
        }

        EndCapture();
        CapturePad.Visibility = Visibility.Visible;
        string label = BindingSlots.LabelOf(_host.Registry, actionId);
        if (_key is { } vk)
        {
            _keyCapture = true;
            CaptureText.Text = $"En lugar de «{slot.Text}», pulsa la combinación nueva para «{label}»… Esc cancela";
            _host.BeginKeyCapture(this, actionId, new KeyChord(vk, slot.Modifiers));
        }
        else if (_mouse is { } input)
        {
            _mouseCapture = (actionId, new MouseChord(input, slot.Modifiers));
            _host.HoldShortcuts(true);
            CaptureText.Text = $"Para «{label}»: haz clic aquí con el botón central o un lateral, o gira o inclina la rueda, con Ctrl, Mayús o Alt si quieres. Esc cancela.";
        }

        CapturePad.Focus(FocusState.Programmatic);
    }

    private async void OnPadPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mouseCapture is not { } capture)
        {
            return;
        }

        e.Handled = true;
        PointerPointProperties properties = e.GetCurrentPoint(CapturePad).Properties;
        MouseInput input = properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => MouseInput.Left,
            PointerUpdateKind.RightButtonPressed => MouseInput.Right,
            _ => MouseShortcutHost.PressedButton(properties) ?? MouseInput.Left,
        };
        await Recorded(capture, new MouseChord(input, MouseShortcutHost.Modifiers(e.KeyModifiers)));
    }

    private async void OnPadWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_mouseCapture is not { } capture)
        {
            return;
        }

        e.Handled = true;
        bool tilt = e.GetCurrentPoint(CapturePad).Properties.IsHorizontalMouseWheel;
        await Recorded(capture, new MouseChord(tilt ? MouseInput.Tilt : MouseInput.Wheel, MouseShortcutHost.Modifiers(e.KeyModifiers)));
    }

    private async Task Recorded((string ActionId, MouseChord From) capture, MouseChord chord)
    {
        if (_host is null)
        {
            return;
        }

        if (_host.Registry.Mouse.Reason(_context, chord, capture.ActionId) is { } reason)
        {
            CaptureText.Text = $"{chord}: {reason} Prueba otra; Esc cancela.";
            return;
        }

        EndCapture();
        await _host.AssignMouseAsync(_context, chord, capture.ActionId, capture.From);
    }

    private void OnPadKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_mouseCapture is not null && e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            EndCapture();
        }
    }

    private void OnCancelCaptureClick(object sender, RoutedEventArgs e) => EndCapture();

    private void Remove(BindingSlot slot)
    {
        if (_host is null)
        {
            return;
        }

        if (_key is { } vk)
        {
            _host.ShowChange(_host.Customizer.ClearKey(new KeyChord(vk, slot.Modifiers)));
        }
        else if (_mouse is { } input)
        {
            _host.ShowChange(_host.Customizer.RemoveMouse(_context, new MouseChord(input, slot.Modifiers)));
        }
    }

    private void OnResetPartClick(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        EndCapture();
        if (_key is { } vk)
        {
            _host.ShowChange(_host.Customizer.ResetKey(vk));
        }
        else if (_mouse is { } input)
        {
            _host.ShowChange(_host.Customizer.ResetMouse(_context, input));
        }
    }

    private void OnResetContextClick(object sender, RoutedEventArgs e)
    {
        EndCapture();
        if (_host is not null)
        {
            _host.ShowChange(_host.Customizer.ResetMouseContext(_context));
        }
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        EndCapture();
        if (_host?.Customizer.UndoLast() is { } undone)
        {
            StatusText.Text = $"Se deshizo: {undone.Message}.";
        }

        Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        EndCapture();
        _host?.CloseEditor();
    }
}
