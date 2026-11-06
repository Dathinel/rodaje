using Rodaje.App.Services;
using Rodaje.App.Views.Export;
using Rodaje.Core.Timing;
using Rodaje.Export.Still;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// «Exportar fotograma» sin WinUI: una exportación a la vez, «Cancelar», el cierre de la ventana y el reintento
/// «Exportar sin él», con un diálogo y un exportador falsos que esperan a que la prueba los suelte.
/// </summary>
public sealed class StillExportFlowTests
{
    private const string Exporting = "Exportando fotograma…";
    private const string Canceled = "Fotograma cancelado";
    private const string Exported = "Fotograma exportado";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Un exportador que no termina hasta que la prueba lo suelta; no escribe nada en disco.</summary>
    private sealed class FakeExporter
    {
        public List<(StillRequest Request, CancellationToken Token, TaskCompletionSource<StillResult> Done)> Calls { get; } = [];

        /// <summary>Si la cancelación termina la exportación sola, como el exportador real.</summary>
        public Func<CancellationToken, Exception>? OnCancel { get; set; } = token => new OperationCanceledException(token);

        public Task<StillResult> ExportAsync(StillRequest request, CancellationToken token)
        {
            var done = new TaskCompletionSource<StillResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (OnCancel is { } onCancel)
            {
                token.Register(() => done.TrySetException(onCancel(token)));
            }

            Calls.Add((request, token, done));
            return done.Task;
        }

        public void Succeed(int call = 0) =>
            Calls[call].Done.SetResult(new StillResult(Calls[call].Request.OutputPath, 1234, 15, 1920, 1080));

        public void Fail(Exception error, int call = 0) => Calls[call].Done.SetException(error);
    }

    private sealed class Harness
    {
        public FakeExporter Exporter { get; } = new();

        public List<EditorNotification> Notifications { get; } = [];

        public int Asks { get; private set; }

        /// <summary>Lo que devuelve el diálogo de guardar; <c>null</c> = el usuario lo cerró.</summary>
        public StillRequest? Answer { get; set; } = Request();

        public StillExportFlow Flow { get; }

        public Harness()
        {
            Flow = new StillExportFlow(
                () =>
                {
                    Asks++;
                    return Answer;
                },
                Exporter.ExportAsync,
                n =>
                {
                    lock (Notifications)
                    {
                        Notifications.Add(n);
                    }
                });
        }

        public List<string> Titles()
        {
            lock (Notifications)
            {
                return Notifications.Select(n => n.Title).ToList();
            }
        }

        public EditorNotification Last(string title)
        {
            lock (Notifications)
            {
                return Notifications.Last(n => n.Title == title);
            }
        }
    }

    /// <summary>
    /// El hilo de interfaz de la prueba: las continuaciones de <see cref="StillExportFlow"/> se encolan aquí y las ejecuta
    /// <see cref="Pump"/> en el hilo de la prueba. Sin él, xUnit las lanzaba en un hilo nuevo que, en la CI de Windows
    /// cargada, tardó más que el tope de la espera.
    /// </summary>
    private sealed class UiLoop : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                _queue.Enqueue((d, state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Lo que <paramref name="action"/> deja en marcha continúa en este contexto.</summary>
        public T Run<T>(Func<T> action)
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try
            {
                return action();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        /// <summary>Ejecuta lo encolado, y lo que eso encole, hasta vaciar la cola.</summary>
        public void Pump() => Run(() =>
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) next;
                lock (_queue)
                {
                    if (!_queue.TryDequeue(out next))
                    {
                        return 0;
                    }
                }

                next.Callback(next.State);
            }
        });
    }

    private static StillRequest Request() =>
        new(ExportTestData.Project(), "sec", Time.FromSeconds(0.5), @"C:\fotos\cuadro.png", new StillSettings());

    [Fact]
    public async Task Exporta_y_avisa_del_resultado()
    {
        var h = new Harness();
        Task run = h.Flow.StartAsync(null);

        Assert.True(h.Flow.IsRunning);
        Assert.Equal(1, h.Asks);
        Assert.Single(h.Exporter.Calls);
        Assert.Equal([Exporting], h.Titles());

        h.Exporter.Succeed();
        await run.WaitAsync(Wait);

        Assert.False(h.Flow.IsRunning);
        EditorNotification done = h.Last(Exported);
        Assert.Equal(NotificationSeverity.Success, done.Severity);
        Assert.Contains("Fotograma 15", done.Message, StringComparison.Ordinal);
        Assert.Contains("cuadro.png", done.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_segundo_Start_no_lanza_otra_exportacion_y_recuerda_la_que_va()
    {
        var h = new Harness();
        Task first = h.Flow.StartAsync(null);
        Task second = h.Flow.StartAsync(null);

        // El segundo vuelve enseguida: ni diálogo ni exportación, solo el aviso con «Cancelar» otra vez.
        Assert.True(second.IsCompletedSuccessfully);
        Assert.Equal(1, h.Asks);
        Assert.Single(h.Exporter.Calls);
        Assert.Equal([Exporting, Exporting], h.Titles());
        Assert.Equal("Cancelar", h.Last(Exporting).Action?.Text);

        h.Exporter.Succeed();
        await first.WaitAsync(Wait);
        Assert.Single(h.Exporter.Calls);
        Assert.Single(h.Titles(), Exported);
    }

    [Fact]
    public async Task Con_el_dialogo_abierto_tampoco_se_lanza_otra()
    {
        // El diálogo de guardar es modal pero bombea mensajes: el atajo puede llegar mientras está abierto.
        var exporter = new FakeExporter();
        var notifications = new List<EditorNotification>();
        Task? reentry = null;
        int asks = 0;
        StillExportFlow? flow = null;
        flow = new StillExportFlow(
            () =>
            {
                asks++;
                reentry = flow!.StartAsync(null);
                return null;
            },
            exporter.ExportAsync,
            notifications.Add);

        await flow.StartAsync(null).WaitAsync(Wait);

        Assert.NotNull(reentry);
        Assert.True(reentry.IsCompletedSuccessfully);
        Assert.Equal(1, asks);
        Assert.Empty(exporter.Calls);
        Assert.False(flow.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelar_cancela_el_token_y_no_guarda_nada(bool asExportException)
    {
        var h = new Harness();
        if (asExportException)
        {
            // StillExporter avisa la cancelación como ExportException con su código.
            h.Exporter.OnCancel = _ => new ExportException(ExportErrorCode.Canceled, "Cancelada.");
        }

        var ui = new UiLoop();
        Task run = ui.Run(() => h.Flow.StartAsync(null));
        h.Last(Exporting).Action!.Run();

        Assert.True(h.Exporter.Calls[0].Token.IsCancellationRequested);
        ui.Pump();
        Assert.True(run.IsCompleted, "La exportación cancelada no terminó al vaciar la cola del hilo de interfaz.");
        await run;

        Assert.False(h.Flow.IsRunning);
        Assert.Equal([Exporting, Canceled], h.Titles());
        Assert.Equal(NotificationSeverity.Informational, h.Last(Canceled).Severity);
        Assert.DoesNotContain(Exported, h.Titles());
    }

    [Fact]
    public async Task Un_error_real_al_cancelar_no_se_disfraza_de_cancelacion()
    {
        // Falla el disco justo cuando el usuario pulsa «Cancelar»: se avisa del disco, no de «cancelado».
        var h = new Harness { Exporter = { OnCancel = null } };
        Task run = h.Flow.StartAsync(null);
        h.Last(Exporting).Action!.Run();
        h.Exporter.Fail(new ExportException(ExportErrorCode.DiskFull, "Disco lleno."));
        await run.WaitAsync(Wait);

        Assert.DoesNotContain(Canceled, h.Titles());
        EditorNotification error = h.Notifications[^1];
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.Equal(ExportErrorText.For(new ExportException(ExportErrorCode.DiskFull, "x")).Title, error.Title);
    }

    [Fact]
    public async Task Dispose_cancela_sin_avisar_y_no_deja_lanzar_otra()
    {
        var h = new Harness();
        Task run = h.Flow.StartAsync(null);

        h.Flow.Dispose();
        Assert.True(h.Exporter.Calls[0].Token.IsCancellationRequested);
        await run.WaitAsync(Wait);

        Assert.Equal([Exporting], h.Titles());
        Assert.False(h.Flow.IsRunning);

        await h.Flow.StartAsync(null).WaitAsync(Wait);
        Assert.Equal(1, h.Asks);
        Assert.Single(h.Exporter.Calls);
        h.Flow.Dispose();
    }

    public static TheoryData<string> Endings => ["éxito", "error de exportación", "error inesperado", "diálogo cerrado"];

    [Theory]
    [MemberData(nameof(Endings))]
    public async Task Al_terminar_o_fallar_queda_libre_y_el_atajo_vuelve_a_funcionar(string ending)
    {
        var h = new Harness();
        if (ending == "diálogo cerrado")
        {
            h.Answer = null;
        }

        Task run = h.Flow.StartAsync(null);
        switch (ending)
        {
            case "éxito":
                h.Exporter.Succeed();
                break;
            case "error de exportación":
                h.Exporter.Fail(new ExportException(ExportErrorCode.EncoderFailed, "ffmpeg terminó con error."));
                break;
            case "error inesperado":
                h.Exporter.Fail(new InvalidOperationException("Algo raro."));
                break;
        }

        await run.WaitAsync(Wait);
        Assert.False(h.Flow.IsRunning);
        if (ending == "error inesperado")
        {
            Assert.Equal("No se pudo exportar el fotograma", h.Notifications[^1].Title);
        }

        h.Answer = Request();
        Task again = h.Flow.StartAsync(null);
        Assert.Equal(2, h.Asks);
        Assert.Equal(ending == "diálogo cerrado" ? 1 : 2, h.Exporter.Calls.Count);
        h.Exporter.Succeed(h.Exporter.Calls.Count - 1);
        await again.WaitAsync(Wait);
        Assert.Equal(Exported, h.Notifications[^1].Title);
    }

    [Fact]
    public async Task El_reintento_exporta_sin_lo_que_falta_sin_volver_a_preguntar()
    {
        var h = new Harness();
        Task run = h.Flow.StartAsync(null);
        h.Exporter.Fail(new ExportException(ExportErrorCode.ContentMissing, "Falta la LUT.") { Culprit = "lut:calida" });
        await run.WaitAsync(Wait);

        EditorNotification error = h.Notifications[^1];
        Assert.Equal(NotificationSeverity.Error, error.Severity);
        Assert.NotNull(error.Action);
        Assert.False(h.Flow.IsRunning);

        error.Action.Run();
        Assert.Equal(1, h.Asks);
        Assert.Equal(2, h.Exporter.Calls.Count);
        StillRequest retry = h.Exporter.Calls[1].Request;
        Assert.True(retry.ContinueWithoutMissing);
        Assert.Equal(@"C:\fotos\cuadro.png", retry.OutputPath);

        // Si vuelve a faltar algo con «sin él» ya pedido, no se ofrece otra vez el mismo botón.
        h.Exporter.Fail(new ExportException(ExportErrorCode.ContentMissing, "Falta otra."), call: 1);
        await WaitUntilAsync(() => !h.Flow.IsRunning);
        Assert.Null(h.Notifications[^1].Action);
    }

    [Fact]
    public async Task El_reintento_con_otra_en_marcha_no_lanza_otra_y_avisa()
    {
        var h = new Harness();
        Task first = h.Flow.StartAsync(null);
        h.Exporter.Fail(new ExportException(ExportErrorCode.EffectFailed, "Un efecto falló."));
        await first.WaitAsync(Wait);
        NotificationAction retry = h.Notifications[^1].Action!;

        Task second = h.Flow.StartAsync(null);
        int before = h.Notifications.Count;
        retry.Run();

        Assert.Equal(2, h.Exporter.Calls.Count);
        Assert.False(h.Exporter.Calls[1].Request.ContinueWithoutMissing);
        Assert.Equal(before + 1, h.Notifications.Count);
        Assert.Equal(Exporting, h.Notifications[^1].Title);

        h.Exporter.Succeed(1);
        await second.WaitAsync(Wait);
    }

    [Fact]
    public async Task El_reintento_mientras_termina_una_cancelada_no_se_traga_sin_avisar()
    {
        var h = new Harness { Exporter = { OnCancel = null } };
        Task first = h.Flow.StartAsync(null);
        h.Exporter.Fail(new ExportException(ExportErrorCode.ContentMissing, "Falta la LUT."));
        await first.WaitAsync(Wait);
        NotificationAction retry = h.Notifications[^1].Action!;

        // Otra exportación, cancelada pero que aún no termina (el exportador real tarda en soltar la GPU y ffmpeg).
        Task second = h.Flow.StartAsync(null);
        h.Last(Exporting).Action!.Run();
        int before = h.Notifications.Count;
        retry.Run();

        Assert.Equal(2, h.Exporter.Calls.Count);
        Assert.True(h.Notifications.Count > before, "El reintento no hizo nada y no avisó.");

        h.Exporter.Fail(new OperationCanceledException(), call: 1);
        await second.WaitAsync(Wait);
    }

    [Fact]
    public async Task El_Cancelar_de_una_exportacion_terminada_no_cancela_la_siguiente()
    {
        var h = new Harness();
        Task first = h.Flow.StartAsync(null);
        NotificationAction oldCancel = h.Last(Exporting).Action!;
        h.Exporter.Succeed();
        await first.WaitAsync(Wait);

        Task second = h.Flow.StartAsync(null);
        oldCancel.Run();

        Assert.False(h.Exporter.Calls[1].Token.IsCancellationRequested);
        h.Exporter.Succeed(1);
        await second.WaitAsync(Wait);
        Assert.Equal(Exported, h.Notifications[^1].Title);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < limit, "La exportación no terminó a tiempo.");
            await Task.Delay(10);
        }
    }
}
