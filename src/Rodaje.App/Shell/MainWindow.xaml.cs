using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.LongActions;
using Rodaje.App.Services.Autosave;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Titles;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.App.Views.Plugins;
using Rodaje.App.Views.Preferences;
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
    private NotificationAction? _notificationSecondaryAction;
    private readonly LongActionService _longActions;
    private readonly ShellDialogs _dialogs;

    // La barra de avisos muestra el avance de una acción larga hasta que llega un aviso; al cerrarse ese aviso vuelve al avance.
    private bool _showingProgress;
    private string? _dismissedAction;
    private readonly HashSet<string> _shownActions = [];
    private PluginsView? _pluginsView;
    private ExportView? _exportView;
    private readonly ExportJobList _exports;
    private readonly ModuleManager _modules;
    private readonly AutosaveService? _autosave;
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
        Program.Metrics.Milestone("ventana_inicio");
        InitializeComponent();
        Program.Metrics.Milestone("ventana_xaml");
        Theme.ThemeResources.ApplyRequestedTheme(Root);
        Theme.ThemeResources.ThemeChanged += (_, _) => Theme.ThemeResources.ApplyRequestedTheme(Root);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetUpTitleBar();
        RestoreLayout();
        StartupSplash.PlaceOver(AppWindow, _hwnd);

        // Las teclas de signos se muestran como están impresas en el teclado de la persona (español: «'» y no «[»).
        KeyNames.DisplayLayout = SystemKeyboardLayout.Instance;
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = settings.Current.ShortcutPreset;
        _shortcutStore = new ShortcutStore(ShortcutStore.DefaultPath);
        string? shortcutsWarning = _shortcutStore.Load(registry);
        _router = new ShortcutRouter(registry);
        Program.Metrics.Milestone("atajos");

        EditorSession? session = null;
        var dialogs = new ShellDialogs(this, _hwnd, settings, () => session?.FilePath);
        _dialogs = dialogs;
        session = new EditorSession(engine, prober, dialogs,
            newProjectTracks: settings.Current.NewProjectTracks, newProjectFormat: settings.Current.NewProjectFormat)
        {
            LinkedAudioTracks = settings.Current.LinkedAudioTracks,
        };
        _visuals = new ClipVisuals(id => session.Project.FindMedia(id)?.Path, new FfmpegVisualsSources(),
            new ClipVisualsOptions { CacheDirectory = ClipVisualsOptions.DefaultCacheDirectory }, SynchronizationContext.Current);
        Program.Metrics.Milestone("sesion");
        var packages = new PackageService(PackageService.DefaultLocations(), PackageService.LoadBundledCatalog(), instances: _instance);
        IEffectContent? content = EffectContentFactory.CreateOrNull(packages);
        Program.Metrics.Milestone("paquetes");
        var preview = new PreviewYield(engine, engine.SetPreviewQuality, () => settings.Current);
        _exports = new ExportJobList(new AppExportRunner(content, preview, () => TitleFontFiles.Find(packages.Installed)), SynchronizationContext.Current)
        {
            Concurrency = new ExportConcurrency(Environment.ProcessorCount, () => engine.DedicatedVideoMemory)
            {
                Profile = () => Rodaje.Media.Gpu.GpuProfileReader.Current,
            },
            Store = options.IsAutomated ? null : new ExportQueueStore(ExportQueueStore.DefaultPath),
        };
        _modules = new ModuleManager(new ModuleManagerOptions
        {
            Roots = ModuleCatalog.DefaultRoots(),
            DedicatedVideoMemory = () => engine.DedicatedVideoMemory,
            Ai = AiProfile.ForThisMachine(() => settings.Current.AiQuality),
            ClientOptions = new() { AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
        }, new ModulePreviewYield(preview), SynchronizationContext.Current);
        _modules.Failed += (_, f) => session.Notify(NotificationSeverity.Error, f.Title, f.Message);
        packages.CloseModule = _modules.CloseModuleAsync;
        packages.Changed += (_, _) => UiTask.Run(Task.Run(_modules.Refresh));
        Program.Metrics.Milestone("modulos");
        _longActions = new LongActionService(session);
        _longActions.Changed += (_, _) => _dispatcher.RunLater(ShowLongActions);
        _longActions.Changed += (_, _) => _dispatcher.RunLater(() => UpdateJobsButton(JobsPanel.ActiveCount));
        _services = new EditorServices(session, _router, settings, engine, this)
        {
            Visuals = _visuals, Packages = packages, Content = content, Exports = _exports,
            Modules = _modules, Proposals = new ModuleProposals(session), LongActions = _longActions,
            AutoProxies = CreateAutoProxies(session),
            WatchedFolders = new WatchedFolderInbox(session, () => settings.Current),
        };
        engine.SetContent(content);
        IReadOnlyList<string> fonts = TitleFontFiles.Find(packages.Installed);
        engine.SetTitleFonts(fonts);
        packages.Changed += (_, _) =>
        {
            if (_closed)
            {
                return;
            }

            IReadOnlyList<string> now = TitleFontFiles.Find(packages.Installed);
            if (!now.SequenceEqual(fonts, StringComparer.OrdinalIgnoreCase))
            {
                fonts = now;
                engine.SetTitleFonts(now);
            }
        };
        Program.Metrics.Milestone("fuentes");
        Views.Export.ExportPresetLibrary.Packages = PackageExportPresets.Load(packages.Installed);
        Services.Inspector.EffectCatalog.EnabledFeatures = Services.Inspector.EffectCatalog.FeaturesFrom(packages.Installed);
        packages.Changed += (_, _) =>
        {
            Views.Export.ExportPresetLibrary.Packages = PackageExportPresets.Load(packages.Installed);
            Services.Inspector.EffectCatalog.EnabledFeatures = Services.Inspector.EffectCatalog.FeaturesFrom(packages.Installed);
        };
        MissingContent.Attach(session, packages);
        Views.Relink.RelinkDialog.Attach(_services, _hwnd);
        _bindings.Add(EditorShortcuts.Bind(_router, session));
        _bindings.Add(_router.Bind(ActionIds.Export, () => ShowPage(ShellPage.Export)));
        _bindings.Add(Views.Export.StillExportCommand.Bind(_router, _services, _hwnd));
        _bindings.Add(_router.Bind(ActionIds.ShowShortcuts, () => ShowPage(ShellPage.Shortcuts)));
        _bindings.Add(_router.Bind(ActionIds.ShowPreferences, ShowPreferences));
        _bindings.Add(_router.Bind(ActionIds.ShowPlugins, () => ShowPage(ShellPage.Plugins)));
        _bindings.Add(_router.Bind(ActionIds.ShowMixer, ToggleMixer));
        _bindings.Add(_router.Bind(ActionIds.ShowHistory, () => HistoryButton.Flyout.ShowAt(HistoryButton)));
        _bindings.Add(Views.Analysis.AnalysisCommands.Bind(_router, _services));
        _bindings.Add(Views.Timeline.TransitionCommands.Bind(_router, _services));
        _bindings.Add(Views.Voice.VoiceCommands.Bind(_router, _services));
        _bindings.Add(Views.Subtitles.SubtitleCommands.Bind(_router, _services));
        _bindings.Add(Views.Subtitles.SubtitleFileCommands.Bind(_router, _services));
        _bindings.Add(Views.Translate.TranslateCommands.Bind(_router, _services));
        _bindings.Add(Views.Raw.RawCommands.Bind(_router, _services));
        _bindings.Add(Views.Vision.VisionCommands.Bind(_router, _services));
        _keyboard = new KeyboardShortcutHost(Root, _router, InterceptKey);

        TitleBar.Attach(_services);
        TitleBar.InspectorTabRequested += (_, tab) => Inspector.Show(tab);
        TitleBar.MarkersPanelRequested += (_, _) => Library.ShowMarkers();
        TitleBar.ExitRequested += (_, _) => RequestClose();
        Library.Attach(_services);
        Player.Attach(_services);
        Inspector.Attach(_services);
        Inspector.GlobalPanelRequested += (_, tab) => ShowGlobalPanel(tab);
        HistoryPanel.Attach(_services);
        JobsPanel.ActiveCountChanged += (_, active) => UpdateJobsButton(active);
        JobsPanel.Attach(_services);
        Timeline.Attach(_services);
        Timeline.MarkerContextRequested += (_, id) => Library.ShowMarkers(id);
        Mixer.Attach(_services);
        Timeline.ClipVisuals = _visuals;
        AttachWorkflow();
        Program.Metrics.Milestone("vistas_unidas");

        session.Notified += (_, n) => ShowNotification(n);
        session.ProjectOpened += (_, _) => RememberProject();
        session.ProjectOpened += (_, _) =>
        {
            _visuals.ForgetRemovedMedia();
            UiTask.Run(_visuals.RetryFailedAsync());
        };
        // Exportar se quedaría con la secuencia, el nombre y el rango del proyecto anterior (Ctrl+N y Ctrl+O siguen con ella abierta).
        session.ProjectOpened += (_, _) => ClosePage();
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
        Notification.Closed += (_, _) =>
        {
            if (_showingProgress)
            {
                // Cerrada a mano mientras corre: no se vuelve a abrir por el avance de esa misma acción.
                _showingProgress = false;
                _dismissedAction = _longActions.Current?.ActionId;
            }
            else
            {
                ShowLongActions();
            }
        };

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
        _settingsTimer.Tick += (_, _) => OnWorkflowTick();

        // Autoguardado (no en las mediciones ni en la autoprueba): el mismo temporizador decide si toca una copia.
        if (!options.IsAutomated)
        {
            var autosave = new AutosaveService(session, dialogs, AutosaveService.DefaultRoot);
            _autosave = autosave;
            if (autosave.Start())
            {
                _settingsTimer.Tick += (_, _) => autosave.Tick();
            }
        }

        _settingsTimer.Start();

        LibrarySplitter.Resized += (_, size) => settings.Current.LibraryWidth = size;
        InspectorSplitter.Resized += (_, size) => settings.Current.InspectorWidth = size;
        Root.SizeChanged += (_, e) =>
        {
            FitSidePanels(e.NewSize.Width);
            ApplyTimelineShare();
        };
        TimelineSplitter.MinSize = TimelineProportion.MinHeight;
        TimelineSplitter.Resized += (_, size) => settings.Current.TimelineShare = TimelineProportion.ShareOf(size, Root.ActualHeight);
        TimelineSplitter.DoubleClicked += (_, _) => SetTimelineShare(TimelineProportion.Toggle(settings.Current.TimelineShare));
        // Un aviso abierto empuja la página modal hacia abajo en lugar de taparle la cabecera.
        Notification.SizeChanged += (_, e) => ModalFrame.Margin = new Thickness(48, 56 + e.NewSize.Height, 48, 40);
        Activated += OnActivated;
        SizeChanged += (_, _) => ApplyUiScale();
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;

        foreach (string? warning in new[] { settingsWarning, shortcutsWarning })
        {
            if (warning is not null)
            {
                session.Notify(NotificationSeverity.Warning, "Ajustes", warning);
            }
        }

        if (options.Files.Count > 0 || options.SelfTest)
        {
            UiTask.Run(OpenStartupFilesAsync(options.Files));
        }

        StartupTrace.Watch(Program.Metrics, OnStartupUsable);
        if (StartupSplash.IsShown)
        {
            // Por si WinUI no pintara mientras está oculta: a los 3 s se muestra igual.
            DispatcherQueueTimer reveal = _dispatcher.CreateTimer();
            reveal.Interval = TimeSpan.FromSeconds(3);
            reveal.IsRepeating = false;
            reveal.Tick += (_, _) => StartupSplash.Reveal(_hwnd);
            reveal.Start();
        }

        Program.Metrics.Milestone("ventana_lista");
    }

    /// <summary>
    /// Lo que no hace falta para ver la ventana empieza después de su primer cuadro: el motor (FFmpeg y la GPU, si no
    /// arrancó antes para abrir archivos), los módulos de IA y la limpieza de cachés y exportaciones a medias.
    /// </summary>
    private void OnStartupUsable()
    {
        StartupSplash.Reveal(_hwnd);
        if (_options.Files.Count == 0)
        {
            _engine.Start();
            Program.Metrics.Milestone("motor_lanzado");
        }

        UiTask.Run(Task.Run(_modules.Refresh));
        UiTask.Run(_visuals.TrimDiskCacheAsync());
        UiTask.Run(OfferExportQueueAsync(Task.Run(() => Rodaje.Export.Running.ExportRunner.CleanupStale())));
        UiTask.Run(OfferTourAsync());
        if (_options.StartupProbe)
        {
            UiTask.Run(StartupTrace.FinishProbeAsync(Program.Metrics, _options.LogPath, _options.Files.Count > 0, () => _engine.FailureMessage, Close));
        }
    }

    public XamlRoot? XamlRoot => Root.XamlRoot;

    internal EditorSession Session => _services.Session;

    internal ShortcutRouter Router => _router;

    internal KeyboardShortcutHost Keyboard => _keyboard;

    private void ShowPreferences() => UiTask.Run(ShowPreferencesAsync());

    private async Task ShowPreferencesAsync()
    {
        var view = new PreferencesView();
        SaveLayout();
        AppSettings current = _settings.Current;
        view.Attach(current, _router.Registry, new WorkspaceProfile
        {
            LibraryWidth = current.LibraryWidth, InspectorWidth = current.InspectorWidth, TimelineShare = current.TimelineShare,
            InspectorTab = Inspector.CurrentTabName, HiddenInspectorTabs = [.. current.HiddenInspectorTabs],
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Preferencias",
            Content = view,
            PrimaryButtonText = "Guardar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await DialogGate.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            view.Discard();
            return;
        }

        PreviewQuality quality = _settings.Current.PreviewQuality;
        view.Apply();
        if (_settings.Current.PreviewQuality != quality)
        {
            _services.SetPreviewQuality(_settings.Current.PreviewQuality);
        }

        _services.Session.NewProjectTracks = _settings.Current.NewProjectTracks;
        _services.Session.NewProjectFormat = _settings.Current.NewProjectFormat;
        _services.Session.LinkedAudioTracks = _settings.Current.LinkedAudioTracks;
        _services.Player.SetProxyUsage(_settings.Current.ProxyUsage);
        _services.Player.TenBitViewer = _settings.Current.TenBitViewer;
        _services.Player.HdrViewer = _settings.Current.HdrViewer;
        _router.Registry.Preset = _settings.Current.ShortcutPreset;
        if (view.WorkspaceToApply is { } workspace)
        {
            ApplyWorkspace(workspace);
        }

        if (view.ProfileImported)
        {
            _shortcutStore.Save(_router.Registry);
            TitleBar.RefreshWorkspaces();
        }

        ApplyUiScale();
        _settings.SaveIfChanged();
    }

    internal nint Hwnd => _hwnd;

    internal Button ExportButton => TitleBar.ExportButton;

    internal HyperlinkButton UpstreamCredit => TitleBar.UpstreamCredit;

    internal SwapChainPanel VideoPanel => Player.VideoPanel;

    internal PlayerView PlayerPane => Player;

    internal Services.Settings.AppSettings Settings => _settings.Current;

    /// <summary>Para la autoprueba: lo que muestra la barra de avisos.</summary>
    internal (bool Open, string Title, bool Progress, string? Button) NotificationState =>
        (Notification.IsOpen, Notification.Title, NotificationProgress.Visibility == Visibility.Visible,
            NotificationButton.Visibility == Visibility.Visible ? NotificationButton.Content as string : null);

    internal Services.LongActions.LongActionService LongActions => _longActions;

    internal Views.Inspector.InspectorView InspectorPane => Inspector;

    internal ExportView? ExportPage => _exportView;

    internal ShortcutsView? ShortcutsPage => _shortcutsView;

    internal PluginsView? PluginsPage => _pluginsView;

    /// <summary>El desplegable de herramientas rápidas, para la autoprueba.</summary>
    internal Views.Tools.ToolsBar QuickTools => Timeline.QuickToolsMenu;

    internal ExportJobList Exports => _exports;

    internal ModuleManager Modules => _modules;

    public void ShowPage(ShellPage page)
    {
        JobsButton.Flyout?.Hide();
        UserControl view;
        if (page == ShellPage.Shortcuts)
        {
            if (_shortcutsView is null)
            {
                _shortcutsView = new ShortcutsView();
                _shortcutsView.Attach(_services);
            }

            view = _shortcutsView;
            ModalTitle.Text = "Atajos de teclado y ratón";
        }
        else if (page == ShellPage.Export)
        {
            if (ModalContent.Content is ExportView)
            {
                // Ctrl+E con Exportar ya abierta no la rehace: se perdería lo cambiado en Avanzado.
                return;
            }

            if (_exportView is null)
            {
                _exportView = new ExportView(_services, _exports, _hwnd);
                _exportView.CloseRequested += (_, _) => ClosePage();
                _exportView.Started += (_, entry) => OnExportStarted(entry);
            }

            view = _exportView;
            ModalTitle.Text = "Exportar";
            UiTask.Run(OpenExportAsync(_exportView));
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
        Root.RowDefinitions[3].Height = new GridLength(fullScreen ? 0 : 8);
        Visibility chrome = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement element in new UIElement[] { TitleBar, Library, LibrarySplitter, Inspector, InspectorSplitter, TimelineSplitter, Timeline })
        {
            element.Visibility = chrome;
        }

        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        Player.SetFullScreen(fullScreen);
        FitSidePanels();
        if (fullScreen)
        {
            ShowHint(Services.Onboarding.OnboardingGuide.FirstFullScreen);
        }
    }

    private async Task OpenExportAsync(ExportView view)
    {
        try
        {
            await view.OpenAsync();
        }
#pragma warning disable CA1031 // Si la sonda o el diálogo fallan, se avisa y el editor sigue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            string? report = Program.WriteCrash(ex.ToString());
            ClosePage();
            ReportUnexpectedError(UnexpectedErrorNotice.Describe(report));
        }
    }

    private void OnExportStarted(ExportEntry entry)
    {
        ShowGlobalPanel(Views.Inspector.InspectorView.Tab.Jobs);
        string mode = entry.Request.Settings.Performance == Rodaje.Export.ExportPerformance.Fastest ? "con alto rendimiento" : "en segundo plano";
        Session.Notify(NotificationSeverity.Informational, "Exportando",
            $"«{entry.Title}» se exporta {mode}; puedes seguir editando. El avance está en Trabajos.");
    }

    private bool InterceptKey(KeyChord chord)
    {
        // Con los atajos suspendidos (la página de atajos captura una tecla), Esc es de quien captura.
        if (_router.IsSuspended)
        {
            return false;
        }

        if (Player.HandleMaskKey(chord))
        {
            return true;
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
        if (files.Count > 0)
        {
            await Session.OpenFilesAsync(files);
        }

        if (_options.MeasureSeconds is { } seconds)
        {
            await new MeasurementRun(this, _engine, Program.Metrics).RunAsync(seconds, _options.Scenario);
        }
        else if (_options.SelfTest)
        {
            await SelfTestRun.RunAsync(this, _engine, Program.Metrics);
        }
    }

    /// <summary>
    /// La barra de título propia. La región de arrastre no se deja a <c>SetTitleBar</c>: con la escala de la interfaz
    /// (Preferencias) la raíz se dibuja transformada y la región se calcula aquí en píxeles con esa escala y la del monitor
    /// (<see cref="TitleBarDragRegion"/>), y se recalcula al cambiar el tamaño de la ventana, de la barra o la escala.
    /// </summary>
    private void SetUpTitleBar()
    {
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ExtendsContentIntoTitleBar = true;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, AppIcon.FileName));
        bar.PreferredHeightOption = TitleBarHeightOption.Tall;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        ApplyTitleBarColors();
        Theme.ThemeResources.ThemeChanged += (_, _) => ApplyTitleBarColors();
        TitleBar.DragRegion.Loaded += (_, _) => UpdateDragRegion();
        TitleBar.DragRegion.SizeChanged += (_, _) => UpdateDragRegion();
    }

    // Los botones de la ventana (minimizar, maximizar, cerrar) siguen al tema, también cuando cambia en caliente.
    private void ApplyTitleBarColors()
    {
        AppWindowTitleBar bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = Theme.ThemeResources.SolidColor("TextBrush");
        bar.ButtonHoverForegroundColor = Theme.ThemeResources.SolidColor("TextBrush");
        bar.ButtonHoverBackgroundColor = Theme.ThemeResources.SolidColor("ControlHoverBrush");
        bar.ButtonPressedBackgroundColor = Theme.ThemeResources.SolidColor("ControlPressedBrush");
        bar.ButtonInactiveForegroundColor = Theme.ThemeResources.SolidColor("TextMutedBrush");
    }

    private void UpdateDragRegion()
    {
        // Antes de cargarse en el árbol (la activación llega antes), TransformToVisual lanza E_UNEXPECTED; Loaded lo repite.
        if (_closed || !TitleBar.DragRegion.IsLoaded)
        {
            return;
        }

        uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
        global::Windows.Foundation.Point origin = TitleBar.DragRegion.TransformToVisual(Root).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        AppWindowTitleBar bar = AppWindow.TitleBar;
        TitleBarDragRegion.Rect? region = TitleBarDragRegion.Compute(origin.X, origin.Y, TitleBar.DragRegion.ActualWidth, TitleBar.DragRegion.ActualHeight,
            _settings.Current.UiScale / 100.0, Math.Max(1u, dpi) / 96.0, AppWindow.ClientSize.Width, bar.LeftInset, bar.RightInset);
        RectInt32[] rects = region is { } r ? [new RectInt32(r.X, r.Y, r.Width, r.Height)] : [];
        bar.SetDragRectangles(rects);
    }

    private void RestoreLayout()
    {
        AppSettings s = _settings.Current;
        LibraryColumn.Width = new GridLength(s.LibraryWidth);
        InspectorColumn.Width = new GridLength(s.InspectorWidth);
        uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
        double width = s.WindowWidth > 0 ? s.WindowWidth : 1440;
        double height = s.WindowHeight > 0 ? s.WindowHeight : 900;
        // 1440×900 en un portátil, o el tamaño guardado en un monitor más grande, no pueden pasar bajo la barra de tareas.
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var wanted = new ScreenRect(AppWindow.Position.X, AppWindow.Position.Y, (int)(width * dpi / 96), (int)(height * dpi / 96));
        ScreenRect fit = wanted.FitInside(new ScreenRect(work.X, work.Y, work.Width, work.Height));
        width = fit.Width * 96.0 / dpi;
        height = fit.Height * 96.0 / dpi;
        // Hasta el primer diseño no se sabe el alto de las demás filas: se estima y SizeChanged lo corrige.
        TimelineRow.Height = new GridLength(TimelineProportion.Height(s.TimelineShare, height, 100));
        AppWindow.MoveAndResize(new RectInt32(fit.X, fit.Y, fit.Width, fit.Height));
        if (s.WindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        FitSidePanels(width);
    }

    /// <summary>
    /// Escala de la interfaz (Preferencias › Personalización): la raíz se dibuja escalada y mide el tamaño de la ventana
    /// dividido por la escala, así todo se recoloca de verdad. Los diálogos y menús flotantes quedan al 100 %.
    /// </summary>
    private void ApplyUiScale()
    {
        double scale = _settings.Current.UiScale / 100.0;
        if (scale == 1 && Root.RenderTransform is null)
        {
            UpdateDragRegion();
            return;
        }

        uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
        double width = AppWindow.ClientSize.Width * 96.0 / Math.Max(1u, dpi);
        double height = AppWindow.ClientSize.Height * 96.0 / Math.Max(1u, dpi);
        if (scale == 1 || width <= 0 || height <= 0)
        {
            Root.RenderTransform = null;
            Root.Width = double.NaN;
            Root.Height = double.NaN;
            Root.HorizontalAlignment = HorizontalAlignment.Stretch;
            Root.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            Root.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            Root.HorizontalAlignment = HorizontalAlignment.Left;
            Root.VerticalAlignment = VerticalAlignment.Top;
            Root.Width = width / scale;
            Root.Height = height / scale;
        }

        // La barra no cambia de tamaño al escalar (la raíz mide lo mismo dividido por la escala): la región se recalcula aquí.
        UpdateDragRegion();
    }

    /// <summary>
    /// Con la ventana estrecha los paneles laterales ceden (primero la biblioteca, luego el panel derecho) para que el
    /// reproductor conserve su mínimo y nada quede fuera de la ventana; al ensancharla vuelven a los anchos elegidos.
    /// </summary>
    private void FitSidePanels(double? availableWidth = null)
    {
        uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
        double winWidth = dpi > 0 ? AppWindow.Size.Width * 96.0 / dpi : AppWindow.Size.Width;
        double width = availableWidth > 0 ? availableWidth.Value : (winWidth > 0 ? winWidth : Root.ActualWidth);
        if (_playerFullScreen || width <= 0)
        {
            return;
        }

        AppSettings s = _settings.Current;
        var (library, inspector) = SidePanelLayout.Fit(width, s.LibraryWidth, s.InspectorWidth);
        Library.Visibility = LibrarySplitter.Visibility = library > 0 ? Visibility.Visible : Visibility.Collapsed;
        Inspector.Visibility = InspectorSplitter.Visibility = inspector > 0 ? Visibility.Visible : Visibility.Collapsed;
        MainArea.ColumnDefinitions[1].Width = new GridLength(library > 0 ? 4 : 0);
        MainArea.ColumnDefinitions[3].Width = new GridLength(inspector > 0 ? 4 : 0);
        LibraryColumn.Width = new GridLength(library);
        InspectorColumn.Width = new GridLength(inspector);
    }

    /// <summary>
    /// El alto de la línea de tiempo sale de su proporción del alto de la ventana (<see cref="TimelineProportion"/>), así
    /// se adapta al cambiar el tamaño; no con el visor a pantalla completa ni con el mezclador, que tienen su propio alto.
    /// </summary>
    private void ApplyTimelineShare()
    {
        if (_playerFullScreen || Mixer.Visibility == Visibility.Visible || Root.ActualHeight <= 0)
        {
            return;
        }

        double others = 0;
        for (int i = 0; i < Root.RowDefinitions.Count; i++)
        {
            if (i != 2 && i != 4)
            {
                others += Root.RowDefinitions[i].ActualHeight;
            }
        }

        TimelineRow.Height = new GridLength(TimelineProportion.Height(_settings.Current.TimelineShare, Root.ActualHeight, others));
    }

    /// <summary>Ver › Distribución y el doble clic en el separador.</summary>
    internal void SetTimelineShare(double share)
    {
        if (_playerFullScreen)
        {
            SetPlayerFullScreen(false);
        }

        _settings.Current.TimelineShare = TimelineProportion.Normalize(share);
        ApplyTimelineShare();
    }

    private void SaveLayout()
    {
        AppSettings s = _settings.Current;
        // Con el visor a pantalla completa el presenter no dice si la ventana estaba maximizada: se queda lo guardado antes.
        if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            s.WindowMaximized = maximized;
            if (!maximized && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                uint dpi = GetDpiForWindow((TerraFX.Interop.Windows.HWND)_hwnd);
                s.WindowWidth = AppWindow.Size.Width * 96.0 / dpi;
                s.WindowHeight = AppWindow.Size.Height * 96.0 / dpi;
            }
        }

        // Los anchos de los paneles laterales y la proporción de la línea de tiempo se guardan al soltar su separador.
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
        _showingProgress = false;
        NotificationProgress.Visibility = Visibility.Collapsed;
        NotificationButton.IsEnabled = true;
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
        _notificationSecondaryAction = notification.SecondaryAction;
        NotificationSecondaryButton.Content = notification.SecondaryAction?.Text;
        NotificationSecondaryButton.Visibility = notification.SecondaryAction is null ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(NotificationSecondaryButton, notification.SecondaryAction?.Text ?? "");
        Notification.IsOpen = true;
        FrameworkElementAutomationPeer.FromElement(Notification)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, notification.Message, "rodaje.aviso");
        _notificationTimer.Stop();
        if (notification.Severity is NotificationSeverity.Informational or NotificationSeverity.Success)
        {
            // Con botón dura más: da tiempo a leerlo y a decidir si se deshace.
            _notificationTimer.Interval = TimeSpan.FromSeconds(notification.Action is null && notification.SecondaryAction is null ? 6 : 10);
            _notificationTimer.Start();
        }
    }

    private void OnNotificationButtonClick(object sender, RoutedEventArgs e)
    {
        if (_showingProgress)
        {
            if (_longActions.Current is { CanCancel: true } running)
            {
                _longActions.Cancel(running.ActionId);
                NotificationButton.IsEnabled = false;
            }

            return;
        }

        NotificationAction? action = _notificationAction;
        _notificationAction = null;
        _notificationSecondaryAction = null;
        _notificationTimer.Stop();
        Notification.IsOpen = false;
        action?.Run();
    }

    private void OnNotificationSecondaryButtonClick(object sender, RoutedEventArgs e)
    {
        NotificationAction? action = _notificationSecondaryAction;
        _notificationAction = null;
        _notificationSecondaryAction = null;
        _notificationTimer.Stop();
        Notification.IsOpen = false;
        action?.Run();
    }

    /// <summary>
    /// El avance de la acción larga más reciente en la barra de avisos: título, mensaje, barra (indeterminada si no se sabe
    /// cuánto falta) y «Cancelar» si la acción lo admite. Un aviso nuevo la tapa; cuando no queda nada en marcha se cierra.
    /// </summary>
    private void ShowLongActions()
    {
        LongActionStatus? current = _longActions.Current;
        if (current is null)
        {
            _dismissedAction = null;
            _shownActions.Clear();
            if (_showingProgress)
            {
                _showingProgress = false;
                NotificationProgress.Visibility = Visibility.Collapsed;
                Notification.IsOpen = false;
            }

            return;
        }

        if (current.ActionId == _dismissedAction)
        {
            return;
        }

        _dismissedAction = null;
        _shownActions.IntersectWith(_longActions.Running.Select(r => r.ActionId));
        bool started = _shownActions.Add(current.ActionId);
        if (!started && !_showingProgress && Notification.IsOpen)
        {
            // Llegó un aviso después de que empezó: se queda a la vista; el botón de la barra dice que sigue en marcha.
            return;
        }

        // Una acción que acaba de empezar tapa el aviso anterior: es lo que la persona acaba de pedir.
        bool fresh = started || !_showingProgress;
        _showingProgress = true;
        _notificationAction = null;
        _notificationSecondaryAction = null;
        NotificationSecondaryButton.Visibility = Visibility.Collapsed;
        _notificationTimer.Stop();
        Notification.Severity = InfoBarSeverity.Informational;
        Notification.Title = current.Title;
        Notification.Message = current.Message.Length > 0 ? current.Message : "En marcha…";
        NotificationProgress.Visibility = Visibility.Visible;
        NotificationProgressBar.IsIndeterminate = current.Progress is null;
        NotificationProgressBar.Value = current.Progress ?? 0;
        NotificationProgressText.Text = current.ProgressText;
        NotificationButton.Content = "Cancelar";
        NotificationButton.Visibility = current.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(NotificationButton, "Cancelar");
        if (fresh)
        {
            NotificationButton.IsEnabled = true;
            Notification.IsOpen = true;
            FrameworkElementAutomationPeer.FromElement(Notification)?.RaiseNotificationEvent(
                AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, current.Title + ": en marcha", "rodaje.avance");
        }
    }

    private void UpdateJobsButton(int activeJobs)
    {
        int active = activeJobs + _longActions.Running.Count;
        JobsButtonText.Text = active > 0 ? $"Trabajos ({active})" : "Trabajos";
        JobsProgress.IsActive = active > 0;
        JobsProgress.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowGlobalPanel(Views.Inspector.InspectorView.Tab tab)
    {
        if (tab == Views.Inspector.InspectorView.Tab.History)
        {
            HistoryButton.Flyout?.ShowAt(HistoryButton);
        }
        else if (tab == Views.Inspector.InspectorView.Tab.Jobs)
        {
            JobsButton.Flyout?.ShowAt(JobsButton);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_activatedOnce)
        {
            return;
        }

        _activatedOnce = true;
        Program.Metrics.Milestone("ventana_activada");
        // Escala de la interfaz: si aún no se conoce (0), GridLength con infinito lanzaría.
        double scale = Root.XamlRoot?.RasterizationScale is double s && s > 0 && double.IsFinite(s) ? s : 1;
        TitleBar.CaptionInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        UpdateDragRegion();
        Theme.ThemeResources.FollowWindows(_dispatcher, () => _settings.Current);
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

    private void OnDrop(object sender, DragEventArgs e) => UiTask.Run(OnDropAsync(e));

    private async Task OnDropAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            // Como soltar en la biblioteca: los paquetes se instalan y una carpeta entra con sus medios.
            await Views.Library.LibraryView.DropFilesAsync(_services, e.DataView);
        }
#pragma warning disable CA1031 // Soltar archivos no debe tumbar la aplicación: el error se muestra en la ventana.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportUnexpectedError(UnexpectedErrorNotice.Describe(Program.WriteCrash(ex.ToString())));
        }
    }

    /// <summary>Avisa de un error que nadie esperaba, con el texto de <see cref="UnexpectedErrorNotice.Describe"/>.</summary>
    internal void ReportUnexpectedError(string notice) => Session.Notify(NotificationSeverity.Error, UnexpectedErrorNotice.Title, notice);

    private void OnCloseModal(object sender, RoutedEventArgs e) => ClosePage();

    /// <summary>
    /// «Recuperar» si la sesión anterior dejó cambios sin guardar, con <paramref name="also"/> en el mismo aviso (ver
    /// <see cref="AutosaveService.OfferRecoveryAsync"/>); <c>null</c> si no hay nada que recuperar.
    /// </summary>
    internal Task<NotificationAction?> OfferRecoveryAsync(NotificationAction? also) =>
        _autosave is { IsActive: true } autosave ? autosave.OfferRecoveryAsync(also) : Task.FromResult<NotificationAction?>(null);

    // args.Cancel se pone dentro del evento; las preguntas van después.
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || _options.IsAutomated)
        {
            return;
        }

        args.Cancel = true;
        UiTask.Run(ConfirmCloseAsync());
    }

    // Archivo › Salir: Window.Close() no lanza AppWindow.Closing, así que pasa por las mismas preguntas que la X.
    private void RequestClose()
    {
        if (_options.IsAutomated)
        {
            Close();
        }
        else
        {
            UiTask.Run(ConfirmCloseAsync());
        }
    }

    /// <summary>
    /// Para la X, Alt+F4 y Archivo › Salir: pregunta por los cambios sin guardar y por los trabajos en curso, y cierra.
    /// </summary>
    private async Task ConfirmCloseAsync()
    {
        if (_closing)
        {
            return;
        }

        if (DialogGate.IsOpen)
        {
            // Las preguntas no caben con otro diálogo abierto: sin el aviso parecería que la X no hace nada.
            Session.Notify(NotificationSeverity.Informational, ClosingNotice.DialogOpenTitle, ClosingNotice.DialogOpen);
            return;
        }

        _closing = true;
        try
        {
            // Con el proyecto guardado, cerrar con una exportación, un módulo de IA o una acción larga en curso los mataría sin avisar.
            if (await Session.ConfirmDiscardChangesAsync()
                && (ClosingNotice.Describe(JobsPanel.ActiveCount + _longActions.Running.Count) is not { } busy
                    || await _dialogs.ConfirmAsync(ClosingNotice.Title, busy, ClosingNotice.Confirm)))
            {
                _allowClose = true;
                Close();
            }
        }
#pragma warning disable CA1031 // Si preguntar falla, la ventana sigue abierta con el trabajo, no se cae.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportUnexpectedError(UnexpectedErrorNotice.Describe(Program.WriteCrash(ex.ToString())));
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
        StartupSplash.Close();
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

        Player.CloseExternal();
        _autosave?.Complete();
        _exports.Dispose();
        // Pide shutdown a los módulos abiertos; si alguno no sale, lo mata (y el Job Object cubre el resto).
        // Un fallo al cerrarlos (Wait lo relanza como AggregateException) se informa y no salta el resto del cierre.
        try
        {
            _modules.CloseOnExitAsync().Wait();
        }
#pragma warning disable CA1031 // Ver arriba.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            UiTask.Report(ex, notify: false);
        }

        _engine.Dispose();
        _visuals.Dispose();
        _instance.Dispose();
        MeasurementRun.WriteReportIfMeasuring(_options, _engine, Program.Metrics, VideoPanel.CompositionScaleX);
    }

    /// <summary>Un módulo de GPU baja la vista previa como una exportación con poca VRAM (proxies y Mitad), con el mismo contador.</summary>
    private sealed class ModulePreviewYield(PreviewYield preview) : IModulePreview
    {
        private static readonly Rodaje.Export.Running.ExportPerformanceProfile LowerOnly = new(
            ThreadPriority.Normal, System.Diagnostics.ProcessPriorityClass.Normal, 0, 0, PausePreview: false, PreviewProxies: true);

        public IDisposable Lower() => preview.Yield(LowerOnly);
    }

    /// <summary>
    /// El mezclador ocupa el lugar de la línea de tiempo, con alto para sus faders (al menos 520 px si la ventana deja 240
    /// al visor); otra vez vuelve a la línea de tiempo con su proporción.
    /// </summary>
    internal void ToggleMixer()
    {
        bool show = Mixer.Visibility != Visibility.Visible;
        Mixer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            double room = Root.ActualHeight - 240 - 80;
            TimelineRow.Height = new GridLength(Math.Max(TimelineRow.ActualHeight, Math.Min(520, room)));
        }
        else
        {
            ApplyTimelineShare();
        }
    }
}
