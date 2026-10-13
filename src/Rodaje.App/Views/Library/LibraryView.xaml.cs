using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Plugins;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Editing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Packages;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Rodaje.App.Views.Library;

/// <summary>Riel de categorías y biblioteca: medios del proyecto, elementos de los paquetes y módulos de IA.</summary>
// CA1001: la vista vive lo mismo que la ventana; el vigilante de carpeta y los proxies terminan con el proceso.
#pragma warning disable CA1001
internal sealed partial class LibraryView : UserControl, IEditorView
#pragma warning restore CA1001
{
    /// <summary>Formato del arrastre de un medio hacia la línea de tiempo; el texto es el id del medio.</summary>
    public const string MediaDragFormat = "rodaje/media-id";

    private const int CoverHeight = 96;

    private readonly MenuFlyout _mediaMenu = new();
    private readonly MenuFlyoutItem _addItem;
    private readonly MenuFlyoutItem _replaceItem;
    private readonly MenuFlyoutItem _proxyItem;
    private readonly MenuFlyoutItem _revealItem;
    private readonly MenuFlyoutItem _removeItem;
    private readonly MenuFlyoutItem _favoriteItem;
    private readonly MenuFlyoutSubItem _moveItem;
    private readonly LibraryOrganizationStore _organizationStore = new(Path.Combine(Rodaje.App.Services.AppDataPaths.Root, LibraryOrganizationStore.FolderName));
    private LibraryOrganization _organization = new();
    private int _savedRevision;
    private List<MediaItem> _allMedia = [];
    private Dictionary<string, MediaItem> _itemsById = new(StringComparer.Ordinal);
    private string? _folderId;
    private LibraryKind _kind = LibraryKind.All;
    private string[] _dragMediaIds = [];
    private int _codecScan;
    private EditorServices? _services;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _ownRefresh;
    private FileSystemWatcher? _ownWatcher;
    private readonly HashSet<MediaSourceResolver> _watchedResolvers = [];
    private ScrollViewer? _mediaScroller;
    private bool _coverScanScheduled;
    private PreviewProxies? _proxies;
    private LibraryCategory _category = LibraryCategory.Media;
    private MediaItem? _menuTarget;
    private ElementApplier? _applier;
    private bool _markersOpen;

    public LibraryView()
    {
        InitializeComponent();
        _addItem = MenuItem("Añadir a la línea de tiempo", "\uE710", (_, _) => AppendTarget());
        _replaceItem = MenuItem("Reemplazar el clip seleccionado", "\uE8AB", (_, _) => UiTask.Run(ReplaceSelectedAsync()));
        _proxyItem = MenuItem("Generar proxy", "\uE8B1", (_, _) => UiTask.Run(GenerateProxyAsync()));
        _revealItem = MenuItem("Mostrar en el Explorador", "\uEC50", (_, _) => RevealTarget());
        _removeItem = MenuItem("Quitar del proyecto", "\uE74D", (_, _) => RemoveTarget());
        _mediaMenu.Items.Add(_addItem);
        _mediaMenu.Items.Add(MenuItem("Abrir en el visor de origen", "", (_, _) => OpenTargetInSource()));
        _mediaMenu.Items.Add(_replaceItem);
        _mediaMenu.Items.Add(_proxyItem);
        _favoriteItem = MenuItem("Añadir a favoritos", "\uE734", (_, _) => ToggleFavoriteTarget());
        _moveItem = new MenuFlyoutSubItem { Text = "Mover a", Icon = new FontIcon { Glyph = "\uE8DE" } };
        _mediaMenu.Items.Add(_revealItem);
        _mediaMenu.Items.Add(new MenuFlyoutSeparator());
        _mediaMenu.Items.Add(_favoriteItem);
        _mediaMenu.Items.Add(_moveItem);
        _mediaMenu.Items.Add(new MenuFlyoutSeparator());
        _mediaMenu.Items.Add(_removeItem);
        AddProMenuItems();
        KindButton.Flyout = FilterFlyout();
    }

    public ObservableCollection<MediaItem> Media { get; } = [];

    public ObservableCollection<MediaItem> AudioMedia { get; } = [];

    /// <summary>Las carpetas que cuelgan de la carpeta abierta (ninguna mientras se busca).</summary>
    public ObservableCollection<FolderItem> Folders { get; } = [];

    public ObservableCollection<ElementItem> Elements { get; } = [];

    /// <summary>Lo que hizo la persona (por ejemplo, «Guardar como transición» del inspector) en la categoría abierta.</summary>
    public ObservableCollection<ElementItem> OwnElements { get; } = [];

    public ObservableCollection<ElementItem> Sounds { get; } = [];

    public ObservableCollection<ModuleItem> Modules { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _dispatcher = DispatcherQueue;
        Shortcuts.MouseShortcutHost.Attach(this, MouseContext.Library, services);
        services.Session.ProjectOpened += (_, _) => RefreshMedia();
        services.Session.ProjectEdited += (_, _) => RefreshMedia();
        services.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorSession.FilePath))
            {
                FollowProjectFile();
            }
        };
        services.Visuals.Changed += OnVisualsChanged;
        AttachWatched(services);
        if (services.AutoProxies is { } autoProxies)
        {
            autoProxies.StatusChanged += (_, status) => ShowProxyStatus(status);
        }

        services.Packages.Changed += (_, _) => RefreshPackages();
        WatchOwnElements(services.Packages.Locations.Own);
        MarkersView.Attach(services);
        RefreshMedia();
        RefreshPackages();
        UiTask.Run(services.Packages.RefreshAsync());
        if (Rodaje.Media.EnvironmentVariables.Read(LibraryScrollMeasurement.Variable) is { } report)
        {
            UiTask.Run(LibraryScrollMeasurement.RunAsync(this, MediaGrid, report));
        }
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, RoutedEventHandler click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += click;
        return item;
    }

    private void RefreshMedia()
    {
        if (_services is null)
        {
            return;
        }

        EditorSession session = _services.Session;
        LoadOrganization(session.Project);
        var used = new HashSet<string>(
            session.Project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips).Select(c => c.MediaId), StringComparer.Ordinal);
        _allMedia = BuildItems(session.Project.Media, used);
        _itemsById = _allMedia.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Sync(AudioMedia, session.Project.Media.Where(m => m.HasAudio && !m.HasVideo).Select(m => _itemsById[m.Id]).ToList());
        RefreshMarks();
        StartHealthScan(session.Project);
        ApplyFilter();
    }

    /// <summary>
    /// Una ficha por medio del proyecto. Se reutiliza la anterior si no cambió nada de lo que muestra, para no perder
    /// portadas, el estado del proxy ni los códecs ya leídos; si cambió, la nueva hereda lo del mismo archivo.
    /// </summary>
    private List<MediaItem> BuildItems(List<Rodaje.Core.Model.MediaRef> media, HashSet<string> used)
    {
        var next = new List<MediaItem>(media.Count);
        foreach (Rodaje.Core.Model.MediaRef m in media)
        {
            _itemsById.TryGetValue(m.Id, out MediaItem? old);
            MediaItem item;
            if (old is not null && old.Shows(m))
            {
                item = old;
                item.IsUsed = used.Contains(m.Id);
            }
            else
            {
                // La portada se pide al hacerse visible (OnMediaContainerChanging), no aquí: con cientos de medios se haría todo.
                item = new MediaItem(m, used.Contains(m.Id));
                if (old is not null && old.Path == item.Path)
                {
                    item.CopyFrom(old);
                }
            }

            item.IsFavorite = _organization.IsFavorite(m.Id);
            next.Add(item);
        }

        return next;
    }

    /// <summary>Reemplaza la colección solo si cambió qué fichas se ven, para no perder la posición del scroll.</summary>
    private static void Sync<T>(ObservableCollection<T> target, List<T> next)
        where T : class
    {
        if (target.Count == next.Count && target.Zip(next).All(p => ReferenceEquals(p.First, p.Second)))
        {
            return;
        }

        target.Clear();
        foreach (T item in next)
        {
            target.Add(item);
        }
    }

    private void RefreshPackages()
    {
        if (_services is null)
        {
            return;
        }

        PackageService packages = _services.Packages;
        Elements.Clear();
        OwnElements.Clear();
        if (_category is not (LibraryCategory.Media or LibraryCategory.Audio or LibraryCategory.Ai))
        {
            foreach (LibraryElement element in LibraryElements.For(_category, packages.Installed))
            {
                (element.Origin == PackageOrigin.Own ? OwnElements : Elements).Add(new ElementItem(element));
            }
        }

        OwnTitle.Text = LibraryElements.OwnTitle(_category);
        LoadTransitionPreviews();

        Sounds.Clear();
        foreach (LibraryElement element in LibraryElements.For(LibraryCategory.Audio, packages.Installed))
        {
            Sounds.Add(new ElementItem(element));
        }

        Modules.Clear();
        foreach (PluginEntry entry in packages.Entries().Where(e => e.Kind == PackageKind.Module))
        {
            Modules.Add(new ModuleItem(entry));
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool markers = _markersOpen;
        bool media = !markers && _category == LibraryCategory.Media;
        bool audio = !markers && _category == LibraryCategory.Audio;
        bool ai = !markers && _category == LibraryCategory.Ai;
        bool elements = !markers && !media && !audio && !ai;
        bool library = _allMedia.Count > 0 || _organization.Folders.Count > 0;
        bool details = DetailsToggle.IsChecked == true;
        LibraryQuery query = Query();
        MarkersView.Visibility = markers ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = media && library ? Visibility.Visible : Visibility.Collapsed;
        MediaGrid.Visibility = !details && Media.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MediaList.Visibility = details && Media.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsHeader.Visibility = MediaList.Visibility;
        FolderGrid.Visibility = Folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FolderBar.Visibility = !query.IsGlobal && _folderId is not null ? Visibility.Visible : Visibility.Collapsed;
        NewFolderButton.Visibility = media && library ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = audio && (AudioMedia.Count > 0 || Sounds.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        AudioGrid.Visibility = AudioMedia.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SoundsPanel.Visibility = Sounds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ElementsPanel.Visibility = elements && Elements.Count + OwnElements.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OwnPanel.Visibility = OwnElements.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AiPanel.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = media || audio ? Visibility.Visible : Visibility.Collapsed;
        string? empty = markers ? null : _category switch
        {
            LibraryCategory.Media when !library =>
                "Importa videos o audios, o arrástralos aquí. Doble clic en uno lo añade al final de la pista principal; también puedes arrastrarlo a la línea de tiempo.",
            LibraryCategory.Media when Media.Count == 0 && Folders.Count == 0 && LibraryFilter.EmptyMessage(query, FolderHasMedia()) is { Length: > 0 } message => message,
            LibraryCategory.Audio when AudioMedia.Count == 0 && Sounds.Count == 0 =>
                "Importa música o efectos de sonido, o instala un paquete de sonidos desde Complementos.",
            _ when elements && Elements.Count + OwnElements.Count == 0 => "Instala un paquete desde Complementos para ver más elementos aquí.",
            _ => null,
        };
        EmptyText.Text = empty ?? "";
        EmptyText.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCategoryChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: "Markers" } markersButton && CategoryTitle is not null)
        {
            _markersOpen = true;
            CategoryTitle.Text = ToolTipService.GetToolTip(markersButton) as string ?? "Marcadores";
            UpdateVisibility();
            return;
        }

        if (sender is not RadioButton { Tag: string tag } button || !Enum.TryParse(tag, out LibraryCategory category) || CategoryTitle is null)
        {
            return;
        }

        _markersOpen = false;
        _category = category;
        CategoryTitle.Text = ToolTipService.GetToolTip(button) as string ?? tag;
        RefreshPackages();
    }

    private void OnVisualsChanged(object? sender, ClipVisualsChangedEventArgs e)
    {
        if (e.Kind != ClipVisualKind.Thumbnails || _services is null)
        {
            return;
        }

        OnSkimThumbnailReady(e.MediaId);
        foreach (MediaItem item in _allMedia.Concat(_subclipItems.Values))
        {
            if (item.Id == e.MediaId && item.Thumbnail is null && item.HasVideo
                && _services.Visuals.GetThumbnail(item.Id, item.CoverTime, CoverHeight) is { } image)
            {
                item.Thumbnail = BgraBitmaps.From(image);
            }
        }
    }

    /// <summary>Pide la portada solo de los medios que se hacen visibles (la cuadrícula virtualiza).</summary>
    /// <summary>Cada vez que la cuadrícula crea o recicla un contenedor, se revisa qué portadas faltan a la vista.</summary>
    private void OnMediaContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args) => ScheduleCoverScan();

    private void ScheduleCoverScan()
    {
        if (!_coverScanScheduled && _dispatcher is not null)
        {
            _coverScanScheduled = _dispatcher.RunLater(DispatcherQueuePriority.Low, ScanVisibleCovers);
        }
    }

    /// <summary>
    /// Pide la portada de los medios que se ven (su contenedor cae dentro de la cuadrícula), de abajo hacia arriba: el
    /// último pedido es el primero que se atiende, así salen primero las de arriba. Lo que la cuadrícula prepara fuera de
    /// la vista espera a que se desplace hasta ahí.
    /// </summary>
    private void ScanVisibleCovers()
    {
        _coverScanScheduled = false;
        if (_services is null || MediaPanel.Visibility != Visibility.Visible || MediaGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        EnsureScroller();
        double height = MediaGrid.ActualHeight;
        var visible = new List<MediaItem>();
        for (int i = 0; i < Media.Count; i++)
        {
            if (Media[i] is not { HasVideo: true, Thumbnail: null } item || MediaGrid.ContainerFromIndex(i) is not { } container)
            {
                continue;
            }

            double top = WinRT.CastExtensions.As<UIElement>(container).TransformToVisual(MediaGrid).TransformPoint(default).Y;
            if (top > -CoverHeight && top < height)
            {
                visible.Add(item);
            }
        }

        for (int i = visible.Count - 1; i >= 0; i--)
        {
            if (_services.Visuals.GetThumbnail(visible[i].Id, visible[i].CoverTime, CoverHeight) is { } image)
            {
                visible[i].Thumbnail = BgraBitmaps.From(image);
            }
        }
    }

    private void OnMediaGridLoaded(object sender, RoutedEventArgs e) => EnsureScroller();

    /// <summary>Al desplazar se vuelve a revisar qué portadas faltan; la plantilla existe recién cuando la cuadrícula se ve.</summary>
    private void EnsureScroller()
    {
        if (_mediaScroller is null && FindScroller(MediaGrid) is { } scroller)
        {
            _mediaScroller = scroller;
            scroller.ViewChanged += (_, _) => ScheduleCoverScan();
        }
    }

    /// <summary>El ScrollViewer de la plantilla; con AOT su envoltorio puede quedar con un tipo base, así que se busca por nombre.</summary>
    internal static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "ScrollViewer" })
            {
                return WinRT.CastExtensions.As<ScrollViewer>(child);
            }

            if (FindScroller(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnImportClick(object sender, RoutedEventArgs e) => UiTask.Run(ImportAsync());

    private async Task ImportAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            await _services.Session.ImportMediaAsync();
        }
        catch (Exception ex)
        {
            UiTask.Report(ex, notify: false);
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo importar", ex.Message);
        }
    }

    private void OnPluginsClick(object sender, RoutedEventArgs e) => _services?.Shortcuts.Invoke(ActionIds.ShowPlugins);

    private void OnMediaDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_services is not null && (e.OriginalSource as FrameworkElement)?.DataContext is MediaItem item)
        {
            if (item.Subclip is { } subclip)
            {
                _services.Session.AppendToTimeline(item.Id, subclip.In, subclip.Out);
            }
            else
            {
                _services.Session.AppendToTimeline(item.Id);
            }
        }
    }

    private void OnMediaDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not MediaItem item)
        {
            e.Cancel = true;
            return;
        }

        _dragMediaIds = [.. e.Items.OfType<MediaItem>().Select(i => i.Id)];
        e.Data.SetData(MediaDragFormat, item.DragText);
        e.Data.SetText(item.Id);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnElementDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not ElementItem { Element.IsAvailable: true } item)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetData(LibraryElement.DragFormat, item.Element.Reference);
        if (item.Element.Kind == ItemKind.Transition && TransitionDrag.TryParse(item.Element.Reference, out TransitionDrag? transition))
        {
            e.Data.SetData(TransitionDrag.DragFormat, transition.ToString());
        }

        if (item.Element.Kind == ItemKind.TextStyle)
        {
            e.Data.SetData(TitleDrag.DragFormat, item.Element.Reference);
        }

        if (item.Element.Kind == ItemKind.Sound)
        {
            e.Data.SetData(SoundDrag.DragFormat, item.Element.Reference);
        }

        e.Data.SetText(item.Element.Reference);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnElementDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ElementItem item)
        {
            if (item.Element.Kind == ItemKind.Sound)
            {
                AddSoundAtCursor(item.Element);
                return;
            }

            UiTask.Run(ApplyElementAsync(item.Element));
        }
    }

    private void OnElementContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.OriginalSource is not FrameworkElement { DataContext: ElementItem item } element
            || (item.Element.Kind != ItemKind.Sound && !ElementApplier.CanApply(item.Element)))
        {
            return;
        }

        args.Handled = true;
        var menu = new MenuFlyout();
        if (item.Element.Kind == ItemKind.Sound)
        {
            AddSoundItems(menu, item.Element);
        }
        else if (item.Element.Kind == ItemKind.TextStyle)
        {
            menu.Items.Add(MenuItem("A\u00F1adir texto en el cursor", "\uE8D2", (_, _) => UiTask.Run(AddTitleAsync(item.Element))));
            if (_services?.Session.SelectedTitle is not null)
            {
                menu.Items.Add(MenuItem("Aplicar al texto seleccionado", "\uE710", (_, _) => UiTask.Run(ApplyElementAsync(item.Element))));
            }
        }
        else
        {
            string text = item.Element.Kind == ItemKind.Transition ? "Poner en el corte del clip seleccionado" : "Aplicar al clip seleccionado";
            menu.Items.Add(MenuItem(text, "\uE710", (_, _) => UiTask.Run(ApplyElementAsync(item.Element))));
        }

        if (args.TryGetPosition(element, out Point point))
        {
            menu.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(element);
        }
    }

    private async Task ApplyElementAsync(LibraryElement element)
    {
        if (Applier() is { } applier && ElementApplier.CanApply(element))
        {
            await applier.ApplyAsync(element);
        }
    }

    private async Task AddTitleAsync(LibraryElement element)
    {
        if (Applier() is { } applier)
        {
            await applier.AddTitleAsync(element);
        }
    }

    private ElementApplier? Applier() => _services is not { } services
        ? null
        : _applier ??= new ElementApplier(services.Session, new InstalledContentLoader(() => services.Packages.Installed));

    private void OnMediaContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        FrameworkElement? element = args.OriginalSource as FrameworkElement;
        MediaItem? item = element switch
        {
            SelectorItem { Content: MediaItem content } => content,
            { DataContext: MediaItem context } => context,
            _ => null,
        };
        if (item is null || element is null || _services is null)
        {
            return;
        }

        _menuTarget = item;
        _replaceItem.IsEnabled = ReplaceTargetClip() is not null;
        _proxyItem.IsEnabled = item.HasVideo;
        _removeItem.IsEnabled = !item.IsUsed;
        _removeItem.Text = item.IsUsed ? "Quitar del proyecto (está en uso)" : "Quitar del proyecto";
        bool favorite = _organization.IsFavorite(item.Id);
        _favoriteItem.Text = favorite ? "Quitar de favoritos" : "Añadir a favoritos";
        _favoriteItem.Icon = new FontIcon { Glyph = favorite ? "\uE735" : "\uE734" };
        FillMoveMenu(item);
        UpdateProMenu(item);
        if (args.TryGetPosition(element, out Point point))
        {
            _mediaMenu.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            _mediaMenu.ShowAt(element);
        }

        args.Handled = true;
    }

    /// <summary>El visor de origen: marcar entrada y salida (I/O) y editar con tres o cuatro puntos (, y .).</summary>
    private void OpenTargetInSource()
    {
        if (_menuTarget is { } item)
        {
            _services?.Session.OpenSource(item.Id);
        }
    }

    private void AppendTarget()
    {
        if (_menuTarget is { } item)
        {
            _services?.Session.AppendToTimeline(item.Id);
        }
    }

    /// <summary>El clip seleccionado cuyo medio se puede reemplazar: uno de medio, no un título.</summary>
    private string? ReplaceTargetClip()
    {
        if (_services is null || _services.Session.SelectedClipIds.Count == 0)
        {
            return null;
        }

        string clipId = _services.Session.SelectedClipIds[0];
        return _services.Session.Sequence.FindClip(clipId) is { Clip.Generator: null } ? clipId : null;
    }

    /// <summary>
    /// Reemplaza el medio del clip seleccionado por el del menú, conservando duración, efectos y keyframes. Si el medio
    /// nuevo es más corto, avisa antes de acortar el clip; si no se puede, avisa por qué.
    /// </summary>
    private async Task ReplaceSelectedAsync()
    {
        if (_menuTarget is not { } item || _services is null || ReplaceTargetClip() is not { } clipId)
        {
            return;
        }

        EditorSession session = _services.Session;
        ReplaceMediaPreview preview;
        try
        {
            preview = TimelineEdits.PreviewReplaceMedia(session.Project, session.Sequence, clipId, item.Id);
        }
        catch (EditException ex)
        {
            session.Notify(NotificationSeverity.Warning, "No se pudo reemplazar", ex.Message);
            return;
        }

        if (preview.Shortened)
        {
            if (DialogGate.IsOpen || XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "El medio nuevo es más corto",
                Content = new TextBlock
                {
                    Text = $"«{item.Name}» dura menos que lo que usa el clip: pasará de {Rodaje.Engine.Presentation.Timecode.Format(preview.OldDuration)} a {Rodaje.Engine.Presentation.Timecode.Format(preview.NewDuration)} (lo que viene después no se corre). Los efectos y los keyframes se conservan. Se puede deshacer con Ctrl+Z.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Reemplazar y acortar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = ElementTheme.Dark,
            };
            if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        session.Execute(Rodaje.Core.Commands.Edits.ReplaceMedia(session.SequenceId, clipId, item.Id));
    }

    private void RevealTarget()
    {
        if (_menuTarget is not { } item || _services is null)
        {
            return;
        }

        if (!File.Exists(item.Path))
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se encuentra el archivo", item.Path);
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se pudo abrir el Explorador", ex.Message);
        }
    }

    private void RemoveTarget()
    {
        if (_menuTarget is { IsUsed: false } item && _services is not null)
        {
            _services.Session.Execute(new Rodaje.Core.Commands.RemoveMediaCommand(item.Id));
        }
    }

    private async Task GenerateProxyAsync()
    {
        if (_menuTarget is not { HasVideo: true } item || _services is null)
        {
            return;
        }

        EditorSession session = _services.Session;
        // El del reproductor si existe: así el proxy aparece en Trabajos y la vista previa lo toma al instante.
        MediaSourceResolver? resolver = _services.Player.ProxyResolver;
        if (resolver is null)
        {
            _proxies ??= PreviewProxies.Create(generate: false);
            resolver = _proxies.Resolver;
        }

        if (_watchedResolvers.Add(resolver))
        {
            resolver.StatusChanged += (_, e) => _dispatcher?.RunLater(() => ShowProxyStatus(e.Status));
        }

        try
        {
            await resolver.RequestProxyAsync(item.Path);
            session.Notify(NotificationSeverity.Success, "Proxy listo", $"La vista previa de «{item.Name}» usa el proxy en cuanto se actualiza.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ProxyGenerationException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            session.Notify(NotificationSeverity.Error, $"No se pudo generar el proxy de «{item.Name}»", ex.Message);
        }
    }

    private void ShowProxyStatus(ProxyStatus status)
    {
        string badge = status.State switch
        {
            ProxyState.Queued => "Proxy en cola",
            ProxyState.Generating => $"Proxy {status.Progress * 100:0} %",
            ProxyState.Ready => "Proxy",
            ProxyState.Failed => "Proxy falló",
            _ => "",
        };
        foreach (MediaItem item in _allMedia.Where(i => string.Equals(i.Path, status.SourcePath, StringComparison.OrdinalIgnoreCase)))
        {
            item.Badge = badge;
        }
    }

    /// <summary>Las transiciones que guarda el inspector en <c>complementos\propios</c> aparecen sin reiniciar.</summary>
    private void WatchOwnElements(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            _ownRefresh = DispatcherQueue.CreateTimer();
            _ownRefresh.Interval = TimeSpan.FromMilliseconds(400);
            _ownRefresh.IsRepeating = false;
            _ownRefresh.Tick += (_, _) => UiTask.Run(_services?.Packages.RefreshAsync());
            _ownWatcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            // Copiar un archivo grande dispara muchos Changed seguidos: un solo aviso pendiente a la vez.
            var restart = new CoalescedDispatch(DispatcherQueue, () => _ownRefresh?.Start());
            FileSystemEventHandler changed = (_, _) => restart.Request();
            _ownWatcher.Created += changed;
            _ownWatcher.Changed += changed;
            _ownWatcher.Deleted += changed;
            _ownWatcher.Renamed += (_, _) => restart.Request();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _ownWatcher?.Dispose();
            _ownWatcher = null;
        }
    }

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Importar a la biblioteca";
            e.Handled = true;
        }
    }

    private void OnFilesDrop(object sender, DragEventArgs e) => UiTask.Run(OnFilesDropAsync(e));

    private async Task OnFilesDropAsync(DragEventArgs e)
    {
        if (_services is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        try
        {
            await DropFilesAsync(_services, e.DataView);
        }
        catch (Exception ex)
        {
            UiTask.Report(ex, notify: false);
            _services.Session.Notify(NotificationSeverity.Error, "No se pudo abrir lo soltado", ex.Message);
        }
    }

    private static async Task DropFilesAsync(EditorServices services, DataPackageView data)
    {
        IReadOnlyList<IStorageItem> items = await data.GetStorageItemsAsync();
        string[] paths = [.. items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p))];
        string[] packages = [.. paths.Where(p => p.EndsWith(PackageManifest.FileExtension, StringComparison.OrdinalIgnoreCase))];
        string[] media = [.. paths.Except(packages)];
        if (media.Any(ProjectPaths.IsProjectFile))
        {
            await services.Session.OpenFilesAsync(media);
        }
        else if (media.Length > 0)
        {
            await services.Session.ImportMediaAsync(media);
        }

        foreach (string package in packages)
        {
            await PluginActions.InstallAsync(services, package);
        }
    }
}
