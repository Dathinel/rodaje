using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Theme;
using Rodaje.App.Views.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Mixer;

/// <summary>Lo que la tira pide al mezclador; el mezclador lo convierte en comandos.</summary>
internal interface IStripHost
{
    IReadOnlyList<AudioBus> Buses { get; }

    bool IsBypassed(string effectId);

    void SetBypassed(string effectId, bool bypassed);

    void FaderStarted(MixerStrip strip, string parameter);

    void FaderChanging(MixerStrip strip, string parameter, double value);

    void FaderCompleted(MixerStrip strip, string parameter);

    void ToggleMute(MixerStrip strip);

    void ToggleSolo(MixerStrip strip, bool exclusive);

    void ToggleArm(MixerStrip strip);

    void SetOutput(MixerStrip strip, string? busId);

    void AddSend(MixerStrip strip, string busId);

    void SendChanging(MixerStrip strip, string busId, double gainDb, bool dragging);

    void SetSendPre(MixerStrip strip, string busId, bool preFader);

    void RemoveSend(MixerStrip strip, string busId);

    void AddInsert(MixerStrip strip, string type);

    void OpenInsert(MixerStrip strip, EffectInstance effect, FrameworkElement anchor);

    void SetInsertEnabled(MixerStrip strip, EffectInstance effect, bool enabled);

    void RemoveInsert(MixerStrip strip, EffectInstance effect);

    void ApplyPreset(MixerStrip strip, EffectInstance effect, InsertPreset preset);

    void ReorderInserts(MixerStrip strip, string movedId, IReadOnlyList<string> order);

    void RenameBus(MixerStrip strip);

    void RemoveBus(MixerStrip strip);

    void OpenDynamics(MixerStrip strip);
}

/// <summary>
/// Una tira del mezclador: nombre, salida, inserciones (arrastrar para reordenar, A/B, preajustes), envíos, paneo y
/// balance, fader con escala en dB junto a su medidor de pico y RMS, y silencio, solo, grabar y modo de automatización.
/// </summary>
internal sealed partial class StripView : UserControl
{
    public const double StripWidth = 124;

    private readonly IStripHost _host;
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
    private readonly ComboBox _output = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly ListView _inserts = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        CanReorderItems = true,
        CanDragItems = true,
        AllowDrop = true,
        IsItemClickEnabled = true,
        Height = 80,
        ItemContainerStyle = CompactItem(),
    };

    private readonly StackPanel _sends = new() { Spacing = 2 };
    private readonly Button _addSend = SmallAdd("Añadir envío");
    private readonly PanControl _pan = new("Paneo");
    private readonly PanControl _balance = new("Balance");
    private readonly TextBlock _panText = new() { TextAlignment = TextAlignment.Center };
    private readonly FaderControl _fader;
    private readonly TextBlock _db = new() { TextAlignment = TextAlignment.Center };
    private readonly ToggleButton _mute = new() { Content = "M" };
    private readonly ToggleButton _solo = new() { Content = "S" };
    private readonly ToggleButton _arm = new() { Content = "R" };
    private readonly ToggleButton _touch = new() { Content = "T" };
    private bool _updating;

    /// <summary>Lo que muestra ahora el desplegable de salida: la mezcla (nulo) y cada bus por su id (ver <see cref="ComboBoxChoices"/>).</summary>
    private ChoiceList<string?> _outputs = new();
    private readonly ClosedListRetry _outputsRetry = new();
    private string _insertSignature = "";
    private string _sendSignature = "";

    public StripView(IStripHost host, MixerStrip strip)
    {
        _host = host;
        Strip = strip;
        Width = StripWidth;
        Margin = new Thickness(0, 0, 4, 0);
        var frame = new Border
        {
            Padding = new Thickness(6),
            CornerRadius = ThemeResources.Corner("ControlCornerRadius"),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeResources.Brush("StrokeBrush"),
            Background = ThemeResources.Brush(strip.Kind == StripKind.Master ? "BarBrush" : "PanelBrush"),
        };
        _fader = new FaderControl(strip.Kind == StripKind.Master ? 24 : MixerModel.FaderMaxDb);
        Meter = new LevelMeter();

        var stack = new Grid { RowSpacing = 4 };
        for (int i = 0; i < 9; i++)
        {
            stack.RowDefinitions.Add(new RowDefinition { Height = i == 7 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        }

        // Nombre con la franja del tipo: pista, bus o mezcla.
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1),
            Background = ThemeResources.Brush(strip.Kind switch { StripKind.Track => "AudioClipBrush", StripKind.Bus => "AccentBrush", _ => "TextMutedBrush" }),
        });
        _name.Style = ThemeResources.Style("LabelTextStyle");
        header.Children.Add(_name);
        Grid.SetRow(header, 0);
        stack.Children.Add(header);

        if (strip.Kind == StripKind.Track)
        {
            AutomationProperties.SetName(_output, "Salida");
            ToolTipService.SetToolTip(_output, "Adónde sale la pista: la mezcla o un bus.");
            _output.SelectionChanged += (_, _) => OnOutputSelected();
            _output.RetryWhenClosed(_outputsRetry, () => Update(Strip));
            Grid.SetRow(_output, 1);
            stack.Children.Add(_output);
        }
        else if (strip.Kind == StripKind.Bus)
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Renombrar…", () => host.RenameBus(Strip)));
            menu.Items.Add(Item("Quitar el bus", () => host.RemoveBus(Strip)));
            header.ContextFlyout = menu;
            ToolTipService.SetToolTip(header, "Bus: suma lo que le mandan las pistas. Clic derecho para renombrar o quitar.");
        }

        // Inserciones.
        var insertsPanel = new StackPanel { Spacing = 2 };
        var add = SmallAdd("Añadir efecto");
        insertsPanel.Children.Add(Section("Inserciones", add));
        _inserts.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: EffectInstance effect } item)
            {
                host.OpenInsert(Strip, effect, item);
            }
        };
        _inserts.DragItemsCompleted += (_, e) =>
        {
            if (e.Items.Count > 0 && e.Items[0] is FrameworkElement { Tag: EffectInstance moved })
            {
                List<string> order = [.. _inserts.Items.OfType<FrameworkElement>().Select(i => ((EffectInstance)i.Tag).Id)];
                host.ReorderInserts(Strip, moved.Id, order);
            }
        };
        AutomationProperties.SetName(_inserts, "Inserciones");
        ToolTipService.SetToolTip(_inserts, "Se aplican de arriba abajo. Arrastra para reordenar; clic para ajustar; clic derecho para preajustes.");
        insertsPanel.Children.Add(_inserts);
        var addMenu = new MenuFlyout();
        foreach (string type in BuiltInEffects.AudioInserts)
        {
            var item = new MenuFlyoutItem { Text = EffectUi.Name(type) };
            ToolTipService.SetToolTip(item, EffectUi.Description(type));
            item.Click += (_, _) => host.AddInsert(Strip, type);
            addMenu.Items.Add(item);
        }

        add.Flyout = addMenu;
        Grid.SetRow(insertsPanel, 2);
        stack.Children.Add(insertsPanel);

        if (strip.Kind == StripKind.Track)
        {
            var sendsPanel = new StackPanel { Spacing = 2 };
            sendsPanel.Children.Add(Section("Envíos", _addSend));
            sendsPanel.Children.Add(_sends);
            Grid.SetRow(sendsPanel, 3);
            stack.Children.Add(sendsPanel);
        }

        if (strip.Kind != StripKind.Master)
        {
            var pan = new StackPanel { Spacing = 2 };
            pan.Children.Add(_pan);
            _panText.Style = ThemeResources.Style("MicroTextStyle");
            _panText.Foreground = ThemeResources.Brush("TextMutedBrush");
            pan.Children.Add(_panText);
            pan.Children.Add(_balance);
            ToolTipService.SetToolTip(_pan, "Paneo con ley de −3 dB. Doble clic: al centro.");
            ToolTipService.SetToolTip(_balance, "Balance: baja solo el lado contrario. Doble clic: al centro.");
            Hook(_pan, BuiltInEffects.AudioParameters.Pan);
            Hook(_balance, BuiltInEffects.AudioParameters.Balance);
            Grid.SetRow(pan, 4);
            stack.Children.Add(pan);
        }

        var buttons = new Grid { ColumnSpacing = 2 };
        int column = 0;
        foreach ((ToggleButton button, string help, bool show) in new[]
        {
            (_mute, "Silenciar", strip.Kind != StripKind.Master),
            (_solo, "Solo (Ctrl: solo exclusivo)", strip.Kind != StripKind.Master),
            (_arm, "Armar para grabar: «Grabar voz» propone esta pista", strip.Kind == StripKind.Track),
            (_touch, "Automatización «tocar»: mientras suena, lo que muevas del fader o el paneo se escribe en keyframes", strip.Kind != StripKind.Master),
        })
        {
            if (!show)
            {
                continue;
            }

            button.MinWidth = 0;
            button.Padding = new Thickness(0, 2, 0, 2);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.FontSize = ThemeResources.Size("CaptionFontSize");
            ToolTipService.SetToolTip(button, help);
            AutomationProperties.SetName(button, help);
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, column++);
            buttons.Children.Add(button);
        }

        _mute.Click += (_, _) => host.ToggleMute(Strip);
        _solo.Click += (_, _) => host.ToggleSolo(Strip, (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0);
        _arm.Click += (_, _) => host.ToggleArm(Strip);
        Grid.SetRow(buttons, 5);
        stack.Children.Add(buttons);

        _db.Style = ThemeResources.Style("TimecodeTextStyle");
        _db.FontSize = ThemeResources.Size("CaptionFontSize");
        Grid.SetRow(_db, 6);
        stack.Children.Add(_db);

        var faderRow = new Grid { ColumnSpacing = 4, MinHeight = 160 };
        faderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        faderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        faderRow.Children.Add(_fader);
        Grid.SetColumn(Meter, 1);
        faderRow.Children.Add(Meter);
        Hook(_fader, BuiltInEffects.AudioParameters.GainDb);
        Grid.SetRow(faderRow, 7);
        stack.Children.Add(faderRow);

        frame.Child = stack;
        Content = frame;
        Update(strip);
    }

    public MixerStrip Strip { get; private set; }

    public LevelMeter Meter { get; }

    /// <summary>El modo «tocar» está puesto.</summary>
    public bool Touch => _touch.IsChecked == true;

    /// <summary>Algún control de la tira se está arrastrando: no se le cambia el valor desde fuera.</summary>
    public bool IsDragging => _fader.IsDragging || _pan.IsDragging || _balance.IsDragging;

    /// <summary>Si la tira se puede actualizar en el sitio (mismas inserciones, envíos y buses) o hay que rehacerla.</summary>
    public bool CanShow(MixerStrip strip) => strip.OwnerId == Strip.OwnerId && strip.Kind == Strip.Kind;

    public void Update(MixerStrip strip)
    {
        Strip = strip;
        _updating = true;
        try
        {
            _name.Text = strip.Name;
            ToolTipService.SetToolTip(_name, strip.Name);
            _mute.IsChecked = strip.Muted;
            _solo.IsChecked = strip.Solo;
            _arm.IsChecked = strip.Armed;
            if (!IsDragging)
            {
                _fader.Value = strip.GainDb;
                _pan.Value = strip.Pan;
                _balance.Value = strip.Balance;
            }

            _db.Text = MixerModel.FormatDb(_fader.Value) + " dB" + (strip.GainAutomated ? " · A" : "");
            ToolTipService.SetToolTip(_db, strip.GainAutomated ? "El volumen tiene keyframes: el fader los sigue al reproducir." : "Volumen");
            _panText.Text = MixerModel.FormatPan(_pan.Value) + (strip.PanAutomated ? " · A" : "") + (Math.Abs(strip.Balance) >= 0.5 ? " · bal " + MixerModel.FormatPan(strip.Balance) : "");
            Opacity = strip.Locked ? 0.6 : 1;
            UpdateOutput();
            UpdateInserts();
            UpdateSends();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateOutput()
    {
        if (Strip.Kind != StripKind.Track)
        {
            return;
        }

        // Antes se vaciaba y se volvía a llenar en cada refresco, también dentro de su propio SelectionChanged y con la lista
        // abierta, lo que puede cerrar la app desde WinUI sin rastro (tarea 91). Ahora solo se rehace si cambiaron los buses.
        // Una salida a un bus que ya no está se muestra como la mezcla, como antes.
        (ChoiceList<string?> next, int selected) = StripOutputChoices.Build(_host.Buses, Strip.Output);
        _outputs = ComboBoxChoices.Show(_output, _outputs, next, selected, retry: _outputsRetry);
    }

    private void OnOutputSelected()
    {
        // Elegir una salida edita y el mezclador refresca esta tira, que puede rehacer este desplegable: va después del evento.
        DispatcherQueue.Choose(_outputs, _output.SelectedIndex, _updating, busId => () => ChooseOutput(busId));
    }

    private void ChooseOutput(string? busId)
    {
        // Entre el evento y ahora pudo quitarse el bus elegido: entonces no se aplica y se refresca la tira, para que el
        // desplegable no siga mostrando la salida que no se aplicó (como hace Máscaras con su destino).
        if (DeferredChoices.StripOutput(_host.Buses, busId) == DeferredOutcome.Apply)
        {
            _host.SetOutput(Strip, busId);
        }
        else
        {
            Update(Strip);
        }
    }

    private void UpdateInserts()
    {
        string signature = string.Join("|", Strip.Inserts.Select(e => $"{e.Id}:{e.Enabled}:{InsertPresets.Match(e)?.Id}:{_host.IsBypassed(e.Id)}"));
        if (signature == _insertSignature)
        {
            return;
        }

        _insertSignature = signature;
        _inserts.Items.Clear();
        foreach (EffectInstance effect in Strip.Inserts)
        {
            _inserts.Items.Add(InsertItem(effect));
        }
    }

    private Grid InsertItem(EffectInstance effect)
    {
        var item = new Grid { Tag = effect, ColumnSpacing = 3, Padding = new Thickness(0, 1, 0, 1) };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var on = new ToggleButton
        {
            IsChecked = effect.Enabled,
            MinWidth = 0,
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = ThemeResources.Size("IconSmallFontSize") },
        };
        AutomationProperties.SetName(on, "Activar " + EffectUi.Name(effect.Type));
        ToolTipService.SetToolTip(on, "Activar o desactivar (cambia el proyecto)");
        on.Click += (_, _) => _host.SetInsertEnabled(Strip, effect, on.IsChecked == true);
        var name = new TextBlock
        {
            Text = EffectUi.Name(effect.Type),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Style = ThemeResources.Style("CaptionTextStyle"),
            Foreground = ThemeResources.Brush(effect.Enabled ? "TextBrush" : "TextDisabledBrush"),
        };
        // El nombre completo va en la ayuda de toda la fila: en una tira estrecha se acorta.
        ToolTipService.SetToolTip(item, EffectUi.Name(effect.Type) + (InsertPresets.Match(effect) is { } current ? " · " + current.Name : ""));
        var ab = new ToggleButton
        {
            Content = "A/B",
            IsChecked = _host.IsBypassed(effect.Id),
            MinWidth = 0,
            Padding = new Thickness(2, 0, 2, 0),
            FontSize = ThemeResources.Size("MicroFontSize"),
        };
        ToolTipService.SetToolTip(ab, "Escuchar sin este efecto (solo la vista previa).");
        ab.Click += (_, _) => _host.SetBypassed(effect.Id, ab.IsChecked == true);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(ab, 2);
        item.Children.Add(on);
        item.Children.Add(name);
        item.Children.Add(ab);

        var menu = new MenuFlyout();
        IReadOnlyList<InsertPreset> presets = InsertPresets.For(effect.Type);
        if (presets.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = "Preajuste" };
            foreach (InsertPreset preset in presets)
            {
                var entry = new MenuFlyoutItem { Text = preset.Name };
                ToolTipService.SetToolTip(entry, preset.Description);
                entry.Click += (_, _) => _host.ApplyPreset(Strip, effect, preset);
                sub.Items.Add(entry);
            }

            menu.Items.Add(sub);
        }

        if (effect.Type == BuiltInEffects.Compressor)
        {
            menu.Items.Add(Item("Abrir en Dinámica", () => _host.OpenDynamics(Strip)));
        }

        menu.Items.Add(Item(effect.Enabled ? "Desactivar" : "Activar", () => _host.SetInsertEnabled(Strip, effect, !effect.Enabled)));
        menu.Items.Add(Item("Quitar", () => _host.RemoveInsert(Strip, effect)));
        item.ContextFlyout = menu;
        return item;
    }

    private void UpdateSends()
    {
        if (Strip.Kind != StripKind.Track)
        {
            return;
        }

        string signature = string.Join("|", Strip.Sends.Select(s => $"{s.BusId}:{s.PreFader}:{s.Muted}")) + "/" + string.Join("|", _host.Buses.Select(b => b.Id + b.Name));
        if (signature != _sendSignature)
        {
            _sendSignature = signature;
            _sends.Children.Clear();
            foreach (AudioSend send in Strip.Sends)
            {
                if (_host.Buses.FirstOrDefault(b => b.Id == send.BusId) is { } bus)
                {
                    _sends.Children.Add(SendRow(send, bus));
                }
            }

            var menu = new MenuFlyout();
            foreach (AudioBus bus in _host.Buses.Where(b => !Strip.Sends.Any(s => s.BusId == b.Id)))
            {
                string id = bus.Id;
                menu.Items.Add(Item("A " + bus.Name, () => _host.AddSend(Strip, id)));
            }

            _addSend.Flyout = menu.Items.Count > 0 ? menu : null;
            _addSend.IsEnabled = menu.Items.Count > 0;
            ToolTipService.SetToolTip(_addSend, _host.Buses.Count == 0
                ? "Primero crea un bus (arriba, «Añadir bus»)."
                : "Manda una copia de la pista a un bus (reverb, delay…) sin cambiar su salida.");
        }

        foreach (FrameworkElement row in _sends.Children.OfType<FrameworkElement>())
        {
            if (row.Tag is string busId && Strip.Sends.FirstOrDefault(s => s.BusId == busId) is { } send && row is Grid grid
                && grid.Children.OfType<Slider>().FirstOrDefault() is { } slider && !_draggingSend.Contains(busId))
            {
                slider.Value = send.GainDb;
            }
        }
    }

    private readonly HashSet<string> _draggingSend = new(StringComparer.Ordinal);

    private Grid SendRow(AudioSend send, AudioBus bus)
    {
        var row = new Grid { Tag = send.BusId, ColumnSpacing = 2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = bus.Name, Style = ThemeResources.Style("MicroTextStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
        var pre = new ToggleButton { Content = "Pre", IsChecked = send.PreFader, MinWidth = 0, Padding = new Thickness(3, 0, 3, 0), FontSize = ThemeResources.Size("MicroFontSize") };
        ToolTipService.SetToolTip(pre, "Antes del fader: lo que se manda no depende del volumen de la pista.");
        string busId = send.BusId;
        pre.Click += (_, _) => _host.SetSendPre(Strip, busId, pre.IsChecked == true);
        var level = new Slider
        {
            Minimum = AudioSend.MinGainDb,
            Maximum = AudioSend.MaxGainDb,
            StepFrequency = 0.5,
            Value = send.GainDb,
            Margin = new Thickness(0, -6, 0, -6),
        };
        AutomationProperties.SetName(level, "Envío a " + bus.Name);
        ToolTipService.SetToolTip(level, "Nivel del envío a " + bus.Name + " (dB).");
        level.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _draggingSend.Add(busId)), true);
        void Release()
        {
            if (_draggingSend.Remove(busId))
            {
                _host.SendChanging(Strip, busId, level.Value, dragging: false);
            }
        }

        level.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Release()), true);
        level.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Release()), true);
        level.ValueChanged += (_, e) =>
        {
            if (!_updating)
            {
                _host.SendChanging(Strip, busId, e.NewValue, _draggingSend.Contains(busId));
            }
        };
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Quitar el envío", () => _host.RemoveSend(Strip, busId)));
        row.ContextFlyout = menu;
        Grid.SetColumn(pre, 1);
        Grid.SetRow(level, 1);
        Grid.SetColumnSpan(level, 2);
        row.Children.Add(name);
        row.Children.Add(pre);
        row.Children.Add(level);
        return row;
    }

    private void Hook(DragControl control, string parameter)
    {
        control.DragStarted += (_, _) => _host.FaderStarted(Strip, parameter);
        control.ValueChanging += (_, value) =>
        {
            _host.FaderChanging(Strip, parameter, value);
            if (control == _fader)
            {
                _db.Text = MixerModel.FormatDb(value) + " dB";
            }
            else if (control == _pan)
            {
                _panText.Text = MixerModel.FormatPan(value);
            }
        };
        control.DragCompleted += (_, _) => _host.FaderCompleted(Strip, parameter);
    }

    /// <summary>Filas de inserción bajas: el estilo de fábrica de la lista deja 40 px por fila.</summary>
    private static Style CompactItem()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(MinHeightProperty, 24.0));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(2, 0, 2, 0)));
        style.Setters.Add(new Setter(MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    /// <summary>El título de una sección con su botón «+» a la derecha.</summary>
    private static Grid Section(string title, Button add)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock caption = Caption(title);
        caption.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(caption);
        Grid.SetColumn(add, 1);
        row.Children.Add(add);
        return row;
    }

    private static Button SmallAdd(string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = ThemeResources.Size("IconSmallFontSize") },
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 0,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = ThemeResources.Style("MicroTextStyle"),
        Foreground = ThemeResources.Brush("TextMutedBrush"),
    };

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }
}
