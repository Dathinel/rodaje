using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Packages;
using Rodaje.App.Shell;
using Rodaje.Paquetes;

namespace Rodaje.App.Views.Plugins;

/// <summary>Gestor de complementos: catálogo, instalados, instalar desde archivo, desinstalar y abrir el repositorio.</summary>
internal sealed partial class PluginsView : UserControl, IEditorView
{
    private static readonly (string Name, string Spec) PackageFilter = ("Complementos de Rodaje", "*" + PackageManifest.FileExtension);

    private EditorServices? _services;
    private int _busy;

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
            ? "Los complementos se descargan de las releases de GitHub y se comprueba su hash antes de instalarlos."
            : "Descarga desde GitHub: próximamente. Mientras tanto, baja el archivo .paquete de la release del repositorio y usa «Instalar desde archivo…», o suéltalo en la biblioteca.";
        Refresh();
        _ = services.Packages.RefreshAsync();
    }

    private void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        PackageService packages = _services.Packages;
        ContentPackages.Clear();
        Modules.Clear();
        foreach (PluginEntry entry in packages.Entries())
        {
            (entry.Kind == PackageKind.Module ? Modules : ContentPackages).Add(new PluginRow(entry, packages.Downloader));
        }

        Problems.Clear();
        foreach (PackageProblem problem in packages.Installed.Problems)
        {
            Problems.Add(new ProblemRow(problem));
        }

        ProblemsPanel.Visibility = Problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private nint OwnerWindow() =>
        XamlRoot?.ContentIslandEnvironment is { } environment ? Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;

    private async void OnInstallFileClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || _busy > 0)
        {
            return;
        }

        await RunBusyAsync("No se pudo instalar", async () =>
        {
            IReadOnlyList<string> picked = FileDialogs.Open(OwnerWindow(), "Instalar complemento", multiple: true, null, PackageFilter, FileDialogs.AllFilter);
            foreach (string path in picked)
            {
                await PluginActions.InstallAsync(_services, path);
            }
        });
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_services is null || (sender as FrameworkElement)?.DataContext is not PluginRow { Entry.Catalog: { } entry })
        {
            return;
        }

        await RunBusyAsync($"No se instaló «{entry.Name}»", async () =>
        {
            try
            {
                InstallResult result = await _services.Packages.InstallFromCatalogAsync(entry);
                _services.Session.Notify(NotificationSeverity.Success, $"«{result.Package.Manifest.Name}» instalado", $"Versión {result.Package.Manifest.Version}.");
            }
            catch (NotSupportedException ex)
            {
                _services.Session.Notify(NotificationSeverity.Informational, "Todavía no", ex.Message);
            }
        });
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
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
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ElementTheme.Dark,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null)
        {
            await RunBusyAsync("No se pudo actualizar la lista", () => _services.Packages.RefreshAsync());
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
