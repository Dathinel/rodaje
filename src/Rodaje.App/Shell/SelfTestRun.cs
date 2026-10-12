using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Modules;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Rodaje.App.Shell;

/// <summary>
/// <c>--autoprueba</c>: lo mínimo para saber que el exe publicado arranca de verdad. Con el clip de prueba en la pista
/// principal y seleccionado, muestra cada pestaña del inspector (la curva «Bala» y la reversa en Velocidad), crea un título,
/// lo escribe, comprueba que el visor lo dibuja y que las asas lo mueven, lo divide y lo deshace, reproduce un segundo y cierra. Sale con 0 si llegó el
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

            await ToolsAsync(window);
            await ShowCurveAsync(window);
            await ToggleReverseAsync(window);
            await TitleAsync(window, engine);
            await ShowExportAsync(window);
            await ModuleAsync(window);
            await FormatAsync(window, engine);
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
            else if (!IsUpstreamCreditShown(window))
            {
                Program.WriteCrash("autoprueba: el crédito upstream de la barra de título no se ve (LICENSE, término adicional 1)");
                code = 1;
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

    private static bool IsUpstreamCreditShown(MainWindow window) =>
        window.UpstreamCredit is { Visibility: Visibility.Visible, ActualWidth: > 0, ActualHeight: > 0 } credit
        && credit.Content as string == UpstreamAttribution.ShortCredit;

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
    /// Crea un título con «Añadir texto» (T), cambia su texto en dos tandas de tecleo, lo divide y lo deshace todo, con la
    /// pestaña Texto a la vista; si el modelo o el historial no siguen, la autoprueba falla.
    /// </summary>
    private static async Task TitleAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        int steps = session.History.Count;
        if (!session.AddTitleAtPlayhead() || session.SelectedTitle is not { } title)
        {
            throw new InvalidOperationException("La autoprueba no pudo crear un título.");
        }

        string id = title.Id;
        await Task.Delay(250);
        var editor = new TitleEditor(session);
        foreach (string text in (string[])["H", "Ho", "Hola", "Hola desde", "Hola desde la autoprueba"])
        {
            editor.SetText(text);
        }

        editor.EndTyping();
        const string Final = "Hola desde la autoprueba\nsegunda línea";
        editor.SetText(Final);
        editor.CommitStyle("stroke-on", s => s.Stroke = new StyleStroke { Color = "#000000", Width = 6 });
        await Task.Delay(250);
        TitleBox box = await WaitForTitleAsync(engine, id, b => b.Width > 0, "El título no se dibujó en el visor en 5 s.");
        await CaptureAsync(window, "texto");
        FrameCapture withTitle = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await SaveFrameAsync(withTitle, "texto-visor");
        await DragTitleAsync(window, engine, box);

        Clip clip = session.Sequence.GetClip(id).Clip;
        if (!session.Execute(Edits.Split(session.SequenceId, id, clip.Start + Time.FromTicks(clip.Duration.Ticks / 2))))
        {
            throw new InvalidOperationException("La autoprueba no pudo dividir el título.");
        }

        Track track = session.Sequence.GetClip(id).Track;
        if (track.Clips.Count(c => c.Generator?.Text == Final) != 2 || session.History.Count != steps + 5)
        {
            throw new InvalidOperationException($"El título dividido no quedó bien ({session.History.Count - steps} pasos).");
        }

        await Task.Delay(250);
        for (int i = 0; i < 5; i++)
        {
            session.Undo();
        }

        if (session.Sequence.FindClip(id) is not null || session.History.Count != steps)
        {
            throw new InvalidOperationException("Deshacer no quitó el título de la autoprueba.");
        }

        SelectFirstClip(window);
        await Task.Delay(250);
        FrameCapture without = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int changed = ChangedPixels(withTitle, without, box, session.Sequence.Width, session.Sequence.Height);
        Program.Metrics.Note($"autoprueba: el título cambia {changed} píxeles del visor");
        if (changed < 500)
        {
            throw new InvalidOperationException($"El título no se ve en el visor: solo cambia {changed} píxeles en su caja.");
        }
    }

    private static async Task<TitleBox> WaitForTitleAsync(PlayerEngine engine, string id, Func<TitleBox, bool> condition, string failure)
    {
        for (int i = 0; i < 50; i++)
        {
            foreach (TitleBox box in engine.TitleBoxes)
            {
                if (box.ClipId == id && condition(box))
                {
                    return box;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(failure);
    }

    /// <summary>Mueve el título 200 px con las asas (el mismo gesto que el ratón) y lo deshace; el visor tiene que seguirlo.</summary>
    private static async Task DragTitleAsync(MainWindow window, PlayerEngine engine, TitleBox box)
    {
        EditorSession session = window.Session;
        var properties = new ClipPropertyEditor(session);
        TitleTransformDrag drag = TitleTransformDrag.Begin(session, properties, box.ClipId, scale: false)
            ?? throw new InvalidOperationException("La autoprueba no pudo agarrar el título.");
        drag.Move(drag.StartX + 100, drag.StartY);
        drag.Move(drag.StartX + 200, drag.StartY);
        drag.Complete();
        await WaitForTitleAsync(engine, box.ClipId, b => b.Transform.X > box.Transform.X + 150, "El visor no siguió al título movido con las asas.");
        await CaptureAsync(window, "texto-movido");
        session.Undo();
        await WaitForTitleAsync(engine, box.ClipId, b => Math.Abs(b.Transform.X - box.Transform.X) < 1, "Deshacer no devolvió el título a su sitio.");
    }

    /// <summary>Píxeles que cambian más de 40 niveles entre los dos cuadros dentro de la caja del título.</summary>
    private static int ChangedPixels(FrameCapture a, FrameCapture b, TitleBox box, int sequenceWidth, int sequenceHeight)
    {
        if (a.Width != b.Width || a.Height != b.Height
            || TitleHandles.Corners(box, sequenceWidth, sequenceHeight, new ImageFrame(0, 0, a.Width / (double)sequenceWidth)) is not { } corners)
        {
            return 0;
        }

        int left = Math.Clamp((int)corners.Min(c => c.X), 0, a.Width);
        int right = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.X)), 0, a.Width);
        int top = Math.Clamp((int)corners.Min(c => c.Y), 0, a.Height);
        int bottom = Math.Clamp((int)Math.Ceiling(corners.Max(c => c.Y)), 0, a.Height);
        int changed = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int i = ((y * a.Width) + x) * 4;
                int d = Math.Abs(a.Rgba[i] - b.Rgba[i]) + Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]) + Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2]);
                changed += d > 40 ? 1 : 0;
            }
        }

        return changed;
    }

    /// <summary>Con <c>RODAJE_AUTOPRUEBA_CAPTURAS</c>, guarda un cuadro del visor (lo que compone la GPU) en PNG.</summary>
    /// <summary>
    /// Cambia el formato del proyecto como el selector del reproductor (9:16 rellenando, 1:1 y 4:5 encajando), dibuja el
    /// visor y el cuadro compuesto de cada uno, abre Exportar con el proyecto vertical (debe salir 1080×1920 sin pedir
    /// barras) y deshace todo.
    /// </summary>
    private static async Task FormatAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        int steps = session.History.Count;
        (int Width, int Height) original = (session.Sequence.Width, session.Sequence.Height);
        foreach ((string name, int width, int height, ClipFit fit) in new[]
        {
            ("formato-9x16", 1080, 1920, ClipFit.Fill), ("formato-1x1", 1080, 1080, ClipFit.Fit), ("formato-4x5", 1080, 1350, ClipFit.Fit),
        })
        {
            if (!session.ChangeFormat(width, height, session.Sequence.FrameRate, fit))
            {
                throw new InvalidOperationException($"No se pudo cambiar el formato a {width}×{height}.");
            }

            await Task.Delay(400);
            FrameCapture frame = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if ((frame.Width, frame.Height) != (width, height))
            {
                throw new InvalidOperationException($"El visor compone {frame.Width}×{frame.Height} en vez de {width}×{height}.");
            }

            await CaptureAsync(window, name);
            await SaveFrameAsync(frame, name + "-cuadro");
            if (name == "formato-9x16")
            {
                window.ShowPage(ShellPage.Export);
                for (int i = 0; i < 50 && window.ExportPage?.Model is null; i++)
                {
                    await Task.Delay(100);
                }

                window.ExportPage?.Model?.SelectPreset(ExportPresets.YouTube1080p.Id);
                await CaptureAsync(window, "exportar-proyecto-vertical");
                if (window.ExportPage?.Model is { } model && (model.OutputSize != (1080, 1920) || model.ShapeDiffers))
                {
                    throw new InvalidOperationException($"Exportar no siguió al proyecto vertical: sale {model.OutputSize}.");
                }

                window.ClosePage();
            }
        }

        while (session.History.Count > steps)
        {
            session.Undo();
        }

        if ((session.Sequence.Width, session.Sequence.Height) != original)
        {
            throw new InvalidOperationException("Deshacer no devolvió el formato del proyecto.");
        }
    }

    /// <summary>La barra de herramientas con un video elegido: aplica estabilizar, los subtítulos dicen «Próximamente».</summary>
    private static async Task ToolsAsync(MainWindow window)
    {
        SelectFirstClip(window);
        await Task.Delay(200);
        (bool stabilize, _) = window.QuickTools.Describe("analysis.stabilize");
        (bool subtitles, string tip) = window.QuickTools.Describe(Views.Analysis.QuickTools.AutoSubtitles);
        if (!stabilize || subtitles || !tip.Contains(Views.Analysis.QuickTools.ComingSoon, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"La barra de herramientas no sigue a la selección (estabilizar {stabilize}, subtítulos {subtitles}).");
        }

        await CaptureAsync(window, "herramientas");
    }

    private static async Task SaveFrameAsync(FrameCapture frame, string name)
    {
        if (Environment.GetEnvironmentVariable(CapturesVariable) is not { Length: > 0 } folder)
        {
            return;
        }

        byte[] bgra = (byte[])frame.Rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4)
        {
            (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        }

        await SavePngAsync(folder, name, bgra, frame.Width, frame.Height);
    }

    /// <summary>
    /// Abre Exportar con «Avanzado» desplegado y exporta de verdad 2 s del clip a MP4 (H.264 y AAC) con el runner de la
    /// ventana, dibujando el diálogo y la tarjeta de progreso con el exe de AOT; luego comprueba el archivo con ffprobe y lo
    /// borra. No toca los ajustes recordados.
    /// </summary>
    private static async Task ShowExportAsync(MainWindow window)
    {
        window.ShowPage(ShellPage.Export);
        for (int i = 0; i < 50 && window.ExportPage?.Model is null; i++)
        {
            await Task.Delay(100);
        }

        await CaptureAsync(window, "exportar-sencillo");
        if (window.ExportPage is { } page && await page.CheckSwitchingAsync(
            [ExportPresets.Vertical.Id, ExportPresets.YouTube4K.Id, ExportPresets.AudioOnly.Id, ExportPresets.YouTube1080p.Id]) is { } failure)
        {
            throw new InvalidOperationException("Exportar: " + failure);
        }

        window.ExportPage?.Model?.SelectPreset(ExportPresets.Vertical.Id);
        await CaptureAsync(window, "exportar-vertical");
        window.ExportPage?.Model?.SelectPreset(ExportPresets.YouTube1080p.Id);
        window.ExportPage?.ShowAdvanced(true);
        await Task.Delay(400);
        await CaptureAsync(window, "exportar-avanzado");
        window.ClosePage();
        if (window.Session.Sequence.Duration.Ticks <= 0)
        {
            return;
        }

        string output = Path.Combine(Path.GetTempPath(), $"rodaje-autoprueba-{Environment.ProcessId}.mp4");
        ExportEntry entry = window.Exports.Enqueue(new ExportRequest(window.Session.Project.Clone(), window.Session.SequenceId, output,
            ExportPresets.YouTube1080p.Settings, new ExportRange(Time.Zero, Time.Min(window.Session.Sequence.Duration, Time.FromSeconds(2)))));
        window.InspectorPane.Show(InspectorView.Tab.Jobs);
        for (int i = 0; i < 100 && entry.Progress.State != ExportJobState.Encoding && !entry.IsFinished; i++)
        {
            await Task.Delay(20);
        }

        await CaptureAsync(window, "exportar-progreso");
        for (int i = 0; i < 600 && !entry.IsFinished; i++)
        {
            await Task.Delay(100);
        }

        await CaptureAsync(window, "exportar-terminado");

        if (entry.Progress.State != ExportJobState.Completed)
        {
            throw new InvalidOperationException($"La exportación quedó en {entry.Progress.State}: {entry.Failure?.Message}");
        }

        window.Exports.Remove(entry);
        if (!window.Exports.Runner.IsSimulation)
        {
            CheckExported(output, entry.Request);
        }
    }

    /// <summary>
    /// Con el módulo eco a mano (Rodaje corriendo desde el repositorio, compilado por <c>app.ps1 -Autoprueba</c>): corre
    /// «contar» como trabajo con la pestaña Trabajos a la vista y aplica una propuesta de marcas en un paso, que se deshace.
    /// Sin el módulo no hace nada.
    /// </summary>
    private static async Task ModuleAsync(MainWindow window)
    {
        ModuleManager modules = window.Modules;
        for (int i = 0; i < 50 && modules.Modules.Count == 0; i++)
        {
            await Task.Delay(100);
        }

        if (modules.Find("rodaje-eco") is null)
        {
            return;
        }

        window.InspectorPane.Show(InspectorView.Tab.Jobs);
        using System.Text.Json.JsonDocument parameters = System.Text.Json.JsonDocument.Parse("""{"pasos":20,"msPorPaso":20}""");
        Rodaje.Modules.Protocol.ModuleResult result = await modules.InvokeAsync("rodaje-eco", "contar", parameters.RootElement, "Autoprueba: contar");
        if (result.Value is not { } value || value.GetProperty("pasos").GetInt32() != 20 || modules.ProcessIds.Count != 1)
        {
            throw new InvalidOperationException("El módulo eco no respondió como se esperaba.");
        }

        await CaptureAsync(window, "trabajos-ia");
        EditorSession session = window.Session;
        int steps = session.History.Count;
        int markers = session.Sequence.Markers.Count;
        Time at = Time.Min(session.Sequence.Duration, Time.FromSeconds(1));
        var proposals = new ModuleProposals(session);
        proposals.Show(new ModuleProposal("Marcas del módulo", session.SequenceId, [new PreviewMark(PreviewMarkKind.Marker, at, at, "eco")],
            _ => [Edits.AddMarker(session.SequenceId, at, "eco"), Edits.AddMarker(session.SequenceId, at + Time.FromSeconds(0.5), "eco")]));
        if (!proposals.Apply() || session.History.Count != steps + 1 || session.Sequence.Markers.Count != markers + 2)
        {
            throw new InvalidOperationException("La propuesta del módulo no quedó en un solo paso.");
        }

        session.Undo();
        if (session.Sequence.Markers.Count != markers)
        {
            throw new InvalidOperationException("Deshacer no quitó la propuesta del módulo.");
        }
    }

    /// <summary>El MP4 de la autoprueba: video H.264 de la duración pedida con <c>moov</c> al principio y audio AAC.</summary>
    private static void CheckExported(string output, ExportRequest request)
    {
        try
        {
            ExportReport report = ExportVerifier.For(FfmpegExecutable.Locate()).Inspect(output);
            double expected = request.Range!.Value.Duration.ToSeconds();
            if (report.Video is not { Codec: "h264" } video || Math.Abs(video.DurationSeconds - expected) > 0.05 || report.MoovAtStart != true
                || report.Audio is not { Codec: "aac" })
            {
                throw new InvalidOperationException($"El MP4 exportado no es el esperado: {report}");
            }
        }
        finally
        {
            File.Delete(output);
        }
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
        await SavePngAsync(folder, name, pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static async Task SavePngAsync(string folder, string name, byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, bgra);
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
