using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Views.Plugins.Logic;
using Rodaje.Packages;

namespace Rodaje.App.Views.Plugins;

/// <summary>
/// Gestor de complementos: catálogo, instalados, instalar desde archivo, desinstalar y abrir el repositorio. Mientras está
/// visible mira cada pocos segundos si hay otras instancias de Rodaje abiertas; con ellas no se desinstala.
/// </summary>
internal sealed partial class PluginsView : UserControl, IEditorView
{
    private static readonly (string Name, string Spec) PackageFilter = ("Complementos de Rodaje", "*" + PackageManifest.FileExtension);

    private static readonly TimeSpan InstancesPoll = TimeSpan.FromSeconds(2);

    private EditorServices? _services;
    private DispatcherQueueTimer? _instancesTimer;
    private int _instances = 1;
    private int _busy;
    private readonly Dictionary<string, DownloadState> _downloads = new(StringComparer.Ordinal);

    public PluginsView()
    {
        InitializeComponent();
    }

    public ObservableCollection<PluginRow> ContentPackages { get; } = [];

    public ObservableCollection<PluginRow> Modules { get; } = [];

    public ObservableCollection<ProblemRow> Problems { get; } = [];

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        services.Packages.Changed += (_, _) => Refresh();
        IPackageDownloader downloader = services.Packages.Downloader;
        DownloadNote.Text = downloader.IsAvailable
            ? DownloadNoteText
            : (downloader.UnavailableReason ?? "") + " También puedes soltar el .paquete en la biblioteca.";
        _instances = services.Packages.ActiveInstances();
        _instancesTimer = DispatcherQueue.CreateTimer();
        _instancesTimer.Interval = InstancesPoll;
        _instancesTimer.Tick += (_, _) => CheckInstances();
        Loaded += (_, _) =>
        {
            CheckInstances();
            _instancesTimer.Start();
        };
        Unloaded += (_, _) => _instancesTimer.Stop();
        if (IsLoaded)
        {
            _instancesTimer.Start();
        }

        Refresh();
        UiTask.Run(services.Packages.RefreshAsync());
        UpdatesChecked = CheckUpdatesAsync();
    }

    /// <summary>La consulta de versiones publicadas que lanzó <see cref="Attach"/> (para la autoprueba).</summary>
    internal Task UpdatesChecked { get; private set; } = Task.CompletedTask;

    /// <summary>La nota de descarga tal como se ve (para la autoprueba).</summary>
    internal string DownloadNoteShown => DownloadNote.Text;

    private const string DownloadNoteText = "Los complementos se descargan de las releases de GitHub con un índice firmado por el proyecto; cada archivo se comprueba con su SHA-256 antes de instalarse.";

    /// <summary>Consulta las versiones publicadas para ofrecer «Actualizar»; sin conexión solo lo avisa en la nota.</summary>
    private async Task CheckUpdatesAsync()
    {
        if (_services is null || !_services.Packages.Downloader.IsAvailable)
        {
            return;
        }

        await _services.Packages.AvailableVersionsAsync();
        DownloadNote.Text = _services.Packages.UpdatesProblem is { } problem
            ? $"{DownloadNoteText} Ahora no se pudo consultar el catálogo: {problem}"
            : DownloadNoteText;
        Refresh();
    }

    private DownloadState DownloadFor(string id)
    {
        if (!_downloads.TryGetValue(id, out DownloadState? state))
        {
            state = new DownloadState();
            _downloads[id] = state;
        }

        return state;
    }

    private void CheckInstances()
    {
        if (_services is null)
        {
            return;
        }

        int count = _services.Packages.ActiveInstances();
        if (count != _instances)
        {
            _instances = count;
            UpdateInstancesNote();
            bool canUninstallNow = _instances <= 1;
            foreach (PluginRow row in ContentPackages.Concat(Modules))
            {
                row.UpdateCanUninstallNow(canUninstallNow);
            }
        }
    }

    private void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        PackageService packages = _services.Packages;
        UpdateInstancesNote();
        bool othersOpen = _instances > 1;
        // Cambios de paquetes o catálogo sí rehacen el contenido; el sondeo de instancias de CheckInstances conserva las filas.
        PropertyChangedRelay.Release(ContentPackages);
        PropertyChangedRelay.Release(Modules);
        foreach (PluginEntry entry in packages.Entries())
        {
            (entry.Kind == PackageKind.Module ? Modules : ContentPackages).Add(
                new PluginRow(entry, packages.Downloader, othersOpen, packages.HasUpdate(entry), DownloadFor(entry.Id),
                    packages.MissingModelsOf(entry.Id).Sum(m => m.Size)));
        }

        Problems.Clear();
        foreach (PackageProblem problem in packages.Installed.Problems)
        {
            Problems.Add(new ProblemRow(problem));
        }

        ContentEmptyState.Visibility = ContentPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModulesEmptyState.Visibility = Modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProblemsPanel.Visibility = Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateInstancesNote()
    {
        bool othersOpen = _instances > 1;
        InstancesNote.Visibility = othersOpen ? Visibility.Visible : Visibility.Collapsed;
        InstancesText.Text = othersOpen
            ? $"Hay {_instances} ventanas de Rodaje abiertas. {PackageService.OtherInstancesMessage} Instalar complementos nuevos sí se puede."
            : "";
    }

    private nint OwnerWindow() =>
        XamlRoot?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;

    private void OnInstallFileClick(object sender, RoutedEventArgs e) => UiTask.Run(OnInstallFileClickAsync(sender, e));

    private async Task OnInstallFileClickAsync(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy > 0)
        {
            return;
        }

        await RunBusyAsync("No se pudo instalar", async () =>
        {
            IReadOnlyList<string> picked = FileDialogs.Open(OwnerWindow(), "Instalar complemento", multiple: true,
                FileDialogFolderPolicy.OpenFolder(_services.Settings.Current), PackageFilter, FileDialogs.AllFilter);
            FileDialogFolderPolicy.RememberOpen(_services.Settings.Current, picked.Count > 0 ? picked[0] : null);
            foreach (string path in picked)
            {
                await PluginActions.InstallAsync(_services, path);
            }
        });
    }

    private void OnInstallClick(object sender, RoutedEventArgs e) => UiTask.Run(OnInstallClickAsync(sender, e));

    private async Task OnInstallClickAsync(object sender, RoutedEventArgs e)
    {
        if (_services is null || (sender as FrameworkElement)?.DataContext is not PluginRow row || row.Download.IsActive)
        {
            return;
        }

        if (row.NeedsModels)
        {
            await DownloadModelsAsync(row);
            return;
        }

        if (row.Entry.Catalog is not { } entry)
        {
            return;
        }

        DownloadState state = row.Download;
        CancellationToken cancellation = state.Start();
        try
        {
            InstallResult result = await _services.Packages.InstallFromCatalogAsync(entry, state.Progress, cancellation);
            PackageManifest manifest = result.Package.Manifest;
            string what = result.Outcome == InstallOutcome.Updated ? $"Se actualizó de la {result.PreviousVersion} a la {manifest.Version}." : $"Versión {manifest.Version}.";
            _services.Session.Notify(NotificationSeverity.Success, $"«{manifest.Name}» instalado", what);
        }
        catch (OperationCanceledException)
        {
            _services.Session.Notify(NotificationSeverity.Informational, $"Descarga de «{entry.Name}» en pausa", "Lo descargado se guardó; «Instalar» sigue desde ahí.");
        }
        catch (NotSupportedException ex)
        {
            _services.Session.Notify(NotificationSeverity.Informational, "Todavía no", ex.Message);
        }
        catch (Exception ex)
        {
            _services.Session.Notify(NotificationSeverity.Error, $"No se instaló «{entry.Name}»", ex.Message);
        }
        finally
        {
            state.Finish();
            Refresh();
        }
    }

    private async Task DownloadModelsAsync(PluginRow row)
    {
        DownloadState state = row.Download;
        CancellationToken cancellation = state.Start();
        try
        {
            await _services!.Packages.EnsureModelsAsync(row.Entry.Id, state.Progress, cancellation);
            _services.Session.Notify(NotificationSeverity.Success, $"Modelos de «{row.Name}» listos", "");
        }
        catch (OperationCanceledException)
        {
            _services!.Session.Notify(NotificationSeverity.Informational, $"Descarga de los modelos de «{row.Name}» en pausa", "Lo descargado se guardó; «Bajar modelos» sigue desde ahí.");
        }
        catch (Exception ex)
        {
            _services!.Session.Notify(NotificationSeverity.Error, $"No se bajaron los modelos de «{row.Name}»", ex.Message);
        }
        finally
        {
            state.Finish();
            Refresh();
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PluginRow row)
        {
            row.Download.Cancel();
        }
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e) => UiTask.Run(OnUninstallClickAsync(sender, e));

    private async Task OnUninstallClickAsync(object sender, RoutedEventArgs e)
    {
        if (_services is null || XamlRoot is null || (sender as FrameworkElement)?.DataContext is not PluginRow row)
        {
            return;
        }

        await RunBusyAsync($"No se desinstaló «{row.Name}»", async () =>
        {
            if (await ConfirmUninstallAsync(row))
            {
                await _services.Packages.UninstallAsync(row.Entry.Id);
                _services.Session.Notify(NotificationSeverity.Success, $"«{row.Name}» desinstalado", "");
            }
        });
    }

    private async Task<bool> ConfirmUninstallAsync(PluginRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"¿Desinstalar «{row.Name}»?",
            Content = "Los proyectos que usan sus elementos siguen abriendo; esos elementos se muestran como «falta el paquete» hasta que lo vuelvas a instalar.",
            PrimaryButtonText = "Desinstalar",
            SecondaryButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Secondary,
        };
        ContentDialogResult? result = await DialogGate.ShowAsync(dialog);
        return PluginUninstallDecision.IsConfirmed(result?.ToString());
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => UiTask.Run(OnRefreshClickAsync(sender, e));

    private async Task OnRefreshClickAsync(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            await RunBusyAsync("No se pudieron recargar los archivos locales", () => _services.Packages.RefreshAsync());
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        string folder = Path.GetDirectoryName(_services.Packages.Locations.Packages)!;
        try
        {
            Directory.CreateDirectory(_services.Packages.Locations.Packages);
            Directory.CreateDirectory(_services.Packages.Locations.Modules);
            Directory.CreateDirectory(_services.Packages.Locations.Own);
            Open(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Session.Notify(NotificationSeverity.Warning, "No se pudo abrir la carpeta", ex.Message);
        }
    }

    private void OnRepositoryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PluginRow { Repository: { } uri })
        {
            Open(uri.AbsoluteUri);
        }
    }

    private void Open(string target)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _services?.Session.Notify(NotificationSeverity.Warning, "No se pudo abrir", ex.Message);
        }
    }

    /// <summary>Hace una acción de la página sin que un error inesperado cierre la app: se avisa con <paramref name="failureTitle"/>.</summary>
    private async Task RunBusyAsync(string failureTitle, Func<Task> action)
    {
        _busy++;
        InstallFileButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _services?.Session.Notify(NotificationSeverity.Error, failureTitle, ex.Message);
        }
        finally
        {
            _busy--;
            InstallFileButton.IsEnabled = _busy == 0;
        }
    }
}
