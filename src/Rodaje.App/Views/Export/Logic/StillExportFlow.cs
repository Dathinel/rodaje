using Rodaje.App.Services;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Format;
using Rodaje.Export.Still;
using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>
/// La lógica de «Exportar fotograma» sin WinUI: una exportación a la vez, el aviso «Exportando fotograma» con su botón
/// «Cancelar», el reintento «Exportar sin él» y la cancelación al cerrar la ventana. El diálogo de guardar y la
/// exportación de verdad llegan como funciones, para probarla con un exportador falso.
/// </summary>
/// <remarks>
/// Se usa desde un solo hilo (el de la interfaz en la app): no tiene cerrojos. <see cref="StartAsync"/> nunca lanza;
/// todo error termina en un aviso.
/// </remarks>
internal sealed partial class StillExportFlow : IDisposable
{
    private readonly Func<StillRequest?> _ask;
    private readonly Func<StillRequest, CancellationToken, Task<StillResult>> _export;
    private readonly Action<EditorNotification> _notify;
    /// <summary>La exportación en marcha (o el diálogo de guardar abierto); <c>null</c> si no hay ninguna.</summary>
    private CancellationTokenSource? _running;
    private bool _disposed;

    /// <param name="ask">Abre el diálogo de guardar; <c>null</c> si se cancela.</param>
    /// <param name="export">Exporta el fotograma; la cancelación llega como <see cref="OperationCanceledException"/> o
    /// <see cref="ExportException"/> con <see cref="ExportErrorCode.Canceled"/>.</param>
    /// <param name="notify">Muestra un aviso en la barra de mensajes.</param>
    public StillExportFlow(
        Func<StillRequest?> ask,
        Func<StillRequest, CancellationToken, Task<StillResult>> export,
        Action<EditorNotification> notify)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(export);
        ArgumentNullException.ThrowIfNull(notify);
        _ask = ask;
        _export = export;
        _notify = notify;
    }

    /// <summary>Si hay una exportación en marcha (o el diálogo de guardar abierto).</summary>
    public bool IsRunning => _running is not null;

    /// <summary>Cancela la exportación que haya, sin avisar de la cancelación (la ventana se cierra).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running?.Cancel();
    }

    /// <summary>Lanza una exportación (con <paramref name="request"/>, el reintento «exportar sin él») si no hay otra.</summary>
    /// <returns>Una tarea que termina con la exportación; no lanza.</returns>
    public async Task StartAsync(StillRequest? request)
    {
        if (_disposed)
        {
            return;
        }

        if (_running is { } current)
        {
            // Ctrl+Mayús+E repetido: no se lanza otra; se recuerda la que va y cómo pararla. Si ya se canceló y aún no
            // termina, se avisa de que hay que esperar: sin aviso, el atajo o «Exportar sin él» parecerían no hacer nada.
            if (current.IsCancellationRequested)
            {
                _notify(new EditorNotification(NotificationSeverity.Informational, "Cancelando el cuadro…",
                    "La exportación anterior aún se está deteniendo; vuelve a intentarlo cuando termine."));
            }
            else
            {
                NotifyExporting(current);
            }

            return;
        }

        using var cancel = new CancellationTokenSource();
        _running = cancel;
        try
        {
            request ??= _ask();
            if (request is not null)
            {
                await ExportAsync(request, cancel);
            }
        }
#pragma warning disable CA1031 // Un fotograma que falla no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _notify(new EditorNotification(NotificationSeverity.Error, "No se pudo exportar el cuadro", ex.Message));
        }
        finally
        {
            _running = null;
        }
    }

    private async Task ExportAsync(StillRequest request, CancellationTokenSource cancel)
    {
        NotifyExporting(cancel);
        try
        {
            StillResult result = await _export(request, cancel.Token);
            _notify(new EditorNotification(NotificationSeverity.Success, "Fotograma exportado",
                string.Create(SpanishNumbers.Format, $"Cuadro {result.Frame} guardado en «{result.OutputPath}» ({result.Width}×{result.Height}).")));
        }
        // Solo la cancelación de verdad: un disco lleno justo al pulsar «Cancelar» se avisa como lo que es.
        catch (Exception error) when (cancel.IsCancellationRequested
            && error is OperationCanceledException or ExportException { Code: ExportErrorCode.Canceled })
        {
            if (!_disposed)
            {
                _notify(new EditorNotification(NotificationSeverity.Informational, "Fotograma cancelado", "No se guardó ningún archivo."));
            }
        }
        catch (ExportException error)
        {
            ExportErrorView view = ExportErrorText.For(error);
            NotificationAction? action = view.Action == ExportErrorAction.ContinueWithout && !request.ContinueWithoutMissing
                ? new NotificationAction(view.ActionText ?? "Exportar sin él", () => UiTask.Run(StartAsync(request with { ContinueWithoutMissing = true })))
                : null;
            _notify(new EditorNotification(NotificationSeverity.Error, view.Title, view.Message, action));
        }
    }

    /// <summary>El indicador de «exportando»: un aviso con el botón para cancelar.</summary>
    private void NotifyExporting(CancellationTokenSource cancel) =>
        _notify(new EditorNotification(NotificationSeverity.Informational, "Exportando cuadro…",
            "Se guarda el cuadro del cursor; el atajo no lanza otro hasta que termine.",
            new NotificationAction("Cancelar", () =>
            {
                if (ReferenceEquals(_running, cancel))
                {
                    cancel.Cancel();
                }
            })));
}
