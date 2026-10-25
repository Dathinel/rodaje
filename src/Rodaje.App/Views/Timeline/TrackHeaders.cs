using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Audio;
using Rodaje.App.Theme;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Timeline;

/// <summary>Qué interruptor de pista se tocó.</summary>
internal enum TrackToggle
{
    Hidden,
    Muted,
    Locked,
}

/// <summary>
/// Las cabeceras de pista: nombre, tipo («principal · magnética» o «libre») y los interruptores de ocultar, silenciar y
/// bloquear. Se rearman solo cuando cambian las pistas o su estado, no al editar clips. Las de audio muestran el volumen
/// de la pista: arrastrarlo a los lados lo cambia (0,25 dB por píxel, con Mayús 0,05) y el doble clic lo vuelve a 0 dB.
/// </summary>
internal sealed class TrackHeaders
{
    private readonly Canvas _canvas;
    private readonly TimelineBrushes _brushes;
    private readonly Style _toggleStyle;
    private readonly TranslateTransform _shift = new();
    private const double DbPerPixel = 0.25;
    private const double FineDbPerPixel = 0.05;

    private readonly Dictionary<string, TextBlock> _gains = new(StringComparer.Ordinal);
    private string _signature = "";
    private readonly Dictionary<string, Grid> _grids = new(StringComparer.Ordinal);
    private double _lastWidth;
    private TrackLayout? _lastLayout;
    private Func<Track, double>? _gainOf;
    private (string TrackId, double StartX, double StartDb)? _gainDrag;
    private (string TrackId, double StartY, int? Index)? _reorderDrag;
    private Rectangle? _dropLine;
    private const double ReorderThreshold = 4;

    public TrackHeaders(Canvas canvas, TimelineBrushes brushes, Style toggleStyle)
    {
        _canvas = canvas;
        _brushes = brushes;
        _toggleStyle = toggleStyle;
        _canvas.RenderTransform = _shift;
    }

    /// <summary>La persona tocó un interruptor de una pista.</summary>
    public event Action<string, TrackToggle>? Toggled;

    /// <summary>Se arrastra el volumen de una pista (dB); cada movimiento llega aquí.</summary>
    public event Action<string, double>? GainChanging;

    /// <summary>Se soltó el volumen de una pista (o se restableció): termina el paso del historial.</summary>
    public event Action<string>? GainCommitted;

    /// <summary>Del menú de una cabecera: «Añadir pista de subtítulos».</summary>
    public event Action? SubtitleTrackRequested;

    /// <summary>Del menú de una cabecera de video: usarla (o dejar de usarla) como pista de subtítulos.</summary>
    public event Action<string, bool>? SubtitleRoleRequested;

    /// <summary>Del menú de una cabecera de subtítulos: importar un archivo (null) o exportar la pista.</summary>
    public event Action<string?>? SubtitleFileRequested;

    /// <summary>Del menú de una cabecera de audio: un preajuste de ecualizador y compresor para la pista (nulo: quitarlos).</summary>
    public event Action<string, AudioPreset?>? AudioPresetRequested;

    /// <summary>El botón del micrófono (o el menú) de una cabecera de audio: grabar voz en off en esa pista.</summary>
    public event Action<string>? VoiceOverRequested;

    /// <summary>«Compresor de la pista…»: abre la pestaña Dinámica con el compresor de esa pista.</summary>
    public event Action<string>? DynamicsRequested;

    /// <summary>Se arrastra el borde de abajo de una cabecera (o se elige en su menú): el alto nuevo, o nulo para el de siempre.</summary>
    public event Action<string, int?>? HeightChanging;

    /// <summary>Se soltó el borde: termina el paso del historial.</summary>
    public event Action<string>? HeightCommitted;

    /// <summary>Del menú de una cabecera: cerrar todos los huecos de la pista.</summary>
    public event Action<string>? CloseGapsRequested;

    /// <summary>Se tocó una cabecera: queda seleccionada (Supr la elimina).</summary>
    public event Action<string>? TrackSelected;

    /// <summary>Del menú de una cabecera: eliminar la pista.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>Del menú de una cabecera: subirla (true) o bajarla un lugar.</summary>
    public event Action<string, bool>? StepRequested;

    /// <summary>Se soltó una cabecera arrastrada: la posición nueva dentro de su tipo.</summary>
    public event Action<string, int>? ReorderRequested;

    /// <summary>Si la pista puede subir (true) o bajar; la da la vista, que conoce la secuencia.</summary>
    public Func<string, bool, bool>? CanStep { get; set; }

    /// <summary>La pista cuya cabecera está seleccionada, si hay.</summary>
    public string? SelectedTrackId { get; private set; }

    public double ScrollY
    {
        get => -_shift.Y;
        set => _shift.Y = -value;
    }

    /// <summary>El volumen en dB de una pista que se muestra en su cabecera (el del cursor, si tiene keyframes).</summary>
    public void RefreshGains(Func<Track, double> gainOf, TrackLayout layout)
    {
        _gainOf = gainOf;
        foreach (TrackRow row in layout.Rows)
        {
            if (_gains.TryGetValue(row.TrackId, out TextBlock? text))
            {
                text.Text = GainText(gainOf(row.Track));
            }
        }
    }

    /// <summary>Selecciona la cabecera de una pista (o ninguna) y la redibuja.</summary>
    public void Select(string? trackId)
    {
        if (SelectedTrackId == trackId)
        {
            return;
        }

        // Solo el fondo: rearmar quitaría la cabecera que se está tocando y cortaría su arrastre.
        string? previous = SelectedTrackId;
        SelectedTrackId = trackId;
        foreach (string? id in (string?[])[previous, trackId])
        {
            if (id is not null && _grids.TryGetValue(id, out Grid? grid) && _lastLayout?.Find(id) is { } row)
            {
                grid.Background = HeaderBackground(row);
            }
        }
    }

    private Brush HeaderBackground(TrackRow row) =>
        row.TrackId == SelectedTrackId ? _brushes.AccentSubtle : row.IsMain ? _brushes.Control : _brushes.Panel;

    public void Update(TrackLayout layout, bool magnetic, double width)
    {
        if (SelectedTrackId is { } selected && layout.Find(selected) is null)
        {
            SelectedTrackId = null;
        }

        string signature = string.Join("|", layout.Rows.Select(r =>
            $"{r.TrackId}:{r.Track.Name}:{r.Track.Role}:{r.Track.Hidden}:{r.Track.Muted}:{r.Track.Locked}:{r.IsMain}:{r.Top}:{r.Height}")) + $"|{magnetic}|{width}";
        _lastLayout = layout;
        _lastWidth = width;
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _canvas.Children.Clear();
        _gains.Clear();
        _grids.Clear();
        foreach (TrackRow row in layout.Rows)
        {
            _canvas.Children.Add(Build(row, magnetic, width));
        }
    }

    private Grid Build(TrackRow row, bool magnetic, double width)
    {
        Track track = row.Track;
        var grid = new Grid
        {
            Width = width,
            Height = row.Height,
            Background = HeaderBackground(row),
            BorderBrush = _brushes.Stroke,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(10, 0, 4, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Canvas.SetTop(grid, row.Top);

        if (row.IsMain)
        {
            var bar = new Rectangle { Width = 3, Fill = magnetic ? _brushes.Accent : _brushes.TextDisabled, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 0, 0, 0) };
            grid.Children.Add(bar);
        }

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        labels.Children.Add(new TextBlock
        {
            Text = track.Name,
            FontSize = ThemeResources.TimelineSize("BodyFontSize"),
            FontWeight = row.IsMain ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = track.Hidden || track.Muted ? _brushes.TextMuted : _brushes.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (row.Kind == TrackKind.Audio)
        {
            labels.Children.Add(GainControl(track));
        }
        else
        {
            labels.Children.Add(new TextBlock
            {
                Text = Caption(row, magnetic),
                FontSize = ThemeResources.TimelineSize("MicroFontSize"),
                Foreground = row.IsMain && magnetic ? _brushes.Accent : _brushes.TextMuted,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        grid.Children.Add(labels);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(toggles, 1);
        if (track.Kind == TrackKind.Video)
        {
            toggles.Children.Add(Toggle(track, TrackToggle.Hidden, track.Hidden, track.Hidden ? "" : "", track.Hidden ? "Mostrar pista" : "Ocultar pista"));
        }
        else
        {
            var record = new ToggleButton
            {
                Content = new FontIcon { Glyph = "\uE720", FontSize = ThemeResources.Size("IconFontSize") },
                Style = _toggleStyle,
                IsEnabled = !track.Locked,
            };
            ToolTipService.SetToolTip(record, "Grabar voz en off en esta pista");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(record, "Grabar voz en off en esta pista");
            string recordTrack = track.Id;
            record.Click += (_, _) =>
            {
                record.IsChecked = false;
                VoiceOverRequested?.Invoke(recordTrack);
            };
            toggles.Children.Add(record);
            toggles.Children.Add(Toggle(track, TrackToggle.Muted, track.Muted, track.Muted ? "" : "", track.Muted ? "Activar sonido" : "Silenciar pista"));
        }

        toggles.Children.Add(Toggle(track, TrackToggle.Locked, track.Locked, track.Locked ? "" : "", track.Locked ? "Desbloquear pista" : "Bloquear pista"));
        grid.Children.Add(toggles);
        grid.Children.Add(HeightGrip(row));
        grid.ContextFlyout = Menu(row);
        HookReorder(grid, row);
        _grids[row.TrackId] = grid;
        return grid;
    }

    /// <summary>Tocar la cabecera la selecciona; arrastrarla en vertical la lleva a otra posición entre las de su tipo.</summary>
    private void HookReorder(Grid grid, TrackRow row)
    {
        string trackId = row.TrackId;
        bool movable = !row.IsMain && !row.Track.Locked;
        grid.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            {
                return;
            }

            TrackSelected?.Invoke(trackId);
            if (movable && grid.CapturePointer(e.Pointer))
            {
                _reorderDrag = (trackId, e.GetCurrentPoint(_canvas).Position.Y, null);
            }

            e.Handled = true;
        };
        grid.PointerMoved += (_, e) =>
        {
            if (_reorderDrag is not { } drag || drag.TrackId != trackId || _lastLayout is not { } layout)
            {
                return;
            }

            double y = e.GetCurrentPoint(_canvas).Position.Y;
            if (drag.Index is null && Math.Abs(y - drag.StartY) < ReorderThreshold)
            {
                return;
            }

            (int Index, double LineY)? slot = layout.ReorderSlot(trackId, y);
            _reorderDrag = drag with { Index = slot?.Index ?? -1 };
            ShowDropLine(slot is { } s && s.Index != layout.IndexInKind(trackId) ? s.LineY : null);
            e.Handled = true;
        };
        void End(PointerRoutedEventArgs e, bool commit)
        {
            if (_reorderDrag is not { } drag || drag.TrackId != trackId)
            {
                return;
            }

            _reorderDrag = null;
            grid.ReleasePointerCaptures();
            ShowDropLine(null);
            if (commit && drag.Index is int index and >= 0 && index != _lastLayout?.IndexInKind(trackId))
            {
                ReorderRequested?.Invoke(trackId, index);
            }

            e.Handled = true;
        }

        grid.PointerReleased += (_, e) => End(e, commit: true);
        grid.PointerCaptureLost += (_, e) => End(e, commit: false);
    }

    private void ShowDropLine(double? y)
    {
        if (y is not { } top)
        {
            if (_dropLine is not null)
            {
                _dropLine.Visibility = Visibility.Collapsed;
            }

            return;
        }

        _dropLine ??= new Rectangle { Height = 2, Fill = _brushes.Accent, IsHitTestVisible = false };
        if (!_canvas.Children.Contains(_dropLine))
        {
            _canvas.Children.Add(_dropLine);
        }

        _dropLine.Width = _lastWidth;
        _dropLine.Visibility = Visibility.Visible;
        Canvas.SetTop(_dropLine, top - 1);
    }

    /// <summary>Franja del borde de abajo: arrastrarla cambia el alto de la pista (doble clic: el de siempre).</summary>
    private Border HeightGrip(TrackRow row)
    {
        var grip = new Border
        {
            Height = 5,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-10, 0, -4, -2),
            Background = _brushes.Transparent,
        };
        Grid.SetColumnSpan(grip, 2);
        ToolTipService.SetToolTip(grip, "Arrastra para cambiar el alto de la pista; doble clic, el de siempre");
        string trackId = row.TrackId;
        double startY = 0;
        double startHeight = row.Height;
        bool dragging = false;
        grip.PointerEntered += (_, _) => grip.Background = _brushes.Accent;
        grip.PointerExited += (_, _) =>
        {
            if (!dragging)
            {
                grip.Background = _brushes.Transparent;
            }
        };
        grip.PointerPressed += (_, e) =>
        {
            dragging = grip.CapturePointer(e.Pointer);
            startY = e.GetCurrentPoint(_canvas).Position.Y;
            startHeight = row.Height;
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (dragging)
            {
                double height = startHeight + e.GetCurrentPoint(_canvas).Position.Y - startY;
                HeightChanging?.Invoke(trackId, (int)Math.Round(Math.Clamp(height, Track.MinHeight, Track.MaxHeight)));
                e.Handled = true;
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            if (dragging)
            {
                dragging = false;
                grip.ReleasePointerCapture(e.Pointer);
                grip.Background = _brushes.Transparent;
                HeightCommitted?.Invoke(trackId);
                e.Handled = true;
            }
        };
        grip.DoubleTapped += (_, e) =>
        {
            HeightChanging?.Invoke(trackId, null);
            HeightCommitted?.Invoke(trackId);
            e.Handled = true;
        };
        return grip;
    }

    private static readonly (string Text, int? Height)[] Heights = [("Compacta", 32), ("Normal", null), ("Alta", 96), ("Muy alta", 160)];

    private MenuFlyout Menu(TrackRow row)
    {
        var menu = new MenuFlyout();
        var height = new MenuFlyoutSubItem { Text = "Alto de la pista", Icon = new FontIcon { Glyph = "" } };
        foreach ((string text, int? value) in Heights)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "alto-" + row.TrackId, IsChecked = row.Track.Height == value };
            item.Click += (_, _) =>
            {
                HeightChanging?.Invoke(row.TrackId, value);
                HeightCommitted?.Invoke(row.TrackId);
            };
            height.Items.Add(item);
        }

        menu.Items.Add(height);
        AddOrderItems(menu, row);
        var gaps = new MenuFlyoutItem { Text = "Cerrar todos los huecos de la pista", Icon = new FontIcon { Glyph = "" }, IsEnabled = !row.Track.Locked };
        gaps.Click += (_, _) => CloseGapsRequested?.Invoke(row.TrackId);
        menu.Items.Add(gaps);
        menu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Añadir pista de subtítulos", Icon = new FontIcon { Glyph = "" } };
        add.Click += (_, _) => SubtitleTrackRequested?.Invoke();
        menu.Items.Add(add);
        if (row.Kind == TrackKind.Video && !row.IsMain)
        {
            bool subtitles = row.Track.Role == TrackRoles.Subtitles;
            var role = new ToggleMenuFlyoutItem { Text = "Usar para subtítulos", IsChecked = subtitles, IsEnabled = !row.Track.Locked };
            string trackId = row.TrackId;
            role.Click += (_, _) => SubtitleRoleRequested?.Invoke(trackId, !subtitles);
            menu.Items.Add(role);
            if (subtitles)
            {
                var import = new MenuFlyoutItem { Text = Subtitles.SubtitleFileCommands.ImportLabel, Icon = new FontIcon { Glyph = "" } };
                import.Click += (_, _) => SubtitleFileRequested?.Invoke(null);
                var export = new MenuFlyoutItem { Text = Subtitles.SubtitleFileCommands.ExportLabel, Icon = new FontIcon { Glyph = "" } };
                export.Click += (_, _) => SubtitleFileRequested?.Invoke(trackId);
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(import);
                menu.Items.Add(export);
            }
        }

        if (row.Kind == TrackKind.Audio)
        {
            var effects = new MenuFlyoutSubItem { Text = "Preajuste de audio", Icon = new FontIcon { Glyph = "\uE9E9" } };
            var dynamics = new MenuFlyoutItem { Text = "Compresor de la pista\u2026" };
            string dynamicsTrack = row.TrackId;
            dynamics.Click += (_, _) => DynamicsRequested?.Invoke(dynamicsTrack);
            var voice = new MenuFlyoutItem { Text = "Grabar voz en off aquí…", Icon = new FontIcon { Glyph = "\uE720" }, IsEnabled = !row.Track.Locked };
            string voiceTrack = row.TrackId;
            voice.Click += (_, _) => VoiceOverRequested?.Invoke(voiceTrack);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(voice);
            menu.Items.Add(effects);
            menu.Items.Add(dynamics);
            Track track = row.Track;

            // Se arma al abrir: la cabecera no se rehace cuando cambian los efectos de la pista.
            menu.Opening += (_, _) =>
            {
                effects.Items.Clear();
                effects.IsEnabled = !track.Locked;
                AudioPreset? current = AudioPresetCommands.Match(track.Effects, out bool has);
                effects.Items.Add(PresetItem(track.Id, null, "Sin ecualizador ni compresor", !has));
                effects.Items.Add(new MenuFlyoutSeparator());
                foreach (AudioPreset preset in AudioPresets.All)
                {
                    effects.Items.Add(PresetItem(track.Id, preset, preset.Name, ReferenceEquals(preset, current)));
                }

                if (has && current is null)
                {
                    effects.Items.Add(new RadioMenuFlyoutItem { Text = "Personalizado", IsChecked = true, IsEnabled = false, GroupName = "preajuste" });
                }
            };
        }

        return menu;
    }

    private void AddOrderItems(MenuFlyout menu, TrackRow row)
    {
        string trackId = row.TrackId;
        var up = new MenuFlyoutItem { Text = "Subir pista", Icon = new FontIcon { Glyph = "\uE74A" } };
        var down = new MenuFlyoutItem { Text = "Bajar pista", Icon = new FontIcon { Glyph = "\uE74B" } };
        var delete = new MenuFlyoutItem { Text = "Eliminar pista", Icon = new FontIcon { Glyph = "\uE74D" }, IsEnabled = !row.IsMain && !row.Track.Locked };
        if (row.IsMain)
        {
            ToolTipService.SetToolTip(delete, "La pista principal no se elimina");
        }

        up.Click += (_, _) => StepRequested?.Invoke(trackId, true);
        down.Click += (_, _) => StepRequested?.Invoke(trackId, false);
        delete.Click += (_, _) => DeleteRequested?.Invoke(trackId);
        menu.Opening += (_, _) =>
        {
            up.IsEnabled = !row.Track.Locked && CanStep?.Invoke(trackId, true) == true;
            down.IsEnabled = !row.Track.Locked && CanStep?.Invoke(trackId, false) == true;
        };
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(delete);
        menu.Items.Add(new MenuFlyoutSeparator());
    }

    private RadioMenuFlyoutItem PresetItem(string trackId, AudioPreset? preset, string text, bool isChecked)
    {
        var item = new RadioMenuFlyoutItem { Text = text, IsChecked = isChecked, GroupName = "preajuste" };
        if (preset is not null)
        {
            ToolTipService.SetToolTip(item, preset.Description);
        }

        item.Click += (_, _) => AudioPresetRequested?.Invoke(trackId, preset);
        return item;
    }

    private TextBlock GainControl(Track track)
    {
        var text = new TextBlock
        {
            Text = GainText(_gainOf?.Invoke(track) ?? 0),
            FontSize = ThemeResources.TimelineSize("MicroFontSize"),
            Foreground = track.Locked ? _brushes.TextDisabled : _brushes.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 0, 8, 0),
        };
        ToolTipService.SetToolTip(text, "Volumen de la pista: arrastra a los lados (Mayús, más fino); doble clic, 0 dB");
        string trackId = track.Id;
        _gains[trackId] = text;
        if (track.Locked)
        {
            return text;
        }

        text.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(text).Properties.IsLeftButtonPressed || !text.CapturePointer(e.Pointer))
            {
                return;
            }

            double current = _gainOf is { } gainOf && FindTrack(trackId) is { } t ? gainOf(t) : 0;
            _gainDrag = (trackId, e.GetCurrentPoint(_canvas).Position.X, current);
            e.Handled = true;
        };
        text.PointerMoved += (_, e) =>
        {
            if (_gainDrag is not { } drag || drag.TrackId != trackId)
            {
                return;
            }

            double step = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? FineDbPerPixel : DbPerPixel;
            double db = Math.Round(drag.StartDb + ((e.GetCurrentPoint(_canvas).Position.X - drag.StartX) * step), 1);
            GainChanging?.Invoke(trackId, Math.Clamp(db, ClipDecorations.MinEnvelopeDb, 24));
            e.Handled = true;
        };
        void End(PointerRoutedEventArgs e)
        {
            if (_gainDrag is { } drag && drag.TrackId == trackId)
            {
                _gainDrag = null;
                text.ReleasePointerCaptures();
                GainCommitted?.Invoke(trackId);
                e.Handled = true;
            }
        }

        text.PointerReleased += (_, e) => End(e);
        text.PointerCaptureLost += (_, e) => End(e);
        text.DoubleTapped += (_, e) =>
        {
            GainChanging?.Invoke(trackId, 0);
            GainCommitted?.Invoke(trackId);
            e.Handled = true;
        };
        return text;
    }

    private Track? FindTrack(string trackId) => _lastLayout?.Find(trackId)?.Track;

    private static string GainText(double db)
    {
        string value = db <= ClipDecorations.MinEnvelopeDb ? "−∞" : (db > 0 ? "+" : db < 0 ? "−" : "") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        return $"Volumen {value} dB";
    }

    private static string Caption(TrackRow row, bool magnetic)
    {
        if (row.Kind == TrackKind.Audio)
        {
            return row.Track.Locked ? "Audio · bloqueada" : "Audio";
        }

        string kind = row.IsMain ? (magnetic ? "Principal · magnética" : "Principal · libre")
            : row.Track.Role == TrackRoles.Subtitles ? "Subtítulos" : "Video · libre";
        return row.Track.Locked ? kind + " · bloqueada" : kind;
    }

    private ToggleButton Toggle(Track track, TrackToggle which, bool isOn, string glyph, string tip)
    {
        var button = new ToggleButton
        {
            Style = _toggleStyle,
            Width = 24,
            Height = 24,
            FontSize = ThemeResources.TimelineSize("BodyFontSize"),
            Content = glyph,
            IsChecked = isOn,
        };
        TimelineView.KeepOffFocus(button);
        ToolTipService.SetToolTip(button, tip);
        string trackId = track.Id;
        button.Click += (_, _) => Toggled?.Invoke(trackId, which);
        return button;
    }
}
