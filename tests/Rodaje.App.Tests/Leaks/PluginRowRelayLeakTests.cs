using System.Collections.ObjectModel;
using System.ComponentModel;
using Rodaje.App.Views.Plugins;

namespace Rodaje.App.Tests.Leaks;

/// <summary>
/// La página de complementos rehace sus filas en cada cambio de la lista, y la descarga de cada complemento vive en la
/// página. Con <c>x:Bind ... OneWay</c> directo a la descarga, que esa suscripción se soltara dependía de WinUI; ahora cada
/// fila repite los avisos con <see cref="PropertyChangedRelay"/> y la página la suelta antes de rehacer la lista.
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

    /// <summary>Como <c>PluginRow</c>: repite los avisos de la descarga con el prefijo «Download».</summary>
    private sealed class Row : IDisposable
    {
        private readonly PropertyChangedRelay _relay;

        public Row(INotifyPropertyChanged download) => _relay = new PropertyChangedRelay(download, name => Seen.Add("Download" + name));

        public List<string> Seen { get; } = [];

        public void Dispose() => _relay.Dispose();
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
