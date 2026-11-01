using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Diagnostics;
using Rodaje.App.Services;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Modules;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Export;
using Rodaje.App.Views.Inspector;
using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Views.Player;
using Rodaje.App.Views.Preferences;
using Rodaje.App.Views.Shortcuts;
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
internal static partial class SelfTestRun
{
    private const string CapturesVariable = "RODAJE_AUTOPRUEBA_CAPTURAS";

    public static async Task RunAsync(MainWindow window, PlayerEngine engine, Metrics metrics)
    {
        int code = 1;
        try
        {
            await CaptureAsync(window, "inicio");
            SelectFirstClip(window);
            foreach (InspectorView.Tab tab in Enum.GetValues<InspectorView.Tab>())
            {
                window.InspectorPane.Show(tab);
                await Task.Delay(250);
                await CaptureAsync(window, "inspector-" + tab.ToString().ToLowerInvariant());
            }

            await ToolsAsync(window);
            await PreferencesAsync(window);
            await ShortcutsAsync(window);
            await ShowCurveAsync(window);
            await ToggleReverseAsync(window);
            await TitleAsync(window, engine);
            await ImageAsync(window, engine);
            await MasksAsync(window, engine);
            await TrackingAsync(window, engine);
            await TransitionsAsync(window, engine);
            await TransformAsync(window, engine);
            await ColorAsync(window, engine);
            await MixerAsync(window);
            await ShowExportAsync(window);
            await PluginsAsync(window);
            await ModuleAsync(window);
            await FormatAsync(window, engine);
            window.InspectorPane.Show(InspectorView.Tab.Properties);
            await LayoutAsync(window);
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

    /// <summary>
    /// Abre el mezclador, crea dos buses, manda la primera pista a uno y le envía a otro con una reverb, le pone una puerta,
    /// captura y lo deshace todo: las tiras, los medidores y los efectos nuevos corren con el exe de AOT.
    /// </summary>
    private static async Task MixerAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        window.ToggleMixer();
        await Task.Delay(300);
        if (session.Sequence.AudioTracks.Count > 0)
        {
            var buses = Rodaje.Core.Commands.Edits.AddBuses(session.SequenceId, ["Voces", "Efectos"]);
            if (session.Execute(buses))
            {
                string track = session.Sequence.AudioTracks[0].Id;
                int steps = 1;
                steps += session.Execute(Rodaje.Core.Commands.Edits.SetTrackOutput(session.SequenceId, track, buses.CreatedIds[0])) ? 1 : 0;
                steps += session.Execute(Rodaje.Core.Commands.Edits.AddEffect(session.SequenceId, buses.CreatedIds[1], Rodaje.Core.Model.BuiltInEffects.Reverb)) ? 1 : 0;
                steps += session.Execute(Rodaje.Core.Commands.Edits.SetSend(session.SequenceId, track, buses.CreatedIds[1], gainDb: -10)) ? 1 : 0;
                steps += session.Execute(Rodaje.Core.Commands.Edits.AddEffect(session.SequenceId, track, Rodaje.Core.Model.BuiltInEffects.Gate)) ? 1 : 0;
                await Task.Delay(400);
                await CaptureAsync(window, "mezclador");
                for (int i = 0; i < steps; i++)
                {
                    session.Undo();
                }
            }
        }

        window.ToggleMixer();
        await Task.Delay(200);
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
    /// Crea un título con «Añadir texto» (T), cambia su texto en dos tandas de tecleo, lo divide y lo deshace todo, con la
    /// pestaña Texto a la vista; si el modelo o el historial no siguen, la autoprueba falla.
    /// </summary>
    private static async Task TitleAsync(MainWindow window, PlayerEngine engine)
    {
        EditorSession session = window.Session;
        int steps = session.History.Count;
        FrameCapture bare = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
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
        (FrameCapture withTitle, _) = await CaptureChangedAsync(engine, bare, box, session.Sequence);
        await SaveFrameAsync(withTitle, "texto-visor");
        await DragTitleAsync(window, engine, box);
        await ProTitleAsync(window, engine, editor, id, withTitle);

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
        (_, int changed) = await CaptureChangedAsync(engine, withTitle, box, session.Sequence);
        Program.Metrics.Note($"autoprueba: el título cambia {changed} píxeles del visor");
        if (changed < 500)
        {
            throw new InvalidOperationException($"El título no se ve en el visor: solo cambia {changed} píxeles en su caja.");
        }
    }

    /// <summary>
    /// Lo profesional del título: degradado, un borde más, arco, «Hola» en negrita roja y salida por letra; el visor tiene
    /// que cambiar y la pestaña Texto se captura (<c>texto-pro</c>). Los dos pasos se deshacen.
    /// </summary>
    private static async Task ProTitleAsync(MainWindow window, PlayerEngine engine, TitleEditor editor, string id, FrameCapture plain)
    {
        EditorSession session = window.Session;
        int steps = session.History.Count;
        editor.CommitStyle("pro", s =>
        {
            s.Fill.Gradient = new StyleGradient { Angle = 90, Stops = [new ColorStop(0, "#FFD400"), new ColorStop(1, "#FF3D00")] };
            s.ExtraStrokes = [new StyleStroke { Color = "#FFFFFF", Width = 12 }];
            s.Path = new StylePath { Bend = 50 };
            s.Out = Rodaje.Core.Text.TextAnimationPresets.Find("rebote")!.Out();
        });
        editor.FormatText(0, 4, s => s with { Weight = 900, Color = "#E01010" }, "Negrita");
        if (session.History.Count != steps + 2)
        {
            throw new InvalidOperationException("El formato por tramos o el estilo profesional no quedaron en el historial.");
        }

        await Task.Delay(400);
        TitleBox box = await WaitForTitleAsync(engine, id, b => b.Width > 0, "El título en arco no se dibujó en 5 s.");
        await CaptureAsync(window, "texto-pro");
        (FrameCapture pro, int changed) = await CaptureChangedAsync(engine, plain, box, session.Sequence);
        await SaveFrameAsync(pro, "texto-pro-visor");
        Program.Metrics.Note($"autoprueba: el título profesional cambia {changed} píxeles respecto del simple");
        if (changed < 500)
        {
            throw new InvalidOperationException($"El título profesional se ve igual que el simple ({changed} píxeles distintos; {Viewer(engine, pro, box)}).");
        }

        session.Undo();
        session.Undo();
        await Task.Delay(200);
    }

    /// <summary>Lo que explica una captura sin cambios: shaders de efectos, capas saltadas, tamaño del cuadro y caja del título.</summary>
    private static string Viewer(PlayerEngine engine, FrameCapture frame, TitleBox box)
    {
        PlayerEngine.EffectTotals totals = engine.Effects;
        return $"shaders de efectos: {engine.ShaderFailure ?? "bien"}; cuadros {totals.Frames}, efectos saltados {totals.Skipped}, contenido que faltó {totals.Missing}; "
            + $"captura {frame.Width}×{frame.Height}; caja {box.Width:0}×{box.Height:0} en ({box.Transform.X:0}, {box.Transform.Y:0}); scRGB {engine.ViewerIsScRgb}";
    }

    /// <summary>
    /// Con la caché de sombreadores vacía (primer arranque, CI) el cuadro nuevo tarda en llegar: se reintenta hasta 10 s. Si los
    /// shaders de efectos no compilaron, ningún título se dibuja y se dice eso en vez de «no cambia».
    /// </summary>
    private static async Task<(FrameCapture Frame, int Changed)> CaptureChangedAsync(PlayerEngine engine, FrameCapture reference, TitleBox box, Sequence sequence)
    {
        FrameCapture frame = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        int changed = ChangedPixels(reference, frame, box, sequence.Width, sequence.Height);
        for (int i = 0; i < 40 && changed < 500; i++)
        {
            if (engine.ShaderFailure is { } failure)
            {
                throw new InvalidOperationException("Los shaders de efectos no compilaron y el visor no dibuja títulos: " + failure);
            }

            await Task.Delay(250);
            frame = await engine.CaptureFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
            changed = ChangedPixels(reference, frame, box, sequence.Width, sequence.Height);
        }

        return (frame, changed);
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
        TitleTransformDrag drag = TitleTransformDrag.Begin(session, properties, box.ClipId, TitleGesture.Move, box.Transform)
            ?? throw new InvalidOperationException("La autoprueba no pudo agarrar el título.");
        drag.Move(drag.StartX + 100, drag.StartY);
        drag.Move(drag.StartX + 200, drag.StartY);
        drag.Complete();
        await WaitForTitleAsync(engine, box.ClipId, b => b.Transform.X > box.Transform.X + 150, "El visor no siguió al título movido con las asas.");
        await CaptureAsync(window, "texto-movido");
        session.Undo();
        await WaitForTitleAsync(engine, box.ClipId, b => Math.Abs(b.Transform.X - box.Transform.X) < 1, "Deshacer no devolvió el título a su sitio.");

        TitleTransformDrag turn = TitleTransformDrag.Begin(session, properties, box.ClipId, TitleGesture.Rotate, box.Transform)
            ?? throw new InvalidOperationException("La autoprueba no pudo girar el título.");
        turn.Rotate(turn.StartRotation + 30);
        turn.Complete();
        await WaitForTitleAsync(engine, box.ClipId, b => Math.Abs(b.Transform.RotationDegrees - box.Transform.RotationDegrees - 30) < 0.5, "El visor no siguió al título girado con el asa.");
        await CaptureAsync(window, "texto-girado");
        session.Undo();
        await WaitForTitleAsync(engine, box.ClipId, b => Math.Abs(b.Transform.RotationDegrees - box.Transform.RotationDegrees) < 0.5, "Deshacer no devolvió el giro del título.");
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
            // Con el panel a menos de la mitad de la secuencia (pantallas de 1024×768) el visor compone a la mitad a propósito.
            if ((frame.Width, frame.Height) != (width, height) && (frame.Width, frame.Height) != (width / 2, height / 2))
            {
                throw new InvalidOperationException($"El visor compone {frame.Width}×{frame.Height} en vez de {width}×{height}.");
            }

            await CaptureAsync(window, name);
            await SaveFrameAsync(frame, name + "-cuadro");
            await CheckCanvasAsync(window, name);
            if (name == "formato-9x16")
            {
                await SafeZonesAsync(window);
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

        await Task.Delay(400);
        await CheckCanvasAsync(window, "formato-16x9");
    }

    /// <summary>
    /// Zonas seguras sobre el proyecto vertical: TikTok (con la imagen compuesta de verdad), los clásicos, YouTube 16:9
    /// (avisa que son para otra forma) y el editor de zonas propias con una zona nueva, que se cierra sin guardar.
    /// </summary>
    private static async Task SafeZonesAsync(MainWindow window)
    {
        PlayerView player = window.PlayerPane;
        Services.Settings.AppSettings settings = window.Settings;
        (bool show, string? zones) = (settings.ShowSafeAreas, settings.SafeZones);
        foreach ((string reference, string name) in new[]
        {
            ("fabrica/tiktok", "zonas-tiktok"), ("fabrica/instagram-reels", "zonas-reels"), ("fabrica/clasicos", "zonas-clasicos"), ("fabrica/youtube", "zonas-otra-forma"),
        })
        {
            player.ChooseSafeZones(reference);
            if (player.ShownSafeZones?.Reference != reference)
            {
                throw new InvalidOperationException($"El visor no muestra las zonas {reference}.");
            }

            await CaptureAsync(window, name);
            if (name == "zonas-tiktok" && Environment.GetEnvironmentVariable(CapturesVariable) is { Length: > 0 } folder
                && WindowCapture.Client(window.Hwnd) is { } shot)
            {
                await SavePngAsync(folder, name + "-pantalla", shot.Bgra, shot.Width, shot.Height);
            }
        }

        player.ChooseSafeZones("fabrica/tiktok");
        if (player.EditSafeZonesForSelfTest(true) != 5)
        {
            throw new InvalidOperationException("El editor de zonas propias no empezó de las de fábrica con una zona nueva.");
        }

        await CaptureAsync(window, "zonas-editor");
        player.EditSafeZonesForSelfTest(false);
        player.ChooseSafeZones(null);
        if (player.ShownSafeZones is not null)
        {
            throw new InvalidOperationException("«Ninguno» no quitó las zonas del visor.");
        }

        (settings.ShowSafeAreas, settings.SafeZones) = (show, zones);
    }

    /// <summary>
    /// Con la ventana compuesta de verdad (la imagen del render incluida), recorre la fila y la columna del medio del lienzo
    /// desde fuera: el primer píxel distinto del gris del visor tiene que ser el contorno, justo un píxel antes de la imagen,
    /// en los cuatro lados. Después muestra el aviso del visor vacío y comprueba que queda dentro de la imagen.
    /// </summary>
    private static async Task CheckCanvasAsync(MainWindow window, string name)
    {
        PlayerView player = window.PlayerPane;
        await Task.Delay(300);
        if (player.PlacementInWindow() is not { } placed || WindowCapture.Client(window.Hwnd) is not { } shot)
        {
            throw new InvalidOperationException($"No se pudo medir el lienzo del visor en {name}.");
        }

        if (Environment.GetEnvironmentVariable(CapturesVariable) is { Length: > 0 } folder)
        {
            await SavePngAsync(folder, name + "-pantalla", shot.Bgra, shot.Width, shot.Height);
        }

        Rodaje.Render.PixelRect r = placed.Image;
        int midX = r.X + (r.Width / 2);
        int midY = r.Y + (r.Height / 2);
        int[] found =
        [
            Edge(shot, r.X - 6, midY, 1, 0), Edge(shot, r.X + r.Width + 5, midY, -1, 0),
            Edge(shot, midX, r.Y - 6, 0, 1), Edge(shot, midX, r.Y + r.Height + 5, 0, -1),
        ];
        // El área cliente que da Windows puede correrse un píxel respecto del XAML: se mide el lienzo entre sus contornos.
        bool crisp = found.All(at => at >= 0) && IsOutline(shot, found[0], midY) && IsOutline(shot, found[1], midY)
            && IsOutline(shot, midX, found[2]) && IsOutline(shot, midX, found[3])
            && !IsOutline(shot, found[0] + 1, midY) && !IsOutline(shot, found[1] - 1, midY)
            && !IsOutline(shot, midX, found[2] + 1) && !IsOutline(shot, midX, found[3] - 1);
        int width = found[1] - found[0] - 1;
        int height = found[3] - found[2] - 1;
        if (!crisp || width != r.Width || height != r.Height || Math.Abs(found[0] + 1 - r.X) > 1 || Math.Abs(found[2] + 1 - r.Y) > 1)
        {
            throw new InvalidOperationException(
                $"El contorno del lienzo no coincide con la imagen en {name}: contornos en {string.Join(", ", found)} (izquierda, derecha, arriba, abajo), " +
                $"lienzo de {width}×{height} y el render pinta {r.Width}×{r.Height} en ({r.X}, {r.Y}); contorno de un píxel nítido: {crisp}.");
        }

        player.ShowHintForSelfTest(true);
        await Task.Delay(200);
        if (player.PlacementInWindow() is { } hinted)
        {
            double scale = window.Content.XamlRoot.RasterizationScale;
            PanelRectCheck(hinted.Status, r, scale, name);
        }

        await CaptureAsync(window, name + "-aviso");
        player.ShowHintForSelfTest(false);
    }

    private static void PanelRectCheck(PanelRect status, Rodaje.Render.PixelRect image, double scale, string name)
    {
        bool inside = status.Width > 0 && status.Left * scale >= image.X - 0.5 && status.Top * scale >= image.Y - 0.5
            && status.Right * scale <= image.X + image.Width + 0.5 && status.Bottom * scale <= image.Y + image.Height + 0.5;
        double offCenter = Math.Abs(((status.Left + status.Right) / 2 * scale) - (image.X + (image.Width / 2.0)));
        if (!inside || offCenter > 2)
        {
            throw new InvalidOperationException($"El aviso del visor vacío se sale del lienzo o no queda centrado en {name}.");
        }
    }

    /// <summary>El píxel es del contorno del lienzo (#3A3A3A).</summary>
    private static bool IsOutline((byte[] Bgra, int Width, int Height) shot, int x, int y)
    {
        int at = ((y * shot.Width) + x) * 4;
        return x >= 0 && y >= 0 && x < shot.Width && y < shot.Height
            && Math.Abs(shot.Bgra[at] - 0x3A) <= 3 && Math.Abs(shot.Bgra[at + 1] - 0x3A) <= 3 && Math.Abs(shot.Bgra[at + 2] - 0x3A) <= 3;
    }

    /// <summary>Desde (x, y) hacia (dx, dy): dónde está el primer píxel que no es el gris del visor (#242424).</summary>
    private static int Edge((byte[] Bgra, int Width, int Height) shot, int x, int y, int dx, int dy)
    {
        for (int i = 0; i < 12; i++, x += dx, y += dy)
        {
            if (x < 0 || y < 0 || x >= shot.Width || y >= shot.Height)
            {
                break;
            }

            int at = ((y * shot.Width) + x) * 4;
            if (Math.Abs(shot.Bgra[at] - 0x24) > 3 || Math.Abs(shot.Bgra[at + 1] - 0x24) > 3 || Math.Abs(shot.Bgra[at + 2] - 0x24) > 3)
            {
                return dx != 0 ? x : y;
            }
        }

        return -1;
    }

    /// <summary>
    /// La página de atajos con el teclado y el ratón dibujados: abre una tecla y le da una acción a una combinación libre,
    /// abre un botón lateral del ratón y hace lo mismo, y restablece las dos. Si el registro no siguió, falla.
    /// </summary>
    private static async Task ShortcutsAsync(MainWindow window)
    {
        window.ShowPage(ShellPage.Shortcuts);
        if (window.ShortcutsPage is not { } page)
        {
            throw new InvalidOperationException("No se abrió la página de atajos.");
        }

        page.AdvancedModeTab.IsChecked = true;
        page.DrawingsToggle.IsChecked = true;
        await Task.Delay(300);
        ShortcutRegistry registry = page.Registry;

        var chord = new KeyChord(0x4A, KeyModifiers.Control | KeyModifiers.Shift);
        if (registry.FindOwner(chord) is { } taken)
        {
            throw new InvalidOperationException($"Ctrl+Mayús+J ya es de {taken}; la autoprueba necesita una combinación libre.");
        }

        page.OpenKey(chord.Key);
        await page.BindingPanel.ChooseAsync(chord.Modifiers, ActionIds.AddMarker);
        if (registry.FindOwner(chord) != ActionIds.AddMarker)
        {
            throw new InvalidOperationException("El teclado dibujado no asignó Ctrl+Mayús+J.");
        }

        await CaptureAsync(window, "atajos-teclado");
        page.BindingPanel.ResetPart();
        if (registry.FindOwner(chord) is not null)
        {
            throw new InvalidOperationException("Restablecer la tecla J no devolvió lo de fábrica.");
        }

        var side = new MouseChord(MouseInput.X1, KeyModifiers.Control);
        page.MouseDrawingView.SetContext(MouseContext.Timeline);
        page.OpenMouse(MouseInput.X1);
        await page.BindingPanel.ChooseAsync(side.Modifiers, ActionIds.Split);
        if (registry.Mouse.Get(MouseContext.Timeline, side) != ActionIds.Split)
        {
            throw new InvalidOperationException("El ratón dibujado no asignó Ctrl+lateral atrás.");
        }

        await CaptureAsync(window, "atajos-raton");
        page.BindingPanel.ResetPart();
        if (registry.Mouse.Get(MouseContext.Timeline, side) is not null || registry.Mouse.IsCustomized(MouseContext.Timeline, side))
        {
            throw new InvalidOperationException("Restablecer el botón lateral no devolvió lo de fábrica.");
        }

        page.CloseEditor();
        window.ClosePage();
        await Task.Delay(200);
    }

    /// <summary>
    /// La barra de herramientas con un video elegido: aplica estabilizar, y subtítulos aplica si está su módulo o, si no,
    /// dice que falta.
    /// </summary>
    private static async Task ToolsAsync(MainWindow window)
    {
        SelectFirstClip(window);
        await Task.Delay(200);
        (bool stabilize, string stabilizeTip) = window.QuickTools.Describe("analysis.stabilize");
        (bool subtitles, string tip) = window.QuickTools.Describe(Views.Analysis.QuickTools.AutoSubtitles);
        // El exe se puede autoprobar sin el medio opcional de TestResults\media-cache. En ese caso la barra debe
        // explicar por qué no aplica, pero no se pueden probar sus acciones sobre un clip inexistente.
        if (!stabilize && stabilizeTip.Contains("Selecciona un clip de video", StringComparison.Ordinal))
        {
            if (subtitles || !tip.Contains("Selecciona un clip con voz", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"La barra no explica que falta un clip compatible (estabilizar {stabilize}, subtítulos {subtitles}).");
            }

            await CaptureAsync(window, "herramientas-sin-clip");
            return;
        }

        if (!stabilize || tip.Contains("Próximamente", StringComparison.Ordinal) || (!subtitles && !tip.Contains("módulo", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"La barra de herramientas no sigue a la selección (estabilizar {stabilize}, subtítulos {subtitles}).");
        }

        await CaptureAsync(window, "herramientas");
        await LongActionAsync(window);
    }

    /// <summary>
    /// «Color automático» desde la barra: al instante el botón queda ocupado y la barra de avisos muestra el avance con
    /// «Cancelar»; un segundo clic no arranca otra; al terminar queda el aviso con «Deshacer». Lo deshace.
    /// </summary>
    private static async Task LongActionAsync(MainWindow window)
    {
        const string id = Views.Analysis.AnalysisCommands.AutoColor;
        EditorSession session = window.Session;
        int steps = session.History.Count;
        window.Router.Invoke(id);
        window.Router.Invoke(id);
        await Task.Delay(30);
        (bool busy, _) = window.QuickTools.Busy(id);
        var bar = window.NotificationState;
        if (!busy || !bar.Open || !bar.Progress || bar.Button != "Cancelar" || window.LongActions.Running.Count != 1)
        {
            throw new InvalidOperationException($"Color automático no se ve en marcha (botón {busy}, barra {bar}, en marcha {window.LongActions.Running.Count}).");
        }

        await CaptureAsync(window, "accion-en-marcha");
        for (int i = 0; i < 150 && window.LongActions.IsRunning(id); i++)
        {
            await Task.Delay(100);
        }

        await Task.Delay(100);
        bar = window.NotificationState;
        if (window.LongActions.IsRunning(id) || window.QuickTools.Busy(id).Busy || bar.Progress || bar.Button != "Deshacer")
        {
            throw new InvalidOperationException($"Color automático no terminó con su aviso y «Deshacer» (barra {bar}).");
        }

        await CaptureAsync(window, "accion-terminada");
        while (session.History.Count > steps)
        {
            session.Undo();
        }
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
        string[] cards = [ExportPresets.Vertical.Id, ExportPresets.YouTube4K.Id, ExportPresets.AudioOnly.Id, ExportPresets.YouTube1080p.Id];
        // La sonda de codificadores puede tardar (la primera vez, o con la GPU ocupada): se esperan las tarjetas, no un plazo fijo.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (window.ExportPage is not { Model: not null } ready || !ready.HasPresetCards(cards))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(90))
            {
                throw new InvalidOperationException("Exportar: las tarjetas de preajustes no llegaron en 90 s (la sonda de codificadores sigue «Comprobando…»).");
            }

            await Task.Delay(100);
        }

        await CaptureAsync(window, "exportar-sencillo");
        if (window.ExportPage is { } page && await page.CheckSwitchingAsync(cards) is { } failure)
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
        ExportSettings settings = ExportPresets.YouTube1080p.Settings;
        if (window.ExportPage?.Model is { } model && ExportSettingsEditor.Validate(settings, model.Encoders).Count > 0)
        {
            // Sin GPU ni Media Foundation (Windows N, Windows Server) no hay H.264: AV1, la salida que ofrece D-028.
            settings = settings with { Video = ExportSettingsEditor.DefaultVideo(ExportCodec.Av1, model.Encoders) };
            Program.Metrics.Note("autoprueba: sin codificador de H.264, se exporta en AV1");
        }

        ExportEntry entry = window.Exports.Enqueue(new ExportRequest(window.Session.Project.Clone(), window.Session.SequenceId, output,
            settings, new ExportRange(Time.Zero, Time.Min(window.Session.Sequence.Duration, Time.FromSeconds(2)))));
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
    /// Abre Complementos y espera la consulta del catálogo publicado: sin conexión la página sigue y solo lo dice en la nota.
    /// </summary>
    private static async Task PluginsAsync(MainWindow window)
    {
        window.ShowPage(ShellPage.Plugins);
        Task check = window.PluginsPage?.UpdatesChecked ?? Task.CompletedTask;
        if (await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(60))) != check)
        {
            throw new InvalidOperationException("Complementos no terminó de consultar el catálogo en 60 s.");
        }

        await check;
        await CaptureAsync(window, "complementos");
        Program.Metrics.Note($"autoprueba: complementos, {window.PluginsPage?.DownloadNoteShown}");
        window.ClosePage();
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

    /// <summary>El MP4 de la autoprueba: video H.264 (AV1 si el equipo no tiene H.264) de la duración pedida con <c>moov</c> al principio y audio AAC.</summary>
    private static void CheckExported(string output, ExportRequest request)
    {
        try
        {
            ExportReport report = ExportVerifier.For(FfmpegExecutable.Locate()).Inspect(output);
            double expected = request.Range!.Value.Duration.ToSeconds();
            string codec = request.Settings.Video?.Codec == ExportCodec.Av1 ? "av1" : "h264";
            if (report.Video is not { } video || video.Codec != codec || Math.Abs(video.DurationSeconds - expected) > 0.05 || report.MoovAtStart != true
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

    /// <summary>Con capturas: la página de complementos y la ventana a 1280×720 (lo mínimo que se cuida), y la deja como estaba.</summary>
    private static async Task LayoutAsync(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable(CapturesVariable) is not { Length: > 0 })
        {
            return;
        }

        window.ShowPage(ShellPage.Plugins);
        await CaptureAsync(window, "complementos");
        window.ClosePage();
        var presenter = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        bool maximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        presenter?.Restore();
        Windows.Graphics.SizeInt32 before = window.AppWindow.Size;
        double scale = window.Content.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * scale), (int)(720 * scale)));
        await Task.Delay(500);
        await CaptureAsync(window, "ventana-1280x720");
        window.AppWindow.Resize(before);
        if (maximized)
        {
            presenter!.Maximize();
        }

        await Task.Delay(200);
    }

    /// <summary>
    /// Preferencias: la pestaña Personalización con el tema Claro en vista previa (los pinceles se recoloran en caliente) y
    /// la de Temas; al cerrar vuelve al tema de los ajustes.
    /// </summary>
    private static async Task PreferencesAsync(MainWindow window)
    {
        var view = new PreferencesView();
        view.Attach(window.Settings, window.Router.Registry, new Services.Settings.WorkspaceProfile());
        var dialog = new ContentDialog { XamlRoot = window.XamlRoot, Title = "Preferencias", Content = view, PrimaryButtonText = "Guardar", CloseButtonText = "Cancelar" };
        var showing = dialog.ShowAsync();
        await Task.Delay(400);
        view.ShowForSelfTest(3, Services.Settings.Themes.LightName);
        await Task.Delay(400);
        if (Theme.ThemeResources.Current.Name != Services.Settings.Themes.LightName)
        {
            throw new InvalidOperationException("La vista previa del tema Claro no se aplicó.");
        }

        if (((FrameworkElement)window.Content).ActualTheme != ElementTheme.Light || dialog.ActualTheme != ElementTheme.Light)
        {
            throw new InvalidOperationException("Con el tema Claro, la ventana o el diálogo siguen con los controles de WinUI en oscuro.");
        }

        await CaptureElementAsync(view, "preferencias-personalizacion");
        await CaptureAsync(window, "tema-claro");
        view.ShowForSelfTest(4, null);
        await Task.Delay(400);
        await CaptureElementAsync(view, "preferencias-temas");
        view.Discard();
        dialog.Hide();
        await showing;
        if (!string.Equals(Theme.ThemeResources.Current.Name, window.Settings.Theme, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cancelar Preferencias no devolvió el tema de los ajustes.");
        }

        if (((FrameworkElement)window.Content).ActualTheme != Theme.ThemeResources.RequestedTheme)
        {
            throw new InvalidOperationException("Cancelar Preferencias no devolvió el tema de WinUI de los ajustes.");
        }
    }

    /// <summary>
    /// Con <c>RODAJE_AUTOPRUEBA_CAPTURAS=carpeta</c> guarda allí la ventana en PNG (dibujada por XAML, no la pantalla), como
    /// evidencia de cómo se ve con el exe de AOT. Sin la variable no hace nada.
    /// </summary>
    private static async Task CaptureAsync(MainWindow window, string name)
    {
        if (window.Content is UIElement root)
        {
            await CaptureElementAsync(root, name);
        }
    }

    private static async Task CaptureElementAsync(UIElement root, string name)
    {
        if (Environment.GetEnvironmentVariable(CapturesVariable) is not { Length: > 0 } folder)
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
