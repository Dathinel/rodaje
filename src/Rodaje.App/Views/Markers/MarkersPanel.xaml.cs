using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.App.Views.Timeline;
using Rodaje.Engine.Presentation;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Markers;

/// <summary>Una fila de la lista de marcadores tal como se muestra.</summary>
internal sealed partial class MarkerItem(Marker marker)
{
    public string Id { get; } = marker.Id;

    public string Name { get; } = marker.Name.Length == 0 ? "(sin nombre)" : marker.Name;

    public Time At { get; } = marker.Time;

    public string TimeText { get; } = Timecode.Format(marker.Time);

    public bool SameAs(MarkerItem other) => Id == other.Id && Name == other.Name && At == other.At;
}

/// <summary>Lista de marcadores: doble clic va al marcador; renombrar y quitar son pasos del historial.</summary>
/// <remarks>Mientras el panel está oculto no se rehace la lista; se rehace al verse.</remarks>
internal sealed partial class MarkersPanel : UserControl, IEditorView
{
    private readonly MenuFlyout _menu = new();
    private EditorSession? _session;
    private MarkerItem? _menuTarget;
    private bool _stale;

    public MarkersPanel()
    {
        InitializeComponent();
        _menu.Items.Add(MenuItem("Ir al marcador", "", (_, _) => GoTo(_menuTarget)));
        _menu.Items.Add(MenuItem("Renombrar…", "", (_, _) => _ = RenameAsync(_menuTarget?.Id)));
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(MenuItem("Quitar marcador", "", (_, _) => Remove(_menuTarget?.Id)));
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
        _session = services.Session;
        _session.ProjectOpened += (_, _) => Refresh();
        _session.ProjectEdited += (_, _) => Refresh();
        Refresh();
        Bindings.Update();
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
        List<MarkerItem> next = [.. _session.Sequence.Markers.Select(m => new MarkerItem(m))];
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

        SummaryText.Text = next.Count == 1 ? "1 marcador" : $"{next.Count} marcadores";
        EmptyText.Visibility = next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkerList.Visibility = next.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GoTo(MarkerItem? item)
    {
        if (item is not null)
        {
            _session?.Seek(item.At);
        }
    }

    private void Remove(string? id)
    {
        if (id is not null && _session is not null)
        {
            _session.Execute(Edits.RemoveMarker(_session.SequenceId, id));
        }
    }

    private async Task RenameAsync(string? id)
    {
        if (id is null || _session is null || DialogGate.IsOpen || XamlRoot is null
            || _session.Sequence.Markers.Find(m => m.Id == id) is not { } marker)
        {
            return;
        }

        var box = new TextBox { Text = marker.Name, MaxLength = 200, PlaceholderText = "Nombre del marcador" };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Renombrar marcador",
            Content = box,
            PrimaryButtonText = "Renombrar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || box.Text.Trim() == marker.Name)
        {
            return;
        }

        _session.Execute(Edits.RenameMarker(_session.SequenceId, id, box.Text));
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
                _ = RenameAsync(item.Id);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                Remove(item.Id);
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

    private void OnRenameClick(object sender, RoutedEventArgs e) => _ = RenameAsync((sender as FrameworkElement)?.Tag as string);

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
}
