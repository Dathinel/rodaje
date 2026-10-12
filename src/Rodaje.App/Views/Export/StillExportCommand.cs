using Rodaje.App.Services;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Services.Titles;
using Rodaje.App.Shell;
using Rodaje.Core.Timing;
using Rodaje.Export.Running;
using Rodaje.Export.Still;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;

namespace Rodaje.App.Views.Export;

/// <summary>
/// «Archivo → Exportar fotograma...» (<see cref="ActionIds.ExportFrame"/>): guarda el cuadro del cursor como PNG o JPEG
/// al tamaño de la secuencia, por el mismo camino que la exportación (<see cref="StillExporter"/>). Lo mínimo: el
/// formato sale de la extensión elegida en el diálogo de Windows; el resultado o el error llegan como aviso.
/// </summary>
/// <remarks>
/// Una exportación a la vez: mientras hay una en marcha (desde que se abre el diálogo hasta el aviso final), el atajo y el
/// menú no lanzan otra (dos al mismo destino compartirían la carpeta temporal); vuelven a mostrar el aviso
/// «Exportando fotograma» con su botón «Cancelar». Al cerrar la ventana se cancela la que haya.
/// </remarks>
internal static class StillExportCommand
{
    public static IDisposable Bind(ShortcutRouter router, EditorServices services, nint owner)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return new Runner(router, services, owner);
    }

    /// <summary>El estado de una ventana: la exportación en marcha, si hay. Se usa solo desde el hilo de la interfaz.</summary>
    private sealed class Runner : IDisposable
    {
        private readonly EditorServices _services;
        private readonly nint _owner;
        private readonly IDisposable _binding;
        /// <summary>La exportación en marcha (o el diálogo de guardar abierto); <c>null</c> si no hay ninguna.</summary>
        private CancellationTokenSource? _running;
        private bool _disposed;

        public Runner(ShortcutRouter router, EditorServices services, nint owner)
        {
            _services = services;
            _owner = owner;
            _binding = router.Bind(ActionIds.ExportFrame, () => Start(request: null));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _binding.Dispose();
            _running?.Cancel();
        }

        /// <summary>Lanza una exportación (con <paramref name="request"/>, el reintento «exportar sin él») si no hay otra.</summary>
        private async void Start(StillRequest? request)
        {
            if (_disposed)
            {
                return;
            }

            if (_running is { } current)
            {
                // Ctrl+Mayús+E repetido: no se lanza otra; se recuerda la que va y cómo pararla.
                if (!current.IsCancellationRequested)
                {
                    NotifyExporting(current);
                }

                return;
            }

            using var cancel = new CancellationTokenSource();
            _running = cancel;
            try
            {
                request ??= Ask();
                if (request is not null)
                {
                    await ExportAsync(request, cancel);
                }
            }
#pragma warning disable CA1031 // Un fotograma que falla no debe cerrar la app: se avisa.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _services.Session.Notify(NotificationSeverity.Error, "No se pudo exportar el fotograma", ex.Message);
            }
            finally
            {
                _running = null;
            }
        }

        /// <summary>Abre el diálogo de guardar; <c>null</c> si se cancela.</summary>
        private StillRequest? Ask()
        {
            EditorSession session = _services.Session;
            Rational rate = session.Project.Sequences.FirstOrDefault(s => s.Id == session.SequenceId)?.FrameRate ?? Rational.Fps30;
            Time position = session.Position;
            long frame = position.ToFrames(rate);
            string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
            string name = string.Concat($"{session.ProjectName} - cuadro {frame}".Split(Path.GetInvalidFileNameChars()));
            string? path = FileDialogs.Save(_owner, "Exportar fotograma", name + ".png", "png",
                projectFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                ("Imagen PNG (*.png)", "*.png"), ("Imagen JPEG (*.jpg)", "*.jpg;*.jpeg"));
            if (path is null)
            {
                return null;
            }

            // «foto.bmp» no sale como un PNG con nombre BMP: se le añade «.png» («foto.bmp.png»).
            StillFormat format = StillSettings.FormatFor(path);
            path = StillSettings.WithExtension(path, format);
            return new StillRequest(session.Project.Clone(), session.SequenceId, position, path,
                new StillSettings { Format = format }) { ProjectDirectory = projectFolder };
        }

        private async Task ExportAsync(StillRequest request, CancellationTokenSource cancel)
        {
            EditorSession session = _services.Session;
            NotifyExporting(cancel);
            try
            {
                FfmpegExecutable ffmpeg = await Task.Run(() => FfmpegExecutable.TryLocate(out string? reason)
                    ?? throw new ExportException(ExportErrorCode.EncoderMissing, reason ?? "No se encontró el ffmpeg.exe LGPL."), cancel.Token);
                var exporter = new StillExporter(new ExportRunnerOptions
                {
                    Ffmpeg = ffmpeg,
                    Content = _services.Content,
                    FontFiles = () => TitleFontFiles.Find(_services.Packages.Installed),
                    Journal = ExportJournal.Default,
                });
                StillResult result = await exporter.ExportAsync(request, cancel.Token);
                session.Notify(NotificationSeverity.Success, "Fotograma exportado",
                    $"Cuadro {result.Frame} guardado en «{result.OutputPath}» ({result.Width}×{result.Height}).");
            }
            catch (Exception error) when (cancel.IsCancellationRequested && error is OperationCanceledException or ExportException)
            {
                if (!_disposed)
                {
                    session.Notify(NotificationSeverity.Informational, "Fotograma cancelado", "No se guardó ningún archivo.");
                }
            }
            catch (ExportException error)
            {
                ExportErrorView view = ExportErrorText.For(error);
                NotificationAction? action = view.Action == ExportErrorAction.ContinueWithout && !request.ContinueWithoutMissing
                    ? new NotificationAction(view.ActionText ?? "Exportar sin él", () => Start(request with { ContinueWithoutMissing = true }))
                    : null;
                session.Notify(NotificationSeverity.Error, view.Title, view.Message, action);
            }
        }

        /// <summary>El indicador de «exportando»: un aviso con el botón para cancelar.</summary>
        private void NotifyExporting(CancellationTokenSource cancel) =>
            _services.Session.Notify(NotificationSeverity.Informational, "Exportando fotograma...",
                "Se guarda el cuadro del cursor; el atajo no lanza otro hasta que termine.",
                new NotificationAction("Cancelar", () =>
                {
                    if (ReferenceEquals(_running, cancel))
                    {
                        cancel.Cancel();
                    }
                }));
    }
}
