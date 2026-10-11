using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Rodaje.App.Shell;

/// <summary>
/// <c>--autoprueba</c>: lo mínimo para saber que el exe publicado arranca de verdad. Con el clip de prueba en la pista
/// principal y seleccionado, muestra cada pestaña del inspector (la curva «Bala» y la reversa en Velocidad), reproduce un segundo y cierra. Sale con 0 si llegó el
/// primer cuadro y nada falló; cualquier excepción queda en <c>rodaje-error.txt</c> y el código es distinto de 0.
/// </summary>
internal static class SelfTestRun
{
    private const string CapturesVariable = "RODAJE_AUTOPRUEBA_CAPTURAS";

    public static async Task RunAsync(MainWindow window, PlayerEngine engine, Metrics metrics)
    {
        int code = 1;
        try
        {
            SelectFirstClip(window);
            foreach (InspectorView.Tab tab in Enum.GetValues<InspectorView.Tab>())
            {
                window.InspectorPane.Show(tab);
                await Task.Delay(250);
            }

            await ShowCurveAsync(window);
            await ToggleReverseAsync(window);
            await ShowExportAsync(window);
            window.InspectorPane.Show(InspectorView.Tab.Properties);
            if (window.Session.Sequence.Duration.Ticks > 0)
            {
                window.Session.Play();
                for (int i = 0; i < 100 && !metrics.HasMilestone("primer_cuadro") && engine.FailureMessage is null; i++)
                {
                    await Task.Delay(100);
                }

                await Task.Delay(1000);
                window.Session.Pause();
            }

            code = engine.FailureMessage is null && (window.Session.Sequence.Duration.Ticks == 0 || metrics.HasMilestone("primer_cuadro")) ? 0 : 1;
            if (code != 0)
            {
                Program.WriteCrash("autoprueba: " + (engine.FailureMessage ?? "no llegó el primer cuadro en 10 s"));
            }
        }
#pragma warning disable CA1031 // La autoprueba informa cualquier fallo con el código de salida.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Program.WriteCrash("autoprueba: " + ex);
            code = 2;
        }

        Program.ExitCode = code;
        window.Close();
    }

    /// <summary>Pone «Bala» al clip para dibujar el editor de curvas y la mini curva con el exe de AOT, y lo deshace.</summary>
    private static async Task ShowCurveAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main
            || !session.Execute(Edits.ApplySpeedPreset(session.SequenceId, main.Clips[0].Id, SpeedPreset.Bullet, session.SpeedScope)))
        {
            return;
        }

        window.InspectorPane.Show(InspectorView.Tab.Speed);
        await Task.Delay(250);
        session.Undo();
    }

    /// <summary>
    /// Invierte el clip con el atajo de la sesión, dibuja la pestaña Velocidad con «◀ Invertido» y lo vuelve a poner hacia
    /// adelante; si el modelo no siguió, la autoprueba falla.
    /// </summary>
    private static async Task ToggleReverseAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        if (session.MainTrack is not { Clips.Count: > 0 } main)
        {
            return;
        }

        string id = main.Clips[0].Id;
        session.Select([id]);
        window.InspectorPane.Show(InspectorView.Tab.Speed);
        if (!session.ToggleReverse() || session.Sequence.GetClip(id).Clip.Speed is not { Reverse: true })
        {
            throw new InvalidOperationException("La autoprueba no pudo invertir el clip.");
        }

        await Task.Delay(250);
        await CaptureAsync(window, "velocidad-invertido");
        if (!session.ToggleReverse() || session.Sequence.GetClip(id).Clip.Speed is { Reverse: true })
        {
            throw new InvalidOperationException("La autoprueba no pudo quitar la reversa del clip.");
        }

        await Task.Delay(100);
    }

    /// <summary>
    /// Abre Exportar con «Avanzado» desplegado y lanza una exportación con el runner de la ventana (hoy la simulación, que
    /// no escribe nada), para dibujar el diálogo y la tarjeta de progreso con el exe de AOT. No toca los ajustes recordados.
    /// </summary>
    private static async Task ShowExportAsync(MainWindow window)
    {
        window.ShowPage(ShellPage.Export);
        for (int i = 0; i < 50 && window.ExportPage?.Model is null; i++)
        {
            await Task.Delay(100);
        }

        await CaptureAsync(window, "exportar-sencillo");
        window.ExportPage?.Model?.SelectPreset(ExportPresets.Vertical.Id);
        await CaptureAsync(window, "exportar-vertical");
        window.ExportPage?.Model?.SelectPreset(ExportPresets.YouTube1080p.Id);
        window.ExportPage?.ShowAdvanced(true);
        await Task.Delay(400);
        await CaptureAsync(window, "exportar-avanzado");
        window.ClosePage();
        if (window.Session.Sequence.Duration.Ticks <= 0 || !window.Exports.Runner.IsSimulation)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), "rodaje-autoprueba.mp4");
        ExportEntry entry = window.Exports.Enqueue(new ExportRequest(window.Session.Project.Clone(), window.Session.SequenceId, output,
            ExportPresets.YouTube1080p.Settings, new ExportRange(Time.Zero, Time.Min(window.Session.Sequence.Duration, Time.FromSeconds(2)))));
        window.InspectorPane.Show(InspectorView.Tab.Jobs);
        for (int i = 0; i < 100 && entry.Progress.State != ExportJobState.Encoding && !entry.IsFinished; i++)
        {
            await Task.Delay(20);
        }

        await CaptureAsync(window, "exportar-progreso");
        for (int i = 0; i < 100 && !entry.IsFinished; i++)
        {
            await Task.Delay(100);
        }

        await CaptureAsync(window, "exportar-terminado");

        if (entry.Progress.State != ExportJobState.Completed)
        {
            throw new InvalidOperationException($"La exportación simulada quedó en {entry.Progress.State}.");
        }

        window.Exports.Remove(entry);
    }

    /// <summary>
    /// Con <c>RODAJE_AUTOPRUEBA_CAPTURAS=carpeta</c> guarda allí la ventana en PNG (dibujada por XAML, no la pantalla), como
    /// evidencia de cómo se ve con el exe de AOT. Sin la variable no hace nada.
    /// </summary>
    private static async Task CaptureAsync(MainWindow window, string name)
    {
        if (Environment.GetEnvironmentVariable(CapturesVariable) is not { Length: > 0 } folder || window.Content is not UIElement root)
        {
            return;
        }

        await Task.Delay(300);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        Directory.CreateDirectory(folder);
        await using FileStream file = File.Create(Path.Combine(folder, name + ".png"));
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    /// <summary>Selecciona el primer clip de la principal para que las pestañas muestren un clip, no el estado vacío.</summary>
    private static void SelectFirstClip(MainWindow window)
    {
        if (window.Session.MainTrack is { Clips.Count: > 0 } main)
        {
            window.Session.Select([main.Clips[0].Id]);
        }
    }
}
