using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Flujo de trabajo: proxies al importar y lo que se revisa con el temporizador de los ajustes.</summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private AutoProxyService CreateAutoProxies(EditorSession session)
    {
        var service = new AutoProxyService(
            () => _settings.Current.ProxyUsage == ProxyUsage.Never ? null : _engine.ProxyResolver,
            () => _engine.DedicatedVideoMemory,
            () => _settings.Current.AutoProxies && _settings.Current.ProxyUsage != ProxyUsage.Never,
            SynchronizationContext.Current);
        session.MediaImported += (_, media) =>
        {
            int count = service.OnImported(media);
            if (count > 0)
            {
                session.Notify(NotificationSeverity.Informational, "Proxies en segundo plano",
                    (count == 1 ? "Un video pesado tendrá" : $"{count} videos pesados tendrán")
                    + " una copia liviana para editar con fluidez. El avance se ve en la portada y en Trabajos; la exportación usa los originales.");
            }
        };
        return service;
    }

    private void OnWorkflowTick() => _services.AutoProxies?.Tick();

    private void AttachWorkflow()
    {
        TitleBar.WorkspaceChosen += (_, id) => ApplyWorkspace(id);
        TitleBar.SaveWorkspaceRequested += (_, _) => UiTask.Run(SaveWorkspaceAsync());
        ShortcutRegistry registry = _router.Registry;
        foreach (WorkspaceProfile profile in WorkspaceProfiles.Factory)
        {
            string id = "view.workspace." + profile.Id["fabrica.".Length..];
            registry.Register(new ShortcutAction(id, $"Espacio de trabajo «{profile.Name}»", ShortcutGroups.Windows, []));
            _bindings.Add(_router.Bind(id, () => ApplyWorkspace(profile.Id)));
        }

        _bindings.Add(_router.Bind(ActionIds.LayoutBalanced, () => SetTimelineShare(TimelineProportion.Balanced)));
        _bindings.Add(_router.Bind(ActionIds.LayoutMoreViewer, () => SetTimelineShare(TimelineProportion.MoreViewer)));
        _bindings.Add(_router.Bind(ActionIds.LayoutMoreTimeline, () => SetTimelineShare(TimelineProportion.MoreTimeline)));
        registry.Register(new ShortcutAction(WatchedFoldersActionId, "Carpetas vigiladas…", ShortcutGroups.File, []));
        _bindings.Add(_router.Bind(WatchedFoldersActionId, () => UiTask.Run(EditWatchedFoldersAsync())));
        StartWatchingFolders();
        AttachOnboarding();
    }

    private const string WatchedFoldersActionId = "file.watchedFolders";

    private readonly WatchedFolderScanner _scanner = new();
    private bool _scanning;

    /// <summary>Cada 4 s mira las carpetas vigiladas fuera del hilo de la interfaz; lo nuevo y ya copiado se ofrece en la biblioteca.</summary>
    private void StartWatchingFolders()
    {
        if (_options.IsAutomated || _services.WatchedFolders is not { } inbox)
        {
            return;
        }

        inbox.Arrived += (_, files) => Session.Notify(NotificationSeverity.Informational, "Medios nuevos", WatchedFolderInbox.Describe(files) + ".",
            new NotificationAction("Importar", () => UiTask.Run(inbox.ImportAsync())));
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.Tick += (_, _) => UiTask.Run(ScanWatchedFoldersAsync(inbox));
        timer.Start();
    }

    private async Task ScanWatchedFoldersAsync(WatchedFolderInbox inbox)
    {
        AppSettings s = _settings.Current;
        if (_scanning || _closed || s.WatchedFolders.Count == 0)
        {
            if (!_scanning && inbox.Pending.Count > 0 && s.WatchedFolders.Count == 0)
            {
                inbox.Update([]);
            }

            return;
        }

        _scanning = true;
        try
        {
            WatchedFolder[] folders = [.. s.WatchedFolders];
            string[] handled = [.. s.HandledWatchedFiles];
            var library = new HashSet<string>(Session.Project.Media.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<WatchedFile> found = await Task.Run(() => _scanner.Scan(folders, handled, library.Contains)).ConfigureAwait(true);
            if (!_closed)
            {
                inbox.Update(found);
            }
        }
        finally
        {
            _scanning = false;
        }
    }

    private async Task EditWatchedFoldersAsync()
    {
        if (Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        AppSettings s = _settings.Current;
        var list = new StackPanel { Spacing = 4 };
        void Fill()
        {
            list.Children.Clear();
            if (s.WatchedFolders.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "Ninguna todavía.", Opacity = 0.7 });
            }

            foreach (WatchedFolder folder in s.WatchedFolders.ToArray())
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = folder.Path, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var remove = new Button { Content = "Quitar" };
                Grid.SetColumn(remove, 1);
                remove.Click += (_, _) =>
                {
                    s.WatchedFolders.Remove(folder);
                    Fill();
                };
                row.Children.Add(remove);
                list.Children.Add(row);
            }
        }

        Fill();
        var add = new Button { Content = "Añadir carpeta…" };
        add.Click += (_, _) =>
        {
            if (Views.Relink.FolderDialog.Pick(_hwnd, "Carpeta que vigilar", s.LastMediaFolder) is { } picked)
            {
                WatchedFolderScanner.Add(s, picked, DateTime.UtcNow);
                ShowHint(Services.Onboarding.OnboardingGuide.FirstWatchedFolder);
                Fill();
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot, Title = "Carpetas vigiladas",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "Los videos y audios que lleguen a estas carpetas (la de la cámara, la que sincroniza el teléfono) aparecen en la biblioteca con un aviso para importarlos con un clic. Lo que ya estaba no cuenta.",
                    },
                    list,
                    add,
                },
            },
            CloseButtonText = "Listo", RequestedTheme = ElementTheme.Dark,
        };
        await DialogGate.ShowAsync(dialog).ConfigureAwait(true);
    }

    /// <summary>Lleva los paneles a la distribución elegida: anchos, alto de la línea de tiempo y pestañas del panel derecho.</summary>
    internal void ApplyWorkspace(string id)
    {
        if (WorkspaceProfiles.Find(_settings.Current, id) is not { } profile)
        {
            return;
        }

        if (_playerFullScreen)
        {
            Player.SetFullScreen(false);
        }

        WorkspaceProfiles.Apply(_settings.Current, profile);
        AppSettings s = _settings.Current;
        ApplyTimelineShare();
        LibraryColumn.Width = new GridLength(s.LibraryWidth);
        InspectorColumn.Width = new GridLength(s.InspectorWidth);
        FitSidePanels();
        Inspector.ApplyTabs(s.HiddenInspectorTabs, profile.InspectorTab);
        TitleBar.RefreshWorkspaces();
    }

    private async Task SaveWorkspaceAsync()
    {
        if (Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var name = new TextBox { PlaceholderText = "Por ejemplo, «Subtítulos»", MaxLength = 40 };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot, Title = "Guardar espacio de trabajo",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Guarda los anchos de los paneles, el alto de la línea de tiempo y las pestañas visibles.", TextWrapping = TextWrapping.Wrap },
                    name,
                },
            },
            PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark,
        };
        if (await DialogGate.ShowAsync(dialog).ConfigureAwait(true) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text))
        {
            return;
        }

        SaveLayout();
        AppSettings s = _settings.Current;
        var current = new WorkspaceProfile
        {
            LibraryWidth = s.LibraryWidth, InspectorWidth = s.InspectorWidth, TimelineShare = s.TimelineShare,
            InspectorTab = Inspector.CurrentTabName, HiddenInspectorTabs = [.. s.HiddenInspectorTabs],
        };
        WorkspaceProfile saved = WorkspaceProfiles.SaveCurrent(s, name.Text, current);
        TitleBar.RefreshWorkspaces();
        Session.Notify(NotificationSeverity.Success, "Espacio de trabajo guardado", $"«{saved.Name}» está en Ver › Espacio de trabajo.");
    }

    /// <summary>Si al cerrar (o en un apagón) quedaron exportaciones en la cola, pregunta si se reanudan.</summary>
    private async Task OfferExportQueueAsync(Task cleanup)
    {
        await cleanup.ConfigureAwait(true);
        if (_exports.Store is not { } store)
        {
            return;
        }

        IReadOnlyList<(Rodaje.Export.ExportRequest Request, bool ContinueWithoutMissing)> pending = await Task.Run(store.Load).ConfigureAwait(true);
        if (store.DamagedNotice is { } damaged && !_closed)
        {
            Session.Notify(NotificationSeverity.Warning, "Exportaciones sin terminar", damaged);
        }

        if (pending.Count == 0 || _closed)
        {
            return;
        }

        string names = string.Join(", ", pending.Take(3).Select(p => $"«{Path.GetFileName(p.Request.OutputPath)}»")) + (pending.Count > 3 ? "…" : "");
        if (Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot, Title = "Exportaciones sin terminar",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = (pending.Count == 1 ? "Quedó una exportación en la cola" : $"Quedaron {pending.Count} exportaciones en la cola")
                    + $" de la última vez: {names}. La que estaba a medias empieza de nuevo.",
            },
            PrimaryButtonText = "Reanudar", CloseButtonText = "Descartar",
            DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark,
        };
        // Si otra pregunta está abierta (recuperar un autoguardado), se espera a que se cierre.
        while (DialogGate.IsOpen && !_closed)
        {
            await Task.Delay(500).ConfigureAwait(true);
        }

        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog).ConfigureAwait(true);
        if (_closed || answer is null)
        {
            return;
        }

        if (answer == ContentDialogResult.Primary)
        {
            _exports.EnqueueRange(pending.Select(p => p.Request with { ContinueWithoutMissing = p.ContinueWithoutMissing }));
            Inspector.Show(Views.Inspector.InspectorView.Tab.Jobs);
        }
        else
        {
            store.Clear();
        }
    }
}
