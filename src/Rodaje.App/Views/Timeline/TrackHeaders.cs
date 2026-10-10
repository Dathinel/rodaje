using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
/// bloquear. Se rearman solo cuando cambian las pistas o su estado, no al editar clips.
/// </summary>
internal sealed class TrackHeaders
{
    private readonly Canvas _canvas;
    private readonly TimelineBrushes _brushes;
    private readonly Style _toggleStyle;
    private readonly TranslateTransform _shift = new();
    private string _signature = "";

    public TrackHeaders(Canvas canvas, TimelineBrushes brushes, Style toggleStyle)
    {
        _canvas = canvas;
        _brushes = brushes;
        _toggleStyle = toggleStyle;
        _canvas.RenderTransform = _shift;
    }

    /// <summary>La persona tocó un interruptor de una pista.</summary>
    public event Action<string, TrackToggle>? Toggled;

    public double ScrollY
    {
        get => -_shift.Y;
        set => _shift.Y = -value;
    }

    public void Update(TrackLayout layout, bool magnetic, double width)
    {
        string signature = string.Join("|", layout.Rows.Select(r =>
            $"{r.TrackId}:{r.Track.Name}:{r.Track.Hidden}:{r.Track.Muted}:{r.Track.Locked}:{r.IsMain}:{r.Top}:{r.Height}")) + $"|{magnetic}|{width}";
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _canvas.Children.Clear();
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
        labels.Children.Add(new TextBlock
        {
            Text = Caption(row, magnetic),
            FontSize = 10,
            Foreground = row.IsMain && magnetic ? _brushes.Accent : _brushes.TextMuted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
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
        return grid;
    }

    private static string Caption(TrackRow row, bool magnetic)
    {
        if (row.Kind == TrackKind.Audio)
        {
            return row.Track.Locked ? "Audio · bloqueada" : "Audio";
        }

        string kind = row.IsMain ? (magnetic ? "Principal · magnética" : "Principal · libre") : "Video · libre";
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
