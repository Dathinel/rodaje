using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services;
using Rodaje.App.Shell;
using Rodaje.Core.Model;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Carpetas, búsqueda, filtro por tipo, favoritos y la lista con datos de la biblioteca de medios. Las reglas están en
/// Logic/ (sin WinUI, con pruebas); aquí solo se conectan con la vista y se guardan por proyecto.
/// </summary>
// CA1001: como en LibraryView.xaml.cs, la vista vive lo mismo que la ventana.
#pragma warning disable CA1001
internal sealed partial class LibraryView
#pragma warning restore CA1001
{
    private const string RootName = "Todos los medios";

    /// <summary>
    /// Códecs ya leídos por ruta (y fecha y tamaño del archivo), para no volver a abrirlo al cambiar de carpeta o de
    /// proyecto; un archivo que cambió se vuelve a leer, también si antes había fallado.
    /// </summary>
    private static readonly MediaCodecCache CodecCache = new();

    private LibraryQuery Query() => new(_folderId, SearchBox.Text ?? "", _kind, FavoritesToggle.IsChecked == true);

    /// <summary>Proyecto (la instancia) cuya organización está cargada; cambia solo al abrir o crear otro.</summary>
    private Project? _organizationProject;

    /// <summary>Clave de <see cref="LibraryOrganizationStore"/> con la que se guarda la organización cargada.</summary>
    private string? _organizationKey;

    /// <summary>
    /// Lee las carpetas y favoritos del proyecto la primera vez que se ve (o si cambió de proyecto). No se poda: los
    /// medios que el proyecto ya no tiene se quedan en el archivo y la vista solo muestra los que existen (deshacer un
    /// «Quitar del proyecto» los devuelve a su carpeta).
    /// </summary>
    private void LoadOrganization(Project project)
    {
        if (ReferenceEquals(project, _organizationProject))
        {
            FollowProjectFile();
            return;
        }

        _organizationProject = project;
        _organizationKey = LibraryOrganizationStore.KeyFor(project.Id, _services?.Session.FilePath);
        _organization = _organizationStore.Load(_organizationKey, fallbackKey: project.Id);
        _savedRevision = _organization.Revision;
        _folderId = null;
    }

    /// <summary>
    /// Al guardar por primera vez o con «Guardar como», la organización sigue al archivo nuevo; la del archivo anterior
    /// queda como estaba (es la de ese otro <c>.rodaje</c>).
    /// </summary>
    private void FollowProjectFile()
    {
        if (_services is null || !ReferenceEquals(_services.Session.Project, _organizationProject) || _organizationProject is null)
        {
            return;
        }

        string key = LibraryOrganizationStore.KeyFor(_organizationProject.Id, _services.Session.FilePath);
        if (key == _organizationKey)
        {
            return;
        }

        _organizationKey = key;
        _savedRevision = -1;
        SaveOrganization();
    }

    private void SaveOrganization()
    {
        if (_organizationKey is null || _organization.Revision == _savedRevision)
        {
            return;
        }

        try
        {
            _organizationStore.Save(_organizationKey, _organization);
            _savedRevision = _organization.Revision;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "No se pudieron guardar las carpetas de la biblioteca", ex.Message);
        }
    }

    /// <summary>Muestra los medios y carpetas que corresponden a la carpeta abierta, la búsqueda, el tipo y los favoritos.</summary>
    private void ApplyFilter()
    {
        if (_services is null)
        {
            return;
        }

        Project project = _services.Session.Project;
        if (_organization.FindFolder(_folderId) is null)
        {
            _folderId = null;
        }

        LibraryQuery query = Query();
        Sync(Media, LibraryFilter.Apply(project.Media, _organization, query)
            .Where(m => _itemsById.ContainsKey(m.Id)).Select(m => _itemsById[m.Id]).ToList());

        List<FolderItem> folders = [.. LibraryFilter.Folders(_organization, query).Select(f => new FolderItem(
            f, LibraryFilter.FolderSummary(LibraryFilter.CountIn(project.Media, _organization, f.Id), _organization.Children(f.Id).Count)))];
        bool sameFolders = Folders.Count == folders.Count
            && Folders.Zip(folders).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.Summary == p.Second.Summary);
        if (!sameFolders)
        {
            Folders.Clear();
            foreach (FolderItem folder in folders)
            {
                Folders.Add(folder);
            }
        }

        FolderPathText.Text = string.Join(" › ", _organization.PathTo(_folderId).Select(f => f.Name).Prepend(RootName));
        UpdateVisibility();
        ScheduleCoverScan();
        StartCodecScan();
    }

    /// <summary>Si la carpeta abierta tiene algún medio sin mirar el tipo (para explicar por qué no se ve nada).</summary>
    private bool FolderHasMedia() =>
        _services is not null && LibraryFilter.Apply(_services.Session.Project.Media, _organization, Query() with { Kind = LibraryKind.All }).Count > 0;

    private MenuFlyout KindMenu()
    {
        var menu = new MenuFlyout();
        foreach ((LibraryKind kind, string label) in LibraryFilter.Kinds)
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "LibraryKind", IsChecked = kind == _kind };
            item.Click += (_, _) =>
            {
                _kind = kind;
                ToolTipService.SetToolTip(KindButton, $"Tipo: {label}");
                ApplyFilter();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFavoritesToggleClick(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnDetailsToggleClick(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OpenFolder(string? folderId)
    {
        _folderId = _organization.FindFolder(folderId)?.Id;
        ApplyFilter();
    }

    private void OnFolderClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FolderItem folder)
        {
            OpenFolder(folder.Id);
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => OpenFolder(_organization.FindFolder(_folderId)?.ParentId);

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        LibraryFolder folder = _organization.CreateFolder(_folderId);
        SaveOrganization();
        ApplyFilter();
        await RenameFolderAsync(folder.Id);
    }

    private void OnFolderContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement element || FolderUnder(element) is not { } item)
        {
            return;
        }

        args.Handled = true;
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Abrir", "", (_, _) => OpenFolder(item.Id)));
        menu.Items.Add(MenuItem("Renombrar…", "", (_, _) => _ = RenameFolderAsync(item.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = MenuItem("Eliminar carpeta", "", (_, _) => DeleteFolder(item.Id));
        ToolTipService.SetToolTip(delete, "Sus medios y carpetas pasan a la carpeta de arriba; ningún medio sale del proyecto.");
        menu.Items.Add(delete);
        if (args.TryGetPosition(element, out Point point))
        {
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(element);
        }
    }

    private void DeleteFolder(string folderId)
    {
        if (_organization.DeleteFolder(folderId))
        {
            SaveOrganization();
            ApplyFilter();
        }
    }

    /// <summary>Pide el nombre nuevo; el diálogo no se cierra con un nombre vacío o repetido y dice por qué.</summary>
    private async Task RenameFolderAsync(string folderId)
    {
        if (_organization.FindFolder(folderId) is not { } folder || DialogGate.IsOpen || XamlRoot is null)
        {
            return;
        }

        LibraryOrganization organization = _organization;
        var box = new TextBox { Text = folder.Name, MaxLength = LibraryOrganization.MaxNameLength };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(box);
        content.Children.Add(problem);
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Nombre de la carpeta",
            Content = content,
            PrimaryButtonText = "Aceptar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (organization.NameProblem(folderId, box.Text) is { } message)
            {
                problem.Text = message;
                problem.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        if (await DialogGate.ShowAsync(dialog) == ContentDialogResult.Primary && ReferenceEquals(organization, _organization)
            && _organization.Rename(folderId, box.Text))
        {
            SaveOrganization();
            ApplyFilter();
        }
    }

    private static FolderItem? FolderUnder(object? source) => source switch
    {
        SelectorItem { Content: FolderItem content } => content,
        FrameworkElement { DataContext: FolderItem context } => context,
        _ => null,
    };

    /// <summary>Lo arrastrado son medios de esta biblioteca (no archivos ni elementos).</summary>
    private bool IsMediaDrag(DragEventArgs e) => _dragMediaIds.Length > 0 && e.DataView.Contains(MediaDragFormat);

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        if (IsMediaDrag(e) && FolderUnder(e.OriginalSource) is { } folder)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Mover a «{folder.Name}»";
            e.Handled = true;
        }
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (IsMediaDrag(e) && FolderUnder(e.OriginalSource) is { } folder)
        {
            e.Handled = true;
            MoveMedia(_dragMediaIds, folder.Id);
        }
    }

    private void OnUpDragOver(object sender, DragEventArgs e)
    {
        if (IsMediaDrag(e) && _folderId is not null)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Mover a «{_organization.FindFolder(_organization.FindFolder(_folderId)?.ParentId)?.Name ?? RootName}»";
            e.Handled = true;
        }
    }

    private void OnUpDrop(object sender, DragEventArgs e)
    {
        if (IsMediaDrag(e) && _folderId is not null)
        {
            e.Handled = true;
            MoveMedia(_dragMediaIds, _organization.FindFolder(_folderId)?.ParentId);
        }
    }

    private void MoveMedia(IReadOnlyList<string> mediaIds, string? folderId)
    {
        _dragMediaIds = [];
        if (_organization.MoveMedia(mediaIds, folderId) > 0)
        {
            SaveOrganization();
            ApplyFilter();
        }
    }

    /// <summary>«Mover a»: la raíz, cada carpeta (sangrada según su profundidad) y una carpeta nueva.</summary>
    private void FillMoveMenu(MediaItem item)
    {
        _moveItem.Items.Clear();
        string? current = _organization.FolderOf(item.Id);
        MenuFlyoutItem root = MenuItem(RootName, "", (_, _) => MoveTarget(null));
        root.IsEnabled = current is not null;
        _moveItem.Items.Add(root);
        foreach ((LibraryFolder folder, int depth) in _organization.Tree())
        {
            string id = folder.Id;
            MenuFlyoutItem entry = MenuItem(new string(' ', depth * 2) + folder.Name, "", (_, _) => MoveTarget(id));
            entry.IsEnabled = id != current;
            _moveItem.Items.Add(entry);
        }

        _moveItem.Items.Add(new MenuFlyoutSeparator());
        _moveItem.Items.Add(MenuItem("Carpeta nueva…", "", (_, _) => _ = MoveTargetToNewFolderAsync()));
    }

    private void MoveTarget(string? folderId)
    {
        if (_menuTarget is { } item)
        {
            MoveMedia([item.Id], folderId);
        }
    }

    private async Task MoveTargetToNewFolderAsync()
    {
        if (_menuTarget is not { } item)
        {
            return;
        }

        LibraryFolder folder = _organization.CreateFolder(_folderId);
        _organization.MoveMedia([item.Id], folder.Id);
        SaveOrganization();
        ApplyFilter();
        await RenameFolderAsync(folder.Id);
    }

    private void ToggleFavoriteTarget()
    {
        if (_menuTarget is not { } item)
        {
            return;
        }

        bool favorite = _organization.ToggleFavorite(item.Id);
        if (_itemsById.TryGetValue(item.Id, out MediaItem? shown))
        {
            shown.IsFavorite = favorite;
        }

        item.IsFavorite = favorite;
        SaveOrganization();
        if (FavoritesToggle.IsChecked == true)
        {
            ApplyFilter();
        }
    }

    /// <summary>
    /// Con la lista abierta, lee en segundo plano el códec de los medios que se ven y aún no lo tienen (el proyecto no
    /// lo guarda), o que fallaron: esos solo se vuelven a abrir si el archivo cambió. Uno a la vez para no competir con
    /// la vista previa; un filtro nuevo corta la lectura anterior. Un error con un medio solo afecta a ese medio.
    /// </summary>
    private void StartCodecScan()
    {
        if (DetailsToggle.IsChecked != true || _dispatcher is not { } dispatcher)
        {
            return;
        }

        List<MediaItem> pending = [.. Media.Where(i => !i.CodecsKnown || i.CodecsFailed)];
        int scan = Interlocked.Increment(ref _codecScan);
        if (pending.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            foreach (MediaItem item in pending)
            {
                if (Volatile.Read(ref _codecScan) != scan)
                {
                    return;
                }

                bool failedBefore = item.CodecsKnown && item.CodecsFailed;
                MediaCodecs codecs = CodecCache.Read(item.Path, ReadCodecs);
                if (failedBefore && codecs.Failed)
                {
                    continue;
                }

                dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => item.SetCodecs(codecs));
            }
        });
    }

    private static (string? Video, string? Audio) ReadCodecs(string path)
    {
        MediaProbeResult result = new FfmpegMediaProber().Probe(path);
        return (result.Info?.Video?.Codec, result.Info?.Audio?.Codec);
    }
}
