using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Rodaje.App.Services;
using Rodaje.App.Services.MotionTemplates;
using Rodaje.App.Views.Inspector;
using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    private const string SelfTestTemplate = """
        { "format": "rodaje.template", "version": 1, "name": "Rótulo de la autoprueba", "kind": "title",
          "reference": { "width": 1920, "height": 1080 }, "durationSeconds": 4, "minSeconds": 1,
          "body": { "layers": [
            { "id": "nombre", "text": "Nombre Apellido", "x": -360, "y": 300,
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Nombre", "font": { "family": "Segoe UI", "weight": 700 }, "size": 72,
                "background": { "color": "#E63946", "padding": [28, 12] },
                "in": { "durationSeconds": 0.3, "curves": { "opacity": [ { "t": 0, "value": 0 }, { "t": 0.3, "value": 1 } ] } } } },
            { "id": "cargo", "text": "Cargo", "start": 0.1, "x": -360, "y": 400,
              "style": { "format": "rodaje.textstyle", "version": 2, "name": "Cargo", "font": { "family": "Segoe UI" }, "size": 44 } } ] },
          "parameters": [
            { "id": "nombre", "label": "Nombre", "type": "text", "targets": ["layers/nombre/text"] },
            { "id": "cargo", "label": "Cargo", "type": "text", "targets": ["layers/cargo/text"] },
            { "id": "acento", "label": "Color de acento", "type": "color", "targets": ["layers/nombre/style/background/color"] } ] }
        """;

    /// <summary>
    /// Plantillas de movimiento (D-064): pone un rótulo de dos capas en el cursor, comprueba que el visor lo dibuja, cambia
    /// un valor y lleva al informe el tiempo hasta el cuadro nuevo; lo reproduce y anota los cuadros perdidos; compone su
    /// miniatura animada con el exe de AOT, y lo deshace todo.
    /// </summary>
    private static async Task TemplatesAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        if (session.IsSourceActive)
        {
            return;
        }

        int steps = session.History.Count;
        var entry = new MotionTemplateEntry("propios@0.0.0/autoprueba", "Rótulo de la autoprueba", "", "Mío", PackageOrigin.Own,
            ContentReaders.ReadTemplate(SelfTestTemplate), SelfTestTemplate);
        session.Select([]);
        await Task.Delay(200);
        FrameCapture bare = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!session.AddTemplate(entry) || session.SelectedTemplate is not { } clip)
        {
            throw new InvalidOperationException("La autoprueba no pudo poner la plantilla en la línea de tiempo.");
        }

        string id = clip.Id;
        window.InspectorPane.Show(InspectorView.Tab.Text);
        FrameCapture shown = bare;
        int drawn = 0;
        for (int i = 0; i < 40 && drawn < 500; i++)
        {
            if (engine.ShaderFailure is { } failure)
            {
                throw new InvalidOperationException("Los shaders de efectos no compilaron y el visor no dibuja la plantilla: " + failure);
            }

            await Task.Delay(250);
            shown = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            drawn = DifferentPixels(bare, shown);
        }

        if (drawn < 500)
        {
            throw new InvalidOperationException($"La plantilla no se ve en el visor: solo cambia {drawn} píxeles.");
        }

        await CaptureAsync(window, "plantilla");
        await SaveFrameAsync(shown, "plantilla-visor");

        var watch = Stopwatch.StartNew();
        if (!session.SetTemplateValue(id, "acento", JsonValue.Create("#2A9D8F")) || !session.SetTemplateValue(id, "nombre", JsonValue.Create("Ana Ruiz")))
        {
            throw new InvalidOperationException("La autoprueba no pudo cambiar los valores de la plantilla.");
        }

        int changed = 0;
        double elapsed = 0;
        while (changed < 200 && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            FrameCapture next = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            elapsed = watch.Elapsed.TotalMilliseconds;
            changed = DifferentPixels(shown, next);
        }

        if (changed < 200)
        {
            throw new InvalidOperationException($"Cambiar un valor de la plantilla no cambió el visor ({changed} píxeles en 10 s).");
        }

        GeneratorTemplate template = session.Sequence.GetClip(id).Clip.Generator!.Template!;
        if (template.Layers[0].Text != "Ana Ruiz" || template.Layers[0].Style.Background?.Color != "#2A9D8F" || session.History.Count != steps + 3)
        {
            throw new InvalidOperationException($"Los valores de la plantilla no quedaron en el clip ({session.History.Count - steps} pasos).");
        }

        // Incluye la captura (bajar el cuadro de la GPU): es una cota superior del tiempo hasta el cuadro nuevo.
        Program.Metrics.Note(string.Create(CultureInfo.InvariantCulture, $"autoprueba: plantilla, cambiar un valor hasta el cuadro nuevo: {elapsed:0.0} ms ({changed} píxeles distintos)"));
        await CaptureAsync(window, "plantilla-valores");

        long dropped = engine.Snapshot.FramesDropped;
        session.Seek(session.Sequence.GetClip(id).Clip.Start);
        session.Play();
        await Task.Delay(2000);
        session.Pause();
        Program.Metrics.Note(string.Create(CultureInfo.InvariantCulture, $"autoprueba: plantilla, cuadros perdidos reproduciendo 2 s el rótulo de 2 capas: {engine.Snapshot.FramesDropped - dropped}"));

        watch.Restart();
        TemplateStrip? strip = null;
        await Task.Run(() => TemplateThumbnails.Render([entry], [], (_, s) => strip = s)).WaitAsync(TimeSpan.FromSeconds(90));
        if (strip is not { Frames: TemplateThumbnails.Frames, FrameWidth: TemplateThumbnails.Width, FrameHeight: TemplateThumbnails.Height })
        {
            throw new InvalidOperationException("La miniatura animada de la plantilla no se compuso.");
        }

        Program.Metrics.Note(string.Create(CultureInfo.InvariantCulture, $"autoprueba: plantilla, miniatura de 12 cuadros (con abrir la GPU y los shaders): {watch.Elapsed.TotalMilliseconds:0} ms"));

        while (session.History.Count > steps)
        {
            session.Undo();
        }

        if (session.Sequence.FindClip(id) is not null)
        {
            throw new InvalidOperationException("Deshacer no quitó la plantilla de la autoprueba.");
        }

        SelectFirstClip(window);
        await Task.Delay(250);
    }

    private static int DifferentPixels(FrameCapture a, FrameCapture b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return int.MaxValue;
        }

        int count = 0;
        for (int i = 0; i < a.Rgba.Length; i += 4)
        {
            count += Math.Abs(a.Rgba[i] - b.Rgba[i]) + Math.Abs(a.Rgba[i + 1] - b.Rgba[i + 1]) + Math.Abs(a.Rgba[i + 2] - b.Rgba[i + 2]) > 48 ? 1 : 0;
        }

        return count;
    }
}
