using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Shell;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Markers;

/// <summary>Una fila de la lista: un marcador o una región tal como se muestra.</summary>
internal sealed partial class MarkerItem
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);

    public MarkerItem(Marker marker)
    {
        Key = "m:" + marker.Id;
        Id = marker.Id;
        IsRegion = false;
        Name = marker.Name.Length == 0 ? "(sin nombre)" : marker.Name;
        At = marker.Time;
        TimeText = Timecode.Format(marker.Time);
        Color = marker.Color;
        Note = marker.Note;
    }

    public MarkerItem(Region region)
    {
        Key = "r:" + region.Id;
        Id = region.Id;
        IsRegion = true;
        Name = region.Name.Length == 0 ? "(sin nombre)" : region.Name;
        At = region.Start;
        TimeText = $"{Timecode.Format(region.Start)} – {Timecode.Format(region.End)}";
        Color = region.Color;
        Note = region.Note;
    }

    /// <summary>Marcadores y regiones comparten la lista: la clave distingue los dos.</summary>
    public string Key { get; }

    public string Id { get; }

    public bool IsRegion { get; }

    public string Name { get; }

    public Time At { get; }

    public string TimeText { get; }

    public string? Color { get; }

    public string? Note { get; }

    public Brush Brush => BrushFor(Color, IsRegion);

    public Visibility MarkerVisibility => IsRegion ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RegionVisibility => IsRegion ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoteVisibility => Note is null ? Visibility.Collapsed : Visibility.Visible;

    public string Tip => Note is { } note ? $"{note}\n\nDoble clic para ir ahí" : "Doble clic para ir ahí";

    public bool SameAs(MarkerItem other) =>
        Key == other.Key && Name == other.Name && TimeText == other.TimeText && Color == other.Color && Note == other.Note;

    /// <summary>El pincel de un color <c>#RRGGBB</c>, o el de siempre de los marcadores o las regiones.</summary>
    public static Brush BrushFor(string? color, bool region) =>
        color is not null && MarkerStyle.IsColor(color) ? Solid(color) : (Brush)Application.Current.Resources[region ? "AccentBrush" : "KeyframeBrush"];

    private static SolidColorBrush Solid(string color)
    {
        if (!Brushes.TryGetValue(color, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(ColorHelper.FromArgb(0xFF, Convert.ToByte(color[1..3], 16), Convert.ToByte(color[3..5], 16), Convert.ToByte(color[5..7], 16)));
            Brushes[color] = brush;
        }

        return brush;
    }
}

/// <summary>
/// Marcadores y regiones: doble clic va ahí; editar (nombre, color y nota) y quitar son pasos del historial.
/// </summary>
/// <remarks>Mientras el panel está oculto no se rehace la lista; se rehace al verse.</remarks>
internal sealed partial class MarkersPanel : UserControl, IEditorView
{
    /// <summary>Los colores que se ofrecen, con su nombre; nulo es el de siempre.</summary>
    public static readonly IReadOnlyList<(string Name, string? Hex)> Palette =
    [
        ("Por defecto", null),
        ("Rojo", "#E5484D"),
        ("Naranja", "#F76B15"),
        ("Amarillo", "#FFC53D"),
        ("Verde", "#46A758"),
        ("Turquesa", "#12A594"),
        ("Azul", "#3E63DD"),
        ("Violeta", "#8E4EC6"),
        ("Rosa", "#D6409F"),
    ];

    private readonly MenuFlyout _menu = new();
    private EditorServices? _services;
    private EditorSession? _session;
    private MarkerItem? _menuTarget;
    private bool _stale;

    public MarkersPanel()
    {
        InitializeComponent();
        _menu.Items.Add(MenuItem("Ir ahí", "", (_, _) => GoTo(_menuTarget)));
        _menu.Items.Add(MenuItem("Editar…", "", (_, _) => UiTask.Run(EditAsync(_menuTarget?.Key))));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(MenuItem("Quitar", "", (_, _) => Remove(_menuTarget?.Key)));
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (_stale && Visibility == Visibility.Visible)
            {
                Refresh();
            }
        });
    }

    public ObservableCollection<MarkerItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _session = services.Session;
        _session.ProjectOpened += (_, _) => Refresh();
        _session.ProjectEdited += (_, _) => Refresh();
        Refresh();
        Bindings.Update();
    }

    /// <summary>Marcadores y regiones en orden de tiempo (a igual tiempo, primero la región).</summary>
    public static List<MarkerItem> ItemsOf(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return [.. sequence.Regions.Select(r => new MarkerItem(r))
            .Concat(sequence.Markers.Select(m => new MarkerItem(m)))
            .OrderBy(i => i.At)
            .ThenBy(i => i.IsRegion ? 0 : 1)];
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += click;
        return item;
    }

    private void Refresh()
    {
        if (_session is null || _session.IsInGesture)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            _stale = true;
            return;
        }

        _stale = false;
        List<MarkerItem> next = ItemsOf(_session.Sequence);
        while (Items.Count > next.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        for (int i = 0; i < next.Count; i++)
        {
            if (i == Items.Count)
            {
                Items.Add(next[i]);
            }
            else if (!Items[i].SameAs(next[i]))
            {
                Items[i] = next[i];
            }
        }

        int markers = _session.Sequence.Markers.Count;
        int regions = _session.Sequence.Regions.Count;
        SummaryText.Text = $"{markers} {(markers == 1 ? "marcador" : "marcadores")} · {regions} {(regions == 1 ? "región" : "regiones")}";
        EmptyText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkerList.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private MarkerItem? Find(string? key) => key is null ? null : Items.FirstOrDefault(i => i.Key == key);

    private void GoTo(MarkerItem? item)
    {
        if (item is not null)
        {
            _session?.Seek(item.At);
        }
    }

    private void Remove(string? key)
    {
        if (Find(key) is not { } item || _session is null)
        {
            return;
        }

        _session.Execute(item.IsRegion ? Edits.RemoveRegion(_session.SequenceId, item.Id) : Edits.RemoveMarker(_session.SequenceId, item.Id));
    }

    public void EditMarker(string markerId) => UiTask.Run(EditAsync("m:" + markerId));

    private async Task EditAsync(string? key)
    {
        if (Find(key) is not { } item || _session is not { } session || DialogGate.IsOpen || XamlRoot is null)
        {
            return;
        }

        Sequence sequence = session.Sequence;
        Marker? marker = item.IsRegion ? null : sequence.Markers.Find(m => m.Id == item.Id);
        Region? region = item.IsRegion ? sequence.Regions.Find(r => r.Id == item.Id) : null;
        if (marker is null && region is null)
        {
            return;
        }

        var name = new TextBox
        {
            Header = "Nombre",
            Text = marker?.Name ?? region!.Name,
            MaxLength = 200,
            PlaceholderText = item.IsRegion ? "Nombre de la región" : "Nombre del marcador",
        };

        name.SelectAll();
        var colors = new ComboBox { Header = "Color", HorizontalAlignment = HorizontalAlignment.Stretch };
        string? current = marker?.Color ?? region!.Color;
        foreach ((string label, string? hex) in Palette)
        {
            var swatch = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 14,
                Height = 14,
                RadiusX = 2,
                RadiusY = 2,
                Fill = MarkerItem.BrushFor(hex, item.IsRegion),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(swatch);
            row.Children.Add(new TextBlock { Text = label });
            colors.Items.Add(new ComboBoxItem { Content = row, Tag = hex });
        }

        int selected = Palette.ToList().FindIndex(p => string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase));
        if (selected < 0 && current is not null)
        {
            colors.Items.Add(new ComboBoxItem { Content = current, Tag = current });
            selected = colors.Items.Count - 1;
        }

        colors.SelectedIndex = Math.Max(0, selected);
        var note = new TextBox
        {
            Header = "Nota",
            Text = marker?.Note ?? region!.Note ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 80,
            MaxLength = MarkerStyle.MaxNoteLength,
            PlaceholderText = "Opcional: qué hay que revisar aquí, para quién…",
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(name);
        panel.Children.Add(colors);
        panel.Children.Add(note);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.IsRegion ? "Editar región" : "Editar marcador",
            Content = panel,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        string? color = (colors.SelectedItem as ComboBoxItem)?.Tag as string;
        session.Execute(region is not null
            ? Edits.EditRegion(session.SequenceId, region.Id, name.Text, color, note.Text, region.Start, region.End)
            : Edits.EditMarker(session.SequenceId, marker!.Id, name.Text, color, note.Text));
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MarkerItem item)
        {
            GoTo(item);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (MarkerList.SelectedItem is not MarkerItem item)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                GoTo(item);
                e.Handled = true;
                break;
            case VirtualKey.F2:
                UiTask.Run(EditAsync(item.Key));
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                Remove(item.Key);
                e.Handled = true;
                break;
        }
    }

    private void OnItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        FrameworkElement? element = args.OriginalSource as FrameworkElement;
        MarkerItem? item = element switch
        {
            ListViewItem { Content: MarkerItem content } => content,
            { DataContext: MarkerItem context } => context,
            _ => null,
        };
        if (item is null || element is null)
        {
            return;
        }

        _menuTarget = item;
        if (args.TryGetPosition(element, out Point point))
        {
            _menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            _menu.ShowAt(element);
        }

        args.Handled = true;
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => UiTask.Run(EditAsync((sender as FrameworkElement)?.Tag as string));

    private void OnRemoveClick(object sender, RoutedEventArgs e) => Remove((sender as FrameworkElement)?.Tag as string);

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null && TimelineEdits.PreviousMarker(_session.Sequence, _session.EditPosition) is { } marker)
        {
            _session.Seek(marker.Time);
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null && TimelineEdits.NextMarker(_session.Sequence, _session.EditPosition) is { } marker)
        {
            _session.Seek(marker.Time);
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        string name = TimelinePlanning.NextName("Marcador", _session.Sequence.Markers.Select(m => m.Name));
        _session.Execute(Edits.AddMarker(_session.SequenceId, _session.EditPosition, name));
    }

    private void OnAddRegionClick(object sender, RoutedEventArgs e) => _services?.Shortcuts.Invoke(ActionIds.AddRegion);
}
