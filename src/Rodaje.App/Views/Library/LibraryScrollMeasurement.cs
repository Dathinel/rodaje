using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Rodaje.App.Views.Library;

/// <summary>
/// Medida de la biblioteca con muchos medios (variable <c>RODAJE_MEDIR_BIBLIOTECA</c> = archivo del informe): espera a
/// que se carguen, recorre la cuadrícula hacia abajo y hacia arriba despacio y luego de golpe, y anota cada intervalo
/// entre cuadros de XAML y cuántas portadas llegaron. Al terminar escribe el informe y cierra la app.
/// </summary>
internal static class LibraryScrollMeasurement
{
    public const string Variable = Rodaje.Media.EnvironmentVariables.MedirBiblioteca;

    public static async Task RunAsync(LibraryView view, GridView grid, string reportPath)
    {
        var report = new StringBuilder();
        var wait = Stopwatch.StartNew();
        while (view.Media.Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(120))
        {
            await Task.Delay(100);
        }

        int count = view.Media.Count;
        var covers = Stopwatch.StartNew();
        while (grid.ContainerFromIndex(0) is null && covers.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(16);
        }

        while (VisibleWithoutCover(view, grid) > 0 && covers.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(16);
        }

        double firstScreen = covers.Elapsed.TotalMilliseconds;
        ScrollViewer? scroller = LibraryView.FindScroller(grid);
        if (scroller is null)
        {
            await File.WriteAllTextAsync(reportPath, string.Create(CultureInfo.InvariantCulture,
                $"sin ScrollViewer (medios {count}, cargada {grid.IsLoaded}, visible {grid.Visibility}, alto {grid.ActualHeight}, hijos {VisualTreeHelper.GetChildrenCount(grid)}, contenedor {grid.ContainerFromIndex(0) is not null})\n"));
            Application.Current.Exit();
            return;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"medios: {count}; portadas de la primera pantalla en {firstScreen:0} ms; alto desplazable {scroller.ScrollableHeight:0} px");
        await Sweep(scroller, TimeSpan.FromSeconds(4), report, "despacio (4 s abajo y 4 s arriba)");
        await Sweep(scroller, TimeSpan.FromSeconds(0.6), report, "rápido (0,6 s abajo y 0,6 s arriba)");
        var settle = Stopwatch.StartNew();
        while (view.Media.Any(m => m.HasVideo && m.Thumbnail is null) && settle.Elapsed < TimeSpan.FromSeconds(30))
        {
            double phase = settle.Elapsed.TotalSeconds % 6 / 3;
            scroller.ChangeView(null, scroller.ScrollableHeight * (phase <= 1 ? phase : 2 - phase), null, true);
            await Task.Delay(100);
        }

        int loaded = view.Media.Count(m => m.Thumbnail is not null);
        string missing = string.Join(", ", view.Media.Select((m, i) => (m, i)).Where(p => p.m.HasVideo && p.m.Thumbnail is null).Select(p => p.i));
        report.AppendLine(CultureInfo.InvariantCulture, $"portadas cargadas: {loaded} de {view.Media.Count(m => m.HasVideo)}{(missing.Length > 0 ? " (faltan: " + missing + ")" : "")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"memoria del proceso: {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB");
        await File.WriteAllTextAsync(reportPath, report.ToString());
        Application.Current.Exit();
    }

    private static async Task Sweep(ScrollViewer scroller, TimeSpan half, StringBuilder report, string label)
    {
        var intervals = new List<double>();
        long last = Stopwatch.GetTimestamp();
        var clock = Stopwatch.StartNew();
        double total = half.TotalSeconds * 2;
        var done = new TaskCompletionSource();
        void OnRendering(object? sender, object e)
        {
            long now = Stopwatch.GetTimestamp();
            intervals.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
            double t = clock.Elapsed.TotalSeconds;
            if (t >= total)
            {
                done.TrySetResult();
                return;
            }

            double fraction = t < half.TotalSeconds ? t / half.TotalSeconds : 2 - (t / half.TotalSeconds);
            scroller.ChangeView(null, scroller.ScrollableHeight * fraction, null, true);
        }

        CompositionTarget.Rendering += OnRendering;
        await done.Task;
        CompositionTarget.Rendering -= OnRendering;
        intervals.RemoveAt(0);
        intervals.Sort();
        double P(double q) => intervals[Math.Min(intervals.Count - 1, (int)(intervals.Count * q))];
        report.AppendLine(CultureInfo.InvariantCulture,
            $"{label}: {intervals.Count} cuadros, {intervals.Count / total:0.0} por segundo; intervalo p50 {P(0.5):0.0} ms, p95 {P(0.95):0.0} ms, " +
            $"p99 {P(0.99):0.0} ms, peor {intervals[^1]:0.0} ms; más de 33 ms: {intervals.Count(i => i > 33.4)}, más de 50 ms: {intervals.Count(i => i > 50)}");
    }

    /// <summary>Medios a la vista (su contenedor cae dentro de la cuadrícula) que todavía no tienen portada.</summary>
    private static int VisibleWithoutCover(LibraryView view, GridView grid)
    {
        int missing = 0;
        for (int i = 0; i < view.Media.Count; i++)
        {
            if (view.Media[i] is not { HasVideo: true, Thumbnail: null } || grid.ContainerFromIndex(i) is not { } container)
            {
                continue;
            }

            double top = WinRT.CastExtensions.As<UIElement>(container).TransformToVisual(grid).TransformPoint(default).Y;
            if (top < grid.ActualHeight && top > -120)
            {
                missing++;
            }
        }

        return missing;
    }
}
