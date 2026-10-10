using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Shell;

namespace Rodaje.App.Views.Library;

/// <summary>Riel de categorías y biblioteca de medios del proyecto.</summary>
internal sealed partial class LibraryView : UserControl, IEditorView
{
    private EditorSession? _session;
    private bool _mediaCategory = true;

    public LibraryView()
    {
        InitializeComponent();
    }

    public ObservableCollection<MediaItem> Items { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _session = services.Session;
        _session.ProjectOpened += (_, _) => Refresh();
        _session.ProjectEdited += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        if (_session is null)
        {
            return;
        }

        var media = _session.Project.Media;
        if (!media.Select(m => m.Id).SequenceEqual(Items.Select(i => i.Id), StringComparer.Ordinal))
        {
            Items.Clear();
            foreach (var m in media)
            {
                Items.Add(new MediaItem(m));
            }
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        MediaGrid.Visibility = _mediaCategory && Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = _mediaCategory && Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ImportButton.Visibility = _mediaCategory ? Visibility.Visible : Visibility.Collapsed;
        if (!_mediaCategory)
        {
            EmptyText.Text = $"{CategoryTitle.Text}: en construcción.";
        }
        else
        {
            EmptyText.Text = "Importa videos o audios, o arrástralos a la ventana. Doble clic en uno lo añade al final de la pista principal.";
        }
    }

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string category } && CategoryTitle is not null && MediaGrid is not null && EmptyText is not null)
        {
            CategoryTitle.Text = category;
            _mediaCategory = ReferenceEquals(sender, MediaTab);
            UpdateVisibility();
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            await _session.ImportMediaAsync();
        }
    }

    private void OnMediaDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_session is not null && (e.OriginalSource as FrameworkElement)?.DataContext is MediaItem item)
        {
            _session.AppendToTimeline(item.Id);
        }
    }
}
