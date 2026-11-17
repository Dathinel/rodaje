using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Player;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Windows.ApplicationModel.DataTransfer;

namespace Rodaje.App.Shell;

/// <summary>
/// <c>--autoprueba</c> en Linux (D-066, fase 8): con el clip de prueba en la línea de tiempo, guarda el proyecto de
/// aceptación y lo vuelve a abrir, lo edita (dividir, deshacer y rehacer), pasa un texto por el portapapeles del sistema,
/// reproduce con el visor de Vulkan hasta ver cuadros en la ventana y exporta un segundo a ProRes por la CPU. Sale con 0
/// si todo pasó; el motivo de un fallo queda en <c>rodaje-error.txt</c>. Las capturas las toma el script (Xvfb).
/// </summary>
internal static partial class SelfTestRun
{
    public static async Task RunAsync(MainWindow window, PlayerEngine engine, Metrics metrics)
    {
        int code = 1;
        string folder = Path.Combine(Path.GetTempPath(), $"rodaje-autoprueba-{Environment.ProcessId}");
        try
        {
            EditorSession session = window.Session;
            await WaitForAsync(() => ClipCount(session) > 0, 30, "el clip de prueba no llegó a la línea de tiempo");
            Directory.CreateDirectory(folder);
            await ReopenAsync(session, Path.Combine(folder, "aceptacion.rodaje"), metrics);
            Edit(session, metrics);
            await ClipboardAsync(metrics);
            await PlayAsync(session, engine, metrics);
            await ExportAsync(window, Path.Combine(folder, "aceptacion.mov"), metrics);
            code = engine.FailureMessage is null ? 0 : 1;
            if (code != 0)
            {
                Program.WriteCrash("autoprueba: " + engine.FailureMessage);
            }
        }
#pragma warning disable CA1031 // La autoprueba informa cualquier fallo con el código de salida.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash("autoprueba: " + ex);
            code = 2;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        WriteReport(metrics);
        Program.ExitCode = code;
        window.Close();
    }

    private static int ClipCount(EditorSession session) => session.Sequence.VideoTracks.Sum(t => t.Clips.Count);

    /// <summary>Abrir: guarda el proyecto en un <c>.rodaje</c> y lo vuelve a abrir con los mismos clips.</summary>
    private static async Task ReopenAsync(EditorSession session, string path, Metrics metrics)
    {
        int clips = ClipCount(session);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!await session.SaveAsAsync(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("Abrir: no se pudo guardar el proyecto de aceptación.");
        }

        if (!await session.OpenProjectAsync(path) || ClipCount(session) != clips || session.FilePath != path)
        {
            throw new InvalidOperationException($"Abrir: el proyecto reabierto tiene {ClipCount(session)} clips (se guardaron {clips}).");
        }

        metrics.Note($"autoprueba: guardar y abrir en {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Editar: divide el primer clip por la mitad, deshace y rehace.</summary>
    private static void Edit(EditorSession session, Metrics metrics)
    {
        int clips = ClipCount(session);
        session.Seek(Time.FromSeconds(session.Sequence.Duration.ToSeconds() / 2));
        if (!session.SplitAtPlayhead() || ClipCount(session) != clips + 1)
        {
            throw new InvalidOperationException("Editar: dividir no dejó un clip más.");
        }

        if (!session.Undo() || ClipCount(session) != clips || !session.Redo() || ClipCount(session) != clips + 1)
        {
            throw new InvalidOperationException("Editar: deshacer y rehacer la división no dejaron los clips esperados.");
        }

        metrics.Note("autoprueba: dividir, deshacer y rehacer");
    }

    /// <summary>El portapapeles del sistema (X11) con un texto de ida y vuelta, como «Copiar informe».</summary>
    private static async Task ClipboardAsync(Metrics metrics)
    {
        string text = $"Rodaje autoprueba {Environment.ProcessId}";
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        string back = await Clipboard.GetContent().GetTextAsync();
        if (back != text)
        {
            throw new InvalidOperationException($"Portapapeles: se copió «{text}» y se leyó «{back}».");
        }

        metrics.Note("autoprueba: portapapeles de ida y vuelta");
    }

    /// <summary>Reproducir: desde el principio, hasta que el visor muestre cuadros y el tiempo avance.</summary>
    private static async Task PlayAsync(EditorSession session, PlayerEngine engine, Metrics metrics)
    {
        session.Seek(Time.Zero);
        session.Play();
        await WaitForAsync(() => metrics.HasMilestone("primer_cuadro") && metrics.HasMilestone("cadena_en_panel") || engine.FailureMessage is not null,
            30, "no llegó el primer cuadro al visor");
        await Task.Delay(1500);
        Time reached = session.Position;
        session.Pause();
        if (engine.FailureMessage is { } failure)
        {
            throw new InvalidOperationException("Reproducir: " + failure);
        }

        if (reached <= Time.FromSeconds(0.25))
        {
            throw new InvalidOperationException($"Reproducir: en 1,5 s el tiempo solo llegó a {reached.ToSeconds():F2} s.");
        }

        metrics.Note($"autoprueba: reproducir hasta {reached.ToSeconds():F2} s en el visor de {engine.DeviceDescription}");
    }

    /// <summary>Exportar: un segundo a ProRes 422 HQ por la CPU (Vulkan), comprobado con ffprobe.</summary>
    private static async Task ExportAsync(MainWindow window, string output, Metrics metrics)
    {
        EditorSession session = window.Session;
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mov,
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.ProRes,
                EncoderId = "prores_ks",
                BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma422,
                Profile = "hq",
                RateControl = RateControl.ProfileDefined,
            },
        };
        var range = new ExportRange(Time.Zero, Time.Min(session.Sequence.Duration, Time.FromSeconds(1)));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ExportEntry entry = window.Exports.Enqueue(new ExportRequest(session.Project.Clone(), session.SequenceId, output, settings, range));
        await WaitForAsync(() => entry.IsFinished, 300, "la exportación no terminó en 5 min");
        if (entry.Progress.State != ExportJobState.Completed)
        {
            throw new InvalidOperationException($"Exportar: quedó en {entry.Progress.State}: {entry.Failure?.Message}");
        }

        window.Exports.Remove(entry);
        ExportReport report = ExportVerifier.For(FfmpegExecutable.Locate()).Inspect(output);
        if (report.Video is not { Codec: "prores" } video || Math.Abs(video.DurationSeconds - range.Duration.ToSeconds()) > 0.05)
        {
            throw new InvalidOperationException($"Exportar: el archivo no es el esperado: {report}");
        }

        metrics.Note($"autoprueba: exportar {range.Duration.ToSeconds():F1} s a ProRes en {clock.ElapsedMilliseconds} ms");
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds, string failure)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException($"En {seconds} s {failure}.");
            }

            await Task.Delay(100);
        }
    }
}
