using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 103: la acción que un panel deja para después de su evento (<see cref="UiDeferral.RunAfterEvent"/>) no pasa por
/// <c>GuardedSynchronizationContext</c>; si lanza, queda registrada como <see cref="CrashKind.DispatcherCallback"/> y no se
/// propaga (en la app, se cerraría con 0xC000027B y sin pila).
/// </summary>
public class UiDeferralTests
{
    private static Func<Exception, bool> Report(CrashReporter reporter, List<string?> reports) => ex =>
    {
        reports.Add(reporter.Write(CrashKind.DispatcherCallback, ex, fatal: false));
        return true;
    };

    [Fact]
    public void UnaAccionQueLanza_EnLaCola_QuedaRegistrada_YNoSePropaga()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog());
        var reports = new List<string?>();
        var queue = new Queue<Action>();

        UiDeferral.RunAfterEvent(a => { queue.Enqueue(a); return true; }, () => throw new InvalidOperationException("falló el desplegable"), Report(reporter, reports));
        Assert.Empty(reports);

        queue.Dequeue()();
        string? path = Assert.Single(reports);
        Assert.NotNull(path);
        Assert.EndsWith("-recuperado.txt", path, StringComparison.Ordinal);
        string text = File.ReadAllText(path);
        Assert.Contains("falló el desplegable", text, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), text, StringComparison.Ordinal);
    }

    [Fact]
    public void SiLaColaNoLaAcepta_SeDescarta()
    {
        // Tarea 121: hacerla ya sería hacerla dentro del evento (la cola la rechaza al cerrar la ventana).
        int runs = 0;
        Assert.False(UiDeferral.RunAfterEvent(_ => false, () => runs++, _ => true));
        Assert.False(UiDeferral.RunAfterEvent(_ => false, () => throw new ArgumentException("ya"), _ => throw new InvalidOperationException()));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void LaAccion_VaDespuesDelEvento()
    {
        var queue = new Queue<Action>();
        int runs = 0;
        UiDeferral.RunAfterEvent(a => { queue.Enqueue(a); return true; }, () => runs++, _ => true);
        Assert.Equal(0, runs);
        queue.Dequeue()();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void SinManejadorQueLaAtienda_SigueSuCamino()
    {
        // Como GuardedSynchronizationContext: sin ventana todavía (el manejador devuelve false) o si el manejador falla.
        var seen = new List<Exception>();
        var queue = new Queue<Action>();
        Assert.True(UiDeferral.RunAfterEvent(a => { queue.Enqueue(a); return true; }, () => throw new InvalidOperationException(), ex =>
        {
            seen.Add(ex);
            return false;
        }));
        Assert.Throws<InvalidOperationException>(queue.Dequeue());
        Assert.Single(seen);
        Assert.Throws<InvalidOperationException>(() => UiDeferral.Run(() => throw new InvalidOperationException(), _ => throw new FormatException()));
    }
}
