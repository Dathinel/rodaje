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
internal static class StillExportCommand
{
    public static IDisposable Bind(ShortcutRouter router, EditorServices services, nint owner)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(services);
        return router.Bind(ActionIds.ExportFrame, () => Run(services, owner));
    }

    private static async void Run(EditorServices services, nint owner)
    {
        try
        {
            await RunAsync(services, owner);
        }
#pragma warning disable CA1031 // Un fotograma que falla no debe cerrar la app: se avisa.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo exportar el fotograma", ex.Message);
        }
    }

    private static async Task RunAsync(EditorServices services, nint owner)
    {
        EditorSession session = services.Session;
        Rational rate = session.Project.Sequences.FirstOrDefault(s => s.Id == session.SequenceId)?.FrameRate ?? Rational.Fps30;
        Time position = session.Position;
        long frame = position.ToFrames(rate);
        string? projectFolder = session.FilePath is { } file ? Path.GetDirectoryName(file) : null;
        string name = string.Concat($"{session.ProjectName} - cuadro {frame}".Split(Path.GetInvalidFileNameChars()));
        string? path = FileDialogs.Save(owner, "Exportar fotograma", name + ".png", "png",
            projectFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ("Imagen PNG (*.png)", "*.png"), ("Imagen JPEG (*.jpg)", "*.jpg;*.jpeg"));
        if (path is null)
        {
            return;
        }

        var request = new StillRequest(session.Project.Clone(), session.SequenceId, position, path,
            new StillSettings { Format = StillSettings.FormatFor(path) }) { ProjectDirectory = projectFolder };
        await ExportAsync(services, request);
    }

    private static async Task ExportAsync(EditorServices services, StillRequest request)
    {
        EditorSession session = services.Session;
        try
        {
            FfmpegExecutable ffmpeg = await Task.Run(() => FfmpegExecutable.TryLocate(out string? reason)
                ?? throw new ExportException(ExportErrorCode.EncoderMissing, reason ?? "No se encontró el ffmpeg.exe LGPL."));
            var exporter = new StillExporter(new ExportRunnerOptions
            {
                Ffmpeg = ffmpeg,
                Content = services.Content,
                FontFiles = () => TitleFontFiles.Find(services.Packages.Installed),
                Journal = ExportJournal.Default,
            });
            StillResult result = await exporter.ExportAsync(request);
            session.Notify(NotificationSeverity.Success, "Fotograma exportado",
                $"Cuadro {result.Frame} guardado en «{result.OutputPath}» ({result.Width}×{result.Height}).");
        }
        catch (ExportException error)
        {
            ExportErrorView view = ExportErrorText.For(error);
            NotificationAction? action = view.Action == ExportErrorAction.ContinueWithout && !request.ContinueWithoutMissing
                ? new NotificationAction(view.ActionText ?? "Exportar sin él", () => Retry(services, request with { ContinueWithoutMissing = true }))
                : null;
            session.Notify(NotificationSeverity.Error, view.Title, view.Message, action);
        }
    }

    private static async void Retry(EditorServices services, StillRequest request)
    {
        try
        {
            await ExportAsync(services, request);
        }
#pragma warning disable CA1031 // Igual que la primera vez: se avisa y el editor sigue.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            services.Session.Notify(NotificationSeverity.Error, "No se pudo exportar el fotograma", ex.Message);
        }
    }
}
