using Rodaje.App.Services;
using Rodaje.App.Services.Format;
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
internal static partial class StillExportCommand
{
    public static IDisposable Bind(ShortcutRouter router, EditorServices services, nint owner)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return new Runner(router, services, owner);
    }

    /// <summary>
    /// El estado de una ventana: el atajo y <see cref="StillExportFlow"/>, que lleva la exportación en marcha. Aquí solo
    /// queda lo de interfaz (el diálogo de guardar) y armar el exportador real. Se usa solo desde el hilo de la interfaz.
    /// </summary>
    private sealed partial class Runner : IDisposable
    {
        private readonly EditorServices _services;
        private readonly nint _owner;
        private readonly IDisposable _binding;
        private readonly StillExportFlow _flow;
        private bool _disposed;

        public Runner(ShortcutRouter router, EditorServices services, nint owner)
        {
            _services = services;
            _owner = owner;
            _flow = new StillExportFlow(Ask, ExportAsync,
                n => _services.Session.Notify(n.Severity, n.Title, n.Message, n.Action));
            _binding = router.Bind(ActionIds.ExportFrame, () => _ = _flow.StartAsync(request: null));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _binding.Dispose();
            _flow.Dispose();
        }

        /// <summary>Abre el diálogo de guardar; <c>null</c> si se cancela.</summary>
        private StillRequest? Ask()
        {
            EditorSession session = _services.Session;
            Rational rate = session.Project.Sequences.FirstOrDefault(s => s.Id == session.SequenceId)?.FrameRate ?? Rational.Fps30;
            Time position = session.Position;
            long frame = position.ToFrames(rate);
            string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
            string name = string.Concat(string.Create(SpanishNumbers.Format, $"{session.ProjectName} - cuadro {frame}").Split(Path.GetInvalidFileNameChars()));
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

        /// <summary>Busca el ffmpeg.exe LGPL y exporta el fotograma con el render real.</summary>
        private async Task<StillResult> ExportAsync(StillRequest request, CancellationToken cancellationToken)
        {
            FfmpegExecutable ffmpeg = await Task.Run(() => FfmpegExecutable.TryLocate(out string? reason)
                ?? throw new ExportException(ExportErrorCode.EncoderMissing, reason ?? "No se encontró el ffmpeg.exe LGPL."), cancellationToken);
            var exporter = new StillExporter(new ExportRunnerOptions
            {
                Ffmpeg = ffmpeg,
                Content = _services.Content,
                FontFiles = () => TitleFontFiles.Find(_services.Packages.Installed),
                Journal = ExportJournal.Default,
            });
            return await exporter.ExportAsync(request, cancellationToken);
        }
    }
}
