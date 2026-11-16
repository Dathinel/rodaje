using Rodaje.App.Services;
using Rodaje.App.Services.Interchange;
using Rodaje.Core.Interchange;
using Rodaje.Core.Model;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Intercambio (D-062): exporta la secuencia de prueba a EDL, FCPXML y OTIO en <c>%TEMP%</c>, relee cada archivo y lo
    /// compara con lo que su formato dice que conserva (<see cref="Fidelity.Project"/>), no solo el número de clips. Después
    /// importa el OTIO con el servicio de la app y deshace la importación. No toca el disco del proyecto.
    /// </summary>
    private static async Task InterchangeAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        Project project = session.Project;
        Sequence sequence = session.Sequence;
        if (sequence.Duration.Ticks == 0)
        {
            return;
        }

        string folder = Path.Combine(Path.GetTempPath(), "rodaje-autoprueba-intercambio-" + Environment.ProcessId);
        Directory.CreateDirectory(folder);
        try
        {
            InterchangeOptions options = InterchangeService.OptionsFor(sequence);
            foreach (InterchangeFormat format in Enum.GetValues<InterchangeFormat>())
            {
                string path = Path.Combine(folder, "prueba" + InterchangeFiles.Extension(format));
                (IReadOnlyList<string> files, _) = await InterchangeService.ExportAsync(path, project, sequence.Id, options);

                // La EDL escribe un archivo por pista de video: cada uno se compara con su pista.
                for (int i = 0; i < files.Count; i++)
                {
                    InterchangeOptions single = format == InterchangeFormat.Edl
                        ? new InterchangeOptions { FrameRate = options.FrameRate, VideoTracks = [i], IncludeAudio = i == 0 }
                        : options;
                    TimelineDraft expected = Fidelity.Project(project.Clone(), sequence.Clone(), format, single, new InterchangeReport());
                    string text = await File.ReadAllTextAsync(files[i]);
                    TimelineDraft read = InterchangeFiles.ReadDraft(text, format, folder, single, new InterchangeReport());
                    if (Fidelity.Difference(expected, read) is { } difference)
                    {
                        throw new InvalidOperationException($"El {format} exportado ({Path.GetFileName(files[i])}) no vuelve igual: {difference}");
                    }
                }
            }

            int steps = session.History.Count;
            var service = new InterchangeService(new FfmpegMediaProber());
            InterchangeResult result = await service.ReadAsync(Path.Combine(folder, "prueba.otio"), project, options);
            if (!session.Execute(service.ImportCommand(result, session.SequenceId, session.EditPosition)) || session.History.Count != steps + 1)
            {
                throw new InvalidOperationException("Importar el OTIO no dejó un solo paso en el historial.");
            }

            session.Undo();
            if (session.History.Count != steps)
            {
                throw new InvalidOperationException("Deshacer no quitó la importación.");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
