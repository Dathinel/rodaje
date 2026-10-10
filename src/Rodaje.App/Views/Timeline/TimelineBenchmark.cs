using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Media;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Medida de la línea de tiempo (<c>RODAJE_MEDIR_LINEA=informe.txt</c>): arma con <see cref="ProjectFactory"/> un
/// proyecto de 200 clips con el primer video abierto (o uno sintético), mide los cuadros al desplazarse y al hacer zoom, y
/// cuánto tarda el reproductor en ver una edición hecha arrastrando. Escribe el informe y cierra.
/// </summary>
internal static class TimelineBenchmark
{
    private const int Frames = 300;

    public static string? RequestedReportPath =>
        EnvironmentVariables.Read(EnvironmentVariables.MedirLinea) is { } path ? Path.GetFullPath(path) : null;

    public static async Task RunAsync(TimelineView view, string reportPath)
    {
        var report = new StringBuilder();
        CultureInfo c = CultureInfo.InvariantCulture;
        try
        {
            EditorSession session = view.Session!;
            var waited = Stopwatch.StartNew();
            while (!session.Project.Media.Exists(m => m.HasVideo) && waited.Elapsed < TimeSpan.FromSeconds(8))
            {
                await Task.Delay(100);
            }

            MediaRef? source = session.Project.Media.Find(m => m.HasVideo);
            string path = Path.Combine(Path.GetTempPath(), "rodaje-medida-200.rodaje");
            Project project = Build(source);
            ProjectSerializer.Save(project, path);
            session.History.MarkClean();
            if (!await session.OpenProjectAsync(path))
            {
                throw new InvalidOperationException("No se pudo abrir el proyecto de medida.");
            }

            int clips = session.Sequence.AllTracks.Sum(t => t.Clips.Count);
            report.AppendLine(c, $"proyecto: {clips} clips, {session.Sequence.Markers.Count} marcadores, {session.Sequence.Regions.Count} regiones, {session.Sequence.Duration.ToSeconds():0.0} s; medio: {(source is null ? "sintético" : Path.GetFileName(source.Path))}");
            await Task.Delay(1500);

            TimelineRenderer renderer = view.Renderer!;
            int realizedBefore = renderer.Realizations;
            double maxScroll = Math.Max(1, renderer.Viewport.ContentWidth(session.Sequence.Duration) - renderer.Viewport.Width);
            (List<double> scrollFrames, List<double> scrollRender) = await Animate(view, i =>
            {
                double x = (i * 30.0) % maxScroll;
                view.ScrollTo(x);
            });
            report.AppendLine(c, $"desplazamiento (60 px/s de zoom, 30 px por cuadro, {Frames} cuadros): {Summary(scrollFrames)}; dibujo {Summary(scrollRender)}; ventanas armadas {renderer.Realizations - realizedBefore}");

            foreach (bool preview in (bool[])[false, true])
            {
                view.DisableZoomPreview = !preview;
                view.ScrollTo(0, 5, 0);
                await Task.Delay(300);
                realizedBefore = renderer.Realizations;
                (List<double> zoomFrames, List<double> zoomRender) = await Animate(view, i =>
                {
                    double pps = 5 * Math.Pow(1.03, i % 150 < 75 ? i % 150 : 150 - (i % 150));
                    view.ScrollTo(0, pps, renderer.Viewport.Width / 2);
                });
                string how = preview ? "como en la app: estira y rearma al pasar de x1,6 o al soltar" : "rearmando en cada cuadro";
                report.AppendLine(c, $"zoom {how} (de 5 a 45 px/s y vuelta, alrededor del centro, {Frames} cuadros; a 5 px/s se ven los 200 clips): {Summary(zoomFrames)}; dibujo {Summary(zoomRender)}; ventanas armadas {renderer.Realizations - realizedBefore}");
            }

            view.DisableZoomPreview = false;

            view.ScrollTo(0, 60, 0);
            await Task.Delay(500);
            report.AppendLine(DragToPlayback(view, session));
        }
#pragma warning disable CA1031 // La medida escribe el error en su informe en vez de tumbar la aplicación.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            report.AppendLine("error: " + ex);
        }

        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
        Application.Current.Exit();
    }

    /// <summary>200 clips: 80 en la principal con su audio enlazado, 40 encima, marcadores, regiones y algunos keyframes.</summary>
    private static Project Build(MediaRef? source)
    {
        var ids = new SequentialIdGenerator();
        MediaRef media = source?.Clone() ?? new MediaRef
        {
            Id = "media-sintetico",
            Path = Path.Combine(Path.GetTempPath(), "rodaje-sintetico.mp4"),
            Name = "sintético",
            Duration = Time.FromSeconds(60),
            HasVideo = true,
            HasAudio = true,
            FrameRate = Rational.Fps30,
            Width = 1920,
            Height = 1080,
        };
        Project project = ProjectFactory.CreateDefault("Medida 200 clips", ids, media.FrameRate ?? Rational.Fps30);
        project.Media.Add(media);
        Sequence sequence = project.Sequences[0];
        var ctx = new EditContext(project, sequence, ids);
        string main = sequence.VideoTracks[0].Id;
        string overlay = sequence.VideoTracks[1].Id;
        string audio = sequence.AudioTracks[0].Id;
        Time available = media.Duration;
        for (int i = 0; i < 80; i++)
        {
            Time length = Time.Min(available, Time.FromSeconds(1 + (i % 5 * 0.5)));
            Time sourceIn = Time.Min(available - length, Time.FromSeconds(i % 7 * 0.25)).SnapToFrame(sequence.FrameRate);
            TimelineEdits.AppendMedia(ctx, media.Id, sourceIn, sourceIn + length, main, media.HasAudio ? audio : null);
        }

        for (int i = 0; i < (media.HasAudio ? 40 : 120); i++)
        {
            Time length = Time.Min(available, Time.FromSeconds(1));
            TimelineEdits.InsertMedia(ctx, media.Id, Time.Zero, length, overlay, Time.FromSeconds(i * 3.5), PlacementMode.Overwrite);
        }

        foreach (Clip clip in sequence.VideoTracks[0].Clips.Where((_, i) => i % 9 == 0))
        {
            clip.Effects.Add(new EffectInstance
            {
                Id = ids.NewId("fx"),
                Type = BuiltInEffects.Transform,
                Parameters = new()
                {
                    [BuiltInEffects.TransformParameters.Opacity] = new Parameter
                    {
                        Value = 1,
                        Keyframes = [new Keyframe(clip.SourceIn, 0), new Keyframe(clip.SourceIn + Time.FromSeconds(0.5), 1)],
                    },
                },
            });
        }

        for (int i = 0; i < 10; i++)
        {
            TimelineEdits.AddMarker(ctx, Time.FromSeconds(i * 15 + 2), $"Marcador {i + 1}");
        }

        TimelineEdits.AddRegion(ctx, Time.FromSeconds(10), Time.FromSeconds(30), "Intro");
        TimelineEdits.AddRegion(ctx, Time.FromSeconds(60), Time.FromSeconds(75), "Medio");
        TimelineEdits.AddRegion(ctx, Time.FromSeconds(120), Time.FromSeconds(140), "Final");
        return project;
    }

    /// <summary>Un paso por cuadro de XAML: aplica el cambio, dibuja y anota el intervalo entre cuadros y lo que tardó el dibujo.</summary>
    private static async Task<(List<double> Frames, List<double> Render)> Animate(TimelineView view, Action<int> step)
    {
        var frames = new List<double>(Frames);
        var render = new List<double>(Frames);
        var done = new TaskCompletionSource();
        long last = 0;
        int i = 0;
        void OnRendering(object? sender, object e)
        {
            long now = Stopwatch.GetTimestamp();
            if (last != 0)
            {
                frames.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            }

            last = now;
            if (i == Frames)
            {
                CompositionTarget.Rendering -= OnRendering;
                done.TrySetResult();
                return;
            }

            step(i++);
            long start = Stopwatch.GetTimestamp();
            view.RenderNow();
            render.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        CompositionTarget.Rendering += OnRendering;
        await done.Task;
        return (frames, render);
    }

    /// <summary>
    /// Recorta con ripple el final de un clip de la principal arrastrando su borde y mide hasta que el motor publica la
    /// duración nueva de la secuencia (la edición ya está en la copia que reproduce).
    /// </summary>
    private static string DragToPlayback(TimelineView view, EditorSession session)
    {
        var applied = new List<double>();
        var reflected = new List<double>();
        Views.Player.PlayerEngine engine = view.Services!.Player;
        for (int i = 0; i < 12; i++)
        {
            Track main = session.MainTrack!;
            Clip clip = main.Clips[main.Clips.Count / 2];
            Time delta = Time.FromSeconds(i % 2 == 0 ? -0.5 : 0.5);
            var options = new DragOptions { MagneticTrackId = main.Id, RippleScope = RippleScope.Track, PixelsPerSecond = 60 };
            var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, clip.Id, DragOperation.TrimEnd, clip.End, options, session.Position));
            drag.Begin();
            long start = Stopwatch.GetTimestamp();
            drag.Move(clip.End + delta, null);
            applied.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Time expected = session.Sequence.Duration;
            while (engine.Snapshot.Duration != expected && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(2))
            {
                Thread.SpinWait(200);
            }

            reflected.Add(engine.Snapshot.Duration == expected ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : double.NaN);
            drag.Complete();
            view.RenderNow();
            Thread.Sleep(50);
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"arrastre a reproducción (recorte ripple de un clip de la principal, 12 veces): paso del gesto en la sesión {Summary(applied)}; hasta que el motor publica la secuencia editada {Summary(reflected)}");
    }

    private static string Summary(List<double> values)
    {
        double[] valid = [.. values.Where(double.IsFinite).Order()];
        if (valid.Length == 0)
        {
            return "sin muestras";
        }

        double P(double q) => valid[Math.Min(valid.Length - 1, (int)Math.Ceiling(valid.Length * q) - 1)];
        int over = valid.Count(v => v > 17.5);
        return string.Create(CultureInfo.InvariantCulture,
            $"n={valid.Length} mediana={P(0.5):0.00} ms p95={P(0.95):0.00} p99={P(0.99):0.00} max={valid[^1]:0.00} (>{17.5} ms: {over}){(values.Count > valid.Length ? $", sin respuesta {values.Count - valid.Length}" : "")}");
    }
}
