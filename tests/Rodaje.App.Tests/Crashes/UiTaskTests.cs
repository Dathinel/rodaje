using System.Collections.Concurrent;
using Rodaje.App.Services.Crashes;

namespace Rodaje.App.Tests.Crashes;

/// <summary>
/// Las tareas sueltas de los manejadores (antes «_ = AlgoAsync()» y async void sin try/catch) no pierden su excepción:
/// UiTask.Run la entrega a quien informa. Reporter es global: cada prueba busca su propia excepción y lo restaura.
/// </summary>
[Collection(nameof(UiTaskTests))]
public sealed class UiTaskTests
{
    [Fact]
    public void Run_TareaQueFallaDespues_LaInformaConAviso()
    {
        var reported = new ConcurrentBag<(Exception, bool)>();
        Action<Exception, bool>? before = UiTask.Reporter;
        UiTask.Reporter = (ex, notify) => reported.Add((ex, notify));
        try
        {
            var pending = new TaskCompletionSource();
            var error = new InvalidOperationException("falló después");
            UiTask.Run(pending.Task);
            pending.SetException(error);

            Assert.Contains((error, true), reported);
        }
        finally
        {
            UiTask.Reporter = before;
        }
    }

    [Fact]
    public void Run_LambdaQueLanzaAntesDeEsperar_LaInforma()
    {
        var reported = new ConcurrentBag<Exception>();
        Action<Exception, bool>? before = UiTask.Reporter;
        UiTask.Reporter = (ex, _) => reported.Add(ex);
        try
        {
            var error = new InvalidOperationException("falló antes del primer await");
            UiTask.Run(() => throw error);
            UiTask.Run(() => Task.FromException(error));

            Assert.Equal(2, reported.Count(e => ReferenceEquals(e, error)));
        }
        finally
        {
            UiTask.Reporter = before;
        }
    }

    [Fact]
    public async Task Run_CanceladaNulaOBien_NoInformaNada()
    {
        var reported = new ConcurrentBag<Exception>();
        Action<Exception, bool>? before = UiTask.Reporter;
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        Task canceled = Task.FromCanceled(cancel.Token);
        Task faultedByCancel = Task.FromException(new OperationCanceledException("cancelada"));
        UiTask.Reporter = (ex, _) => reported.Add(ex);
        try
        {
            UiTask.Run(canceled);
            UiTask.Run(faultedByCancel);
            UiTask.Run((Task?)null);
            UiTask.Run(Task.CompletedTask);

            Assert.DoesNotContain(reported, e => e is OperationCanceledException);
        }
        finally
        {
            UiTask.Reporter = before;
        }
    }

    [Fact]
    public void Report_SinQuienInforme_DevuelveFalseYNoLanza()
    {
        Action<Exception, bool>? before = UiTask.Reporter;
        UiTask.Reporter = null;
        try
        {
            Assert.False(UiTask.Report(new InvalidOperationException("sin informe")));
            UiTask.Reporter = (_, _) => throw new InvalidOperationException("el que informa falla");
            Assert.False(UiTask.Report(new InvalidOperationException("tampoco")));
        }
        finally
        {
            UiTask.Reporter = before;
        }
    }
}
