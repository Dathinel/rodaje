using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Shell;

/// <summary>Flujo de trabajo: proxies al importar y lo que se revisa con el temporizador de los ajustes.</summary>
#pragma warning disable CA1001 // Lo descartable se libera en OnClosed (MainWindow.xaml.cs).
internal sealed partial class MainWindow
#pragma warning restore CA1001
{
    private AutoProxyService CreateAutoProxies(EditorSession session)
    {
        var service = new AutoProxyService(
            () => _settings.Current.ProxyUsage == ProxyUsage.Never ? null : _engine.ProxyResolver,
            () => _engine.DedicatedVideoMemory,
            () => _settings.Current.AutoProxies && _settings.Current.ProxyUsage != ProxyUsage.Never,
            SynchronizationContext.Current);
        session.MediaImported += (_, media) =>
        {
            int count = service.OnImported(media);
            if (count > 0)
            {
                session.Notify(NotificationSeverity.Informational, "Proxies en segundo plano",
                    (count == 1 ? "Un video pesado tendrá" : $"{count} videos pesados tendrán")
                    + " una copia liviana para editar con fluidez. El avance se ve en la portada y en Trabajos; la exportación usa los originales.");
            }
        };
        return service;
    }

    private void OnWorkflowTick() => _services.AutoProxies?.Tick();

    /// <summary>Si al cerrar (o en un apagón) quedaron exportaciones en la cola, pregunta si se reanudan.</summary>
    private async Task OfferExportQueueAsync(Task cleanup)
    {
        await cleanup.ConfigureAwait(true);
        if (_exports.Store is not { } store)
        {
            return;
        }

        IReadOnlyList<(Rodaje.Export.ExportRequest Request, bool ContinueWithoutMissing)> pending = await Task.Run(store.Load).ConfigureAwait(true);
        if (pending.Count == 0 || _closed)
        {
            return;
        }

        string names = string.Join(", ", pending.Take(3).Select(p => $"«{Path.GetFileName(p.Request.OutputPath)}»")) + (pending.Count > 3 ? "…" : "");
        if (Root.XamlRoot is not { } xamlRoot)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot, Title = "Exportaciones sin terminar",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = (pending.Count == 1 ? "Quedó una exportación en la cola" : $"Quedaron {pending.Count} exportaciones en la cola")
                    + $" de la última vez: {names}. La que estaba a medias empieza de nuevo.",
            },
            PrimaryButtonText = "Reanudar", CloseButtonText = "Descartar",
            DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark,
        };
        // Si otra pregunta está abierta (recuperar un autoguardado), se espera a que se cierre.
        while (DialogGate.IsOpen && !_closed)
        {
            await Task.Delay(500).ConfigureAwait(true);
        }

        ContentDialogResult? answer = await DialogGate.ShowAsync(dialog).ConfigureAwait(true);
        if (_closed || answer is null)
        {
            return;
        }

        if (answer == ContentDialogResult.Primary)
        {
            _exports.EnqueueRange(pending.Select(p => p.Request with { ContinueWithoutMissing = p.ContinueWithoutMissing }));
            Inspector.Show(Views.Inspector.InspectorView.Tab.Jobs);
        }
        else
        {
            store.Clear();
        }
    }
}
