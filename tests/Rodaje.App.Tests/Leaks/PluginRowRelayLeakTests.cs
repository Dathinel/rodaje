using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using Rodaje.App.Views.Plugins;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// La página de complementos rehace sus filas en cada cambio de la lista, y la descarga de cada complemento vive en la
/// página. Con <c>x:Bind ... OneWay</c> directo a la descarga, que esa suscripción se soltara dependía de WinUI; ahora cada
/// fila repite los avisos con <see cref="RelayedPropertyChanged"/> y la página la suelta antes de rehacer la lista. La fila
/// de la prueba hace lo mismo que <c>PluginRow</c> (que arrastra WinUI y aquí no compila); que <c>PluginRow</c> y
/// <c>PluginsView.Refresh</c> lo sigan haciendo se vigila leyendo su código (tarea 141).
/// </summary>
public sealed class PluginRowRelayLeakTests
{
    [Fact]
    public void Rebuilding_a_hundred_times_leaves_only_the_current_rows_subscribed()
    {
        var download = new CountingSource();
        var rows = new ObservableCollection<Row>();
        for (int refresh = 0; refresh < 100; refresh++)
        {
            // Como PluginsView.Refresh: se sueltan las viejas y se ponen tres nuevas sobre la misma descarga.
            PropertyChangedRelay.Release(rows);
            for (int i = 0; i < 3; i++)
            {
                rows.Add(new Row(download));
            }
        }

        // Sin soltarlas quedaban 300 manejadores en la descarga, uno por fila hecha.
        Assert.Equal(3, download.Handlers);
        download.Raise("Percent");
        Assert.All(rows, r => Assert.Equal(["DownloadPercent"], r.Seen));
    }

    [Fact]
    public void Released_rows_are_not_kept_alive_by_the_download()
    {
        var download = new CountingSource();
        WeakReference[] rows = MakeAndRelease(download, 100);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Equal(0, download.Handlers);
        Assert.DoesNotContain(rows, r => r.IsAlive);
    }

    [Fact]
    public void After_dispose_no_notice_arrives_and_disposing_twice_is_harmless()
    {
        var download = new CountingSource();
        var row = new Row(download);
        download.Raise("Text");
        row.Dispose();
        row.Dispose();
        download.Raise("Text");

        Assert.Equal(["DownloadText"], row.Seen);
        Assert.Equal(0, download.Handlers);
    }

    [Fact]
    public void The_download_is_not_subscribed_until_someone_listens_and_an_empty_name_passes_as_is()
    {
        var download = new CountingSource();
        using var notices = new RelayedPropertyChanged(download, "Download");
        Assert.Equal(0, download.Handlers);

        var seen = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => seen.Add(e.PropertyName);
        notices.Add(this, handler);
        notices.Add(this, handler);
        Assert.Equal(1, download.Handlers);
        download.Raise("");
        download.Raise("Percent");
        notices.Remove(handler);
        download.Raise("Text");
        Assert.Equal(["", "", "DownloadPercent", "DownloadPercent", "DownloadText"], seen);
    }

    [Fact]
    public void A_row_can_publish_its_own_property_change_through_the_same_subscription()
    {
        var download = new CountingSource();
        using var notices = new RelayedPropertyChanged(download, "Download");
        var seen = new List<string?>();
        notices.Add(this, (_, e) => seen.Add(e.PropertyName));

        notices.Raise(this, "CanUninstallNow");
        download.Raise("Percent");

        Assert.Equal(["CanUninstallNow", "DownloadPercent"], seen);
    }

    [Fact]
    public void PluginRow_uses_the_relay_and_releases_it_on_dispose()
    {
        // PluginRow arrastra WinUI: se lee su código. Si su evento dejara de pasar por RelayedPropertyChanged o Dispose
        // dejara de soltarlo, la fila real volvería a quedar retenida por la descarga.
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginRow.cs"));
        Match field = Regex.Match(source, @"private readonly RelayedPropertyChanged (\w+) = new\(download, ""Download""\);");
        Assert.True(field.Success, "PluginRow debe repetir los avisos de su descarga con RelayedPropertyChanged y el prefijo «Download».");
        string notices = Regex.Escape(field.Groups[1].Value);
        Assert.Matches(@"add => " + notices + @"\.Add\(this, value\);", source);
        Assert.Matches(@"remove => " + notices + @"\.Remove\(value\);", source);
        Assert.Matches(@"public void Dispose\(\) => " + notices + @"\.Dispose\(\);", source);
        Assert.DoesNotMatch(@"new PropertyChangedRelay\(", source);
    }

    [Fact]
    public void PluginsView_releases_both_lists_before_rebuilding_them()
    {
        // Sin los Release de PluginsView.Refresh, cada vuelta dejaba otra suscripción a la descarga por fila.
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginsView.xaml.cs"));
        string refresh = MethodBody(source, "Refresh");
        foreach (string list in new[] { "ContentPackages", "Modules" })
        {
            int release = refresh.IndexOf("PropertyChangedRelay.Release(" + list + ");", StringComparison.Ordinal);
            Assert.True(release >= 0, $"PluginsView.Refresh debe soltar {list} con PropertyChangedRelay.Release.");
            int add = refresh.IndexOf(".Add(", StringComparison.Ordinal);
            Assert.True(add > release, $"PluginsView.Refresh debe soltar {list} antes de añadir filas.");
        }

        Assert.DoesNotMatch(@"\b(ContentPackages|Modules)\.Clear\(", source);
    }

    [Fact]
    public void Instance_poll_updates_existing_rows_in_place()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginsView.xaml.cs"));
        string poll = MethodBody(source, "CheckInstances");

        Assert.Contains("row.UpdateCanUninstallNow(canUninstallNow);", poll, StringComparison.Ordinal);
        Assert.DoesNotContain("Refresh();", poll, StringComparison.Ordinal);
        string row = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginRow.cs"));
        Assert.Contains("public void UpdateCanUninstallNow(bool canUninstallNow)", row, StringComparison.Ordinal);
        Assert.Contains("_notices.Raise(this, nameof(CanUninstallNow));", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_lists_offer_guidance_when_empty_and_refresh_is_local()
    {
        string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginsView.xaml"));
        Assert.Contains("x:Name=\"ContentEmptyState\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ModulesEmptyState\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Recargar archivos locales\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Instalar desde archivo…\"", xaml, StringComparison.Ordinal);
        string view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/Rodaje.App/Views/Plugins/PluginsView.xaml.cs"));
        Assert.Contains("ContentEmptyState.Visibility = ContentPackages.Count == 0", view, StringComparison.Ordinal);
        Assert.Contains("ModulesEmptyState.Visibility = Modules.Count == 0", view, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string method)
    {
        Match signature = Regex.Match(source, @"private void " + method + @"\(\)\s*\{");
        Assert.True(signature.Success, $"No encuentro {method}.");
        int depth = 1;
        int i = signature.Index + signature.Length;
        int start = i;
        for (; depth > 0; i++)
        {
            depth += source[i] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
        }

        return source[start..(i - 1)];
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] MakeAndRelease(CountingSource download, int count)
    {
        var rows = new ObservableCollection<Row>();
        for (int i = 0; i < count; i++)
        {
            rows.Add(new Row(download));
        }

        WeakReference[] weak = [.. rows.Select(r => new WeakReference(r))];
        PropertyChangedRelay.Release(rows);
        return weak;
    }

    /// <summary>Como <c>PluginRow</c>: su evento es el de <see cref="RelayedPropertyChanged"/> con el prefijo «Download».</summary>
    private sealed class Row : INotifyPropertyChanged, IDisposable
    {
        private readonly RelayedPropertyChanged _notices;

        public Row(INotifyPropertyChanged download)
        {
            _notices = new RelayedPropertyChanged(download, "Download");
            PropertyChanged += (sender, e) =>
            {
                Assert.Same(this, sender);
                Seen.Add(e.PropertyName ?? "");
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _notices.Add(this, value);
            remove => _notices.Remove(value);
        }

        public List<string> Seen { get; } = [];

        public void Dispose() => _notices.Dispose();
    }

    /// <summary>Una descarga de larga vida que cuenta cuántos manejadores tiene.</summary>
    private sealed class CountingSource : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _handlers;

        public int Handlers => _handlers?.GetInvocationList().Length ?? 0;

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public void Raise(string name) => _handlers?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
