using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Tarea 106: <see cref="UiDeferral.TryRunLater"/> es el <c>TryEnqueue</c> de la app para lo que llega de otro hilo. Envuelve
/// la acción con el registro de fallos como <see cref="UiDeferral.RunAfterEvent"/>, pero conserva lo que hacía
/// <c>TryEnqueue</c>: devuelve si la cola la aceptó y, si no, no la hace (hacerla ya sería tocar la interfaz desde ese hilo).
/// </summary>
public class UiDeferralRunLaterTests
{
    [Fact]
    public void UnaAccionQueLanza_QuedaRegistrada_YNoSePropaga()
    {
        using var dir = new TempDirectory();
        var reporter = new CrashReporter(dir.Path, "1.0.0+prueba", new SessionLog());
        var reports = new List<string?>();
        var queue = new Queue<Action>();

        bool accepted = UiDeferral.TryRunLater(a => { queue.Enqueue(a); return true; }, () => throw new InvalidOperationException("falló el motor"), ex =>
        {
            reports.Add(reporter.Write(CrashKind.DispatcherCallback, ex, fatal: false));
            return true;
        });

        Assert.True(accepted);
        Assert.Empty(reports);
        queue.Dequeue()();
        string? path = Assert.Single(reports);
        Assert.NotNull(path);
        Assert.Contains("falló el motor", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SiLaColaNoLaAcepta_NoSeHace_YDevuelveFalse()
    {
        int runs = 0;
        Assert.False(UiDeferral.TryRunLater(_ => false, () => runs++, _ => true));
        Assert.Equal(0, runs);
    }

    [Fact]
    public void LaAccion_VaDespues_YSeHaceUnaVez()
    {
        var queue = new Queue<Action>();
        int runs = 0;
        Assert.True(UiDeferral.TryRunLater(a => { queue.Enqueue(a); return true; }, () => runs++, _ => true));
        Assert.Equal(0, runs);
        queue.Dequeue()();
        Assert.Equal(1, runs);
        Assert.Empty(queue);
    }

    [Fact]
    public void SiElRegistroNoLaAtiende_SigueSuCamino()
    {
        var queue = new Queue<Action>();
        UiDeferral.TryRunLater(a => { queue.Enqueue(a); return true; }, () => throw new InvalidOperationException("sin ventana"), _ => false);
        Assert.Throws<InvalidOperationException>(queue.Dequeue());
    }

    [Fact]
    public void SinCola_SinAccion_OSinRegistro_Lanza()
    {
        Assert.Throws<ArgumentNullException>(() => UiDeferral.TryRunLater(null!, () => { }, _ => true));
        Assert.Throws<ArgumentNullException>(() => UiDeferral.TryRunLater(_ => true, null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => UiDeferral.TryRunLater(_ => true, () => { }, null!));
    }
}
