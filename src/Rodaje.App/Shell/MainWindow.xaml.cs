using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Player;
using Rodaje.App.Views.Plugins;
using Rodaje.App.Views.Shortcuts;
using Rodaje.Engine.Timeline;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.App.Shell;

/// <summary>
/// La ventana: crea los servicios, une las vistas, lleva el estado del reproductor a la sesión con un temporizador,
/// maneja los atajos de toda la ventana, las páginas modales, los avisos, soltar archivos y el cierre con cambios sin
/// guardar.
/// </summary>
// CA1001: el motor es de Program (using); la ventana lo detiene en Closed para leer las cifras finales.
#pragma warning disable CA1001
internal sealed partial class MainWindow : Window, IShellHost
#pragma warning restore CA1001
{
    private readonly AppOptions _options;
    private readonly PlayerEngine _engine;
    private readonly SettingsStore _settings;
    private readonly ShortcutStore _shortcutStore;
    private readonly ShortcutRouter _router;
    private readonly KeyboardShortcutHost _keyboard;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _transportTimer;
    private readonly DispatcherQueueTimer _notificationTimer;
    private readonly DispatcherQueueTimer _settingsTimer;
    private readonly nint _hwnd;
    private readonly EditorServices _services;
    private readonly ClipVisuals _visuals;
    private readonly List<IDisposable> _bindings = [];
    private readonly InstanceRegistry _instance = InstanceRegistry.Register(InstanceRegistry.DefaultDirectory);
    private ShortcutsView? _shortcutsView;
    private NotificationAction? _notificationAction;
    private PluginsView? _pluginsView;
    private IDisposable? _pageShortcuts;
    private bool _activatedOnce;
    private bool _allowClose;
    private bool _closing;
    private bool _closed;
    private bool _playerFullScreen;
    private (GridLength Library, GridLength Inspector, GridLength Timeline) _layoutBeforeFullScreen;

    internal MainWindow(AppOptions options, PlayerEngine engine, IMediaProber prober, SettingsStore settings, string? settingsWarning)
    {
        _options = options;
        _engine = engine;
        _settings = settings;
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetUpTitleBar();
        RestoreLayout();

        // Las teclas de signos se muestran como están impresas en el teclado de la persona (español: «'» y no «[»).
        KeyNames.DisplayLayout = SystemKeyboardLayout.Instance;
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = settings.Current.ShortcutPreset;
        _shortcutStore = new ShortcutStore(ShortcutStore.DefaultPath);
        string? shortcutsWarning = _shortcutStore.Load(registry);
        _router = new ShortcutRouter(registry);

        var session = new EditorSession(engine, prober, new ShellDialogs(this, _hwnd, settings));
        _visuals = new ClipVisuals(id => session.Project.FindMedia(id)?.Path, new FfmpegVisualsSources(),
            new ClipVisualsOptions { CacheDirectory = ClipVisualsOptions.DefaultCacheDirectory }, SynchronizationContext.Current);
        _ = _visuals.TrimDiskCacheAsync();
        var packages = new PackageService(PackageService.DefaultLocations(), PackageService.LoadBundledCatalog(), instances: _instance);
        IEffectContent? content = EffectContentFactory.CreateOrNull(packages);
        _services = new EditorServices(session, _router, settings, engine, this) { Visuals = _visuals, Packages = packages, Content = content };
        engine.SetContent(content);
        MissingContent.Attach(session, packages);
        _bindings.Add(EditorShortcuts.Bind(_router, session));
        _bindings.Add(_router.Bind(ActionIds.Export, () => session.Notify(NotificationSeverity.Informational, "Exportar",
            "La exportación llega en la fase 5. Por ahora puedes guardar el proyecto.")));
        _bindings.Add(_router.Bind(ActionIds.ShowShortcuts, () => ShowPage(ShellPage.Shortcuts)));
        _bindings.Add(_router.Bind(ActionIds.ShowPlugins, () => ShowPage(ShellPage.Plugins)));
        _keyboard = new KeyboardShortcutHost(Root, _router, InterceptKey);

        TitleBar.Attach(_services);
        TitleBar.InspectorTabRequested += (_, tab) => Inspector.Show(tab);
        TitleBar.ExitRequested += (_, _) => Close();
        Library.Attach(_services);
        Player.Attach(_services);
        Inspector.Attach(_services);
        Timeline.Attach(_services);
        Timeline.ClipVisuals = _visuals;

        session.Notified += (_, n) => ShowNotification(n);
        session.ProjectOpened += (_, _) => RememberProject();
        session.ProjectOpened += (_, _) => _visuals.ForgetRemovedMedia();
        session.ProjectEdited += (_, _) => _visuals.ForgetRemovedMedia();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EditorSession.FilePath))
            {
                RememberProject();
            }
        };
        registry.Changed += (_, _) => SaveShortcuts();

        _notificationTimer = _dispatcher.CreateTimer();
        _notificationTimer.Interval = TimeSpan.FromSeconds(6);
        _notificationTimer.IsRepeating = false;
        _notificationTimer.Tick += (_, _) => Notification.IsOpen = false;

        // El transporte se copia del reproductor a la sesión a un ritmo fijo (30 Hz por defecto, --ui-hz).
        _transportTimer = _dispatcher.CreateTimer();
        _transportTimer.Interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, options.UiHz));
        _transportTimer.Tick += (_, _) => session.UpdateTransport(_engine.Snapshot);
        if (options.UiHz <= 0)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            _transportTimer.Start();
        }

        // Los ajustes se guardan poco después de cambiar (no solo al cerrar): un apagón no se lleva los últimos.
        _settingsTimer = _dispatcher.CreateTimer();
        _settingsTimer.Interval = TimeSpan.FromSeconds(2);
        _settingsTimer.Tick += (_, _) => SaveSettingsIfChanged();
        _settingsTimer.Start();

        LibrarySplitter.Resized += (_, size) => settings.Current.LibraryWidth = size;
        InspectorSplitter.Resized += (_, size) => settings.Current.InspectorWidth = size;
        TimelineSplitter.Resized += (_, size) => settings.Current.TimelineHeight = size;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;

        foreach (string? warning in new[] { settingsWarning, shortcutsWarning })
        {
            if (warning is not null)
            {
                session.Notify(NotificationSeverity.Warning, "Ajustes", warning);
            }
        }

        if (options.Files.Count > 0)
        {
            _ = OpenStartupFilesAsync(options.Files);
        }
    }

    public XamlRoot? XamlRoot => Root.XamlRoot;

    internal EditorSession Session => _services.Session;

    internal ShortcutRouter Router => _router;

    internal KeyboardShortcutHost Keyboard => _keyboard;

    internal nint Hwnd => _hwnd;

    internal Button ExportButton => TitleBar.ExportButton;

    internal SwapChainPanel VideoPanel => Player.VideoPanel;

    public void ShowPage(ShellPage page)
    {
        UserControl view;
        if (page == ShellPage.Shortcuts)
        {
            if (_shortcutsView is null)
            {
                _shortcutsView = new ShortcutsView();
                _shortcutsView.Attach(_services);
            }

            view = _shortcutsView;
            ModalTitle.Text = "Atajos de teclado";
        }
        else
        {
            if (_pluginsView is null)
            {
                _pluginsView = new PluginsView();
                _pluginsView.Attach(_services);
            }

            view = _pluginsView;
            ModalTitle.Text = "Complementos";
        }

        ModalContent.Content = view;
        ModalLayer.Visibility = Visibility.Visible;

        // Con una página encima, el editor de detrás no recibe teclas; Esc (la cierra) y Archivo siguen.
        _pageShortcuts ??= _router.Restrict(action => action.Group == ShortcutGroups.File);
    }

    public void ClosePage()
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        ModalContent.Content = null;
        _pageShortcuts?.Dispose();
        _pageShortcuts = null;
    }

    public void SetPlayerFullScreen(bool fullScreen)
    {
        if (fullScreen == _playerFullScreen)
        {
            return;
        }

        _playerFullScreen = fullScreen;
        if (fullScreen)
        {
            _layoutBeforeFullScreen = (LibraryColumn.Width, InspectorColumn.Width, TimelineRow.Height);
            LibraryColumn.Width = InspectorColumn.Width = TimelineRow.Height = new GridLength(0);
        }
        else
        {
            (LibraryColumn.Width, InspectorColumn.Width, TimelineRow.Height) = _layoutBeforeFullScreen;
        }

        var splitter = new GridLength(fullScreen ? 0 : 4);
        MainArea.ColumnDefinitions[1].Width = splitter;
        MainArea.ColumnDefinitions[3].Width = splitter;
        Root.RowDefinitions[3].Height = splitter;
        Visibility chrome = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement element in new UIElement[] { TitleBar, Library, LibrarySplitter, Inspector, InspectorSplitter, TimelineSplitter, Timeline })
        {
            element.Visibility = chrome;
        }

        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Player.SetFullScreen(fullScreen);
    }

    private bool InterceptKey(KeyChord chord)
    {
        // Con los atajos suspendidos (la página de atajos captura una tecla), Esc es de quien captura.
        if (_router.IsSuspended)
        {
            return false;
        }

        if (chord == new KeyChord(0x1B))
        {
            if (_playerFullScreen)
            {
                SetPlayerFullScreen(false);
                return true;
            }

            if (ModalLayer.Visibility == Visibility.Visible)
            {
                ClosePage();
                return true;
            }

            if (Session.IsInGesture)
            {
                Session.CancelGesture();
                return true;
            }
        }

        return false;
    }

    private async Task OpenStartupFilesAsync(IReadOnlyList<string> files)
    {
        await Session.OpenFilesAsync(files);
        if (_options.MeasureSeconds is { } seconds)
        {
            await new MeasurementRun(this, _engine, Program.Metrics).RunAsync(seconds, _options.Scenario);
        }
    }

    private void SetUpTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar.DragRegion);
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ColorHelper.FromArgb(0xFF, 0xD8, 0xD8, 0xD8);
        bar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(0xFF, 0x23, 0x23, 0x23);
    }

    private void RestoreLayout()
    {
        AppSettings s = _settings.Current;
        LibraryColumn.Width = new GridLength(s.LibraryWidth);
        InspectorColumn.Width = new GridLength(s.InspectorWidth);
        TimelineRow.Height = new GridLength(s.TimelineHeight);
        uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
        double width = s.WindowWidth > 0 ? s.WindowWidth : 1440;
        double height = s.WindowHeight > 0 ? s.WindowHeight : 900;
        AppWindow.Resize(new SizeInt32((int)(width * dpi / 96), (int)(height * dpi / 96)));
        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void SaveLayout()
    {
        AppSettings s = _settings.Current;
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        s.WindowMaximized = maximized;
        if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
            s.WindowWidth = AppWindow.Size.Width * 96.0 / dpi;
            s.WindowHeight = AppWindow.Size.Height * 96.0 / dpi;
        }

        s.LibraryWidth = LibraryColumn.ActualWidth;
        s.InspectorWidth = InspectorColumn.ActualWidth;
        s.TimelineHeight = TimelineRow.ActualHeight;
        s.ShortcutPreset = _router.Registry.Preset;
    }

    private void SaveSettingsIfChanged()
    {
        // En pantalla completa o minimizada los paneles miden 0: se guarda lo de antes.
        if (_closed || !_activatedOnce || _playerFullScreen || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            return;
        }

        SaveLayout();
        try
        {
            _settings.SaveIfChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RememberProject()
    {
        if (Session.FilePath is { } path)
        {
            _settings.Current.AddRecentProject(path);
            _settings.Current.LastProjectFolder = Path.GetDirectoryName(path);
        }
    }

    private void SaveShortcuts()
    {
        try
        {
            _shortcutStore.Save(_router.Registry);
        }
        catch (IOException ex)
        {
            Session.Notify(NotificationSeverity.Warning, "Atajos", "No se pudieron guardar los atajos: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Session.Notify(NotificationSeverity.Warning, "Atajos", "No se pudieron guardar los atajos: " + ex.Message);
        }
    }

    private void ShowNotification(EditorNotification notification)
    {
        Notification.Severity = notification.Severity switch
        {
            NotificationSeverity.Success => InfoBarSeverity.Success,
            NotificationSeverity.Warning => InfoBarSeverity.Warning,
            NotificationSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        Notification.Title = notification.Title;
        Notification.Message = notification.Message;
        _notificationAction = notification.Action;
        NotificationButton.Content = notification.Action?.Text;
        NotificationButton.Visibility = notification.Action is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(NotificationButton, notification.Action?.Text ?? "");
        Notification.IsOpen = true;
        FrameworkElementAutomationPeer.FromElement(Notification)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, notification.Message, "rodaje.aviso");
        _notificationTimer.Stop();
        if (notification.Severity is NotificationSeverity.Informational or NotificationSeverity.Success)
        {
            // Con botón dura más: da tiempo a leerlo y a decidir si se deshace.
            _notificationTimer.Interval = TimeSpan.FromSeconds(notification.Action is null ? 6 : 10);
            _notificationTimer.Start();
        }
    }

    private void OnNotificationButtonClick(object sender, RoutedEventArgs e)
    {
        NotificationAction? action = _notificationAction;
        _notificationAction = null;
        _notificationTimer.Stop();
        Notification.IsOpen = false;
        action?.Run();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_activatedOnce)
        {
            return;
        }

        _activatedOnce = true;
        Program.Metrics.Milestone("ventana_activada");
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        TitleBar.CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    private void OnRendering(object? sender, object e) => Session.UpdateTransport(_engine.Snapshot);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Importar";
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            string[] paths = [.. items.OfType<StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p))];
            if (paths.Length == 0)
            {
                return;
            }

            if (paths.Any(ProjectPaths.IsProjectFile))
            {
                await Session.OpenFilesAsync(paths);
            }
            else
            {
                await Session.ImportMediaAsync(paths);
            }
        }
#pragma warning disable CA1031 // Soltar archivos no debe tumbar la aplicación: el error se muestra en la ventana.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash(ex.ToString());
            ReportUnexpectedError(ex.Message);
        }
    }

    /// <summary>Avisa de un error que nadie esperaba; los detalles quedan en <c>rodaje-error.txt</c>.</summary>
    internal void ReportUnexpectedError(string message) =>
        Session.Notify(NotificationSeverity.Error, "Algo falló", $"{message} Los detalles quedaron en rodaje-error.txt, junto al programa.");

    private void OnCloseModal(object sender, RoutedEventArgs e) => ClosePage();

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _options.MeasureSeconds is not null)
        {
            return;
        }

        args.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            if (await Session.ConfirmDiscardChangesAsync())
            {
                _allowClose = true;
                Close();
            }
        }
#pragma warning disable CA1031 // Si preguntar falla, la ventana sigue abierta con el trabajo, no se cae.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash(ex.ToString());
            ReportUnexpectedError(ex.Message);
        }
        finally
        {
            _closing = false;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CompositionTarget.Rendering -= OnRendering;
        _transportTimer.Stop();
        _settingsTimer.Stop();
        _keyboard.Dispose();
        foreach (IDisposable binding in _bindings)
        {
            binding.Dispose();
        }

        bool wasFullScreen = _playerFullScreen;
        if (wasFullScreen)
        {
            Player.SetFullScreen(false);
        }

        SaveLayout();
        if (wasFullScreen)
        {
            // Los paneles recién restaurados todavía no pasaron por el diseño: se guardan los tamaños de antes.
            _settings.Current.LibraryWidth = _layoutBeforeFullScreen.Library.Value;
            _settings.Current.InspectorWidth = _layoutBeforeFullScreen.Inspector.Value;
            _settings.Current.TimelineHeight = _layoutBeforeFullScreen.Timeline.Value;
        }
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        _engine.Dispose();
        _visuals.Dispose();
        _instance.Dispose();
        MeasurementRun.WriteReportIfMeasuring(_options, _engine, Program.Metrics, VideoPanel.CompositionScaleX);
    }
}
