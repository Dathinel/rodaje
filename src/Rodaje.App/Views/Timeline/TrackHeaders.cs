using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Audio;
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
    private TrackLayout? _lastLayout;
    private Func<Track, double>? _gainOf;
    private (string TrackId, double StartX, double StartDb)? _gainDrag;

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

    /// <summary>Del menú de una cabecera de audio: un preajuste de ecualizador y compresor para la pista (nulo: quitarlos).</summary>
    public event Action<string, AudioPreset?>? AudioPresetRequested;

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

    public void Update(TrackLayout layout, bool magnetic, double width)
    {
        string signature = string.Join("|", layout.Rows.Select(r =>
            $"{r.TrackId}:{r.Track.Name}:{r.Track.Role}:{r.Track.Hidden}:{r.Track.Muted}:{r.Track.Locked}:{r.IsMain}:{r.Top}:{r.Height}")) + $"|{magnetic}|{width}";
        _lastLayout = layout;
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _canvas.Children.Clear();
        _gains.Clear();
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
            Background = row.IsMain ? _brushes.Control : _brushes.Panel,
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
            FontSize = 12,
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
                FontSize = 10,
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
            toggles.Children.Add(Toggle(track, TrackToggle.Muted, track.Muted, track.Muted ? "" : "", track.Muted ? "Activar sonido" : "Silenciar pista"));
        }

        toggles.Children.Add(Toggle(track, TrackToggle.Locked, track.Locked, track.Locked ? "" : "", track.Locked ? "Desbloquear pista" : "Bloquear pista"));
        grid.Children.Add(toggles);
        grid.ContextFlyout = Menu(row);
        return grid;
    }

    private MenuFlyout Menu(TrackRow row)
    {
        var menu = new MenuFlyout();
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
        }

        if (row.Kind == TrackKind.Audio)
        {
            var effects = new MenuFlyoutSubItem { Text = "Ecualizador y compresor", Icon = new FontIcon { Glyph = "\uE9E9" } };
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(effects);
            Track track = row.Track;

            // Se arma al abrir: la cabecera no se rehace cuando cambian los efectos de la pista.
            menu.Opening += (_, _) =>
            {
                effects.Items.Clear();
                effects.IsEnabled = !track.Locked;
                AudioPreset? current = AudioPresetCommands.Match(track.Effects, out bool has);
                effects.Items.Add(PresetItem(track.Id, null, "Sin ecualizador ni compresor", !has));
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
            FontSize = 10,
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
            FontSize = 12,
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
