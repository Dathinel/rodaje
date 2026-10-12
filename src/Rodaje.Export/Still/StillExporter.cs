using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Export;
using Rodaje.Render.Text;

namespace Rodaje.Export.Still;

/// <summary>
/// Exporta el cuadro del cursor como imagen por el mismo camino que la exportación de video (D-028):
/// <c>OfflineTimeline.Render</c> (exacto, sin proxies ni reloj) → <c>Compositor.RenderStrict</c> con los títulos →
/// <c>ExportFrameConverter</c> a RGB(A) en la GPU → <see cref="StillEncoder"/> (<c>ffmpeg.exe</c>, PNG o JPEG).
/// </summary>
/// <remarks>
/// Usa un <see cref="GpuDevice"/> propio, como la exportación, para no tocar el del reproductor; la vista previa no
/// cede (un cuadro no la frena). La política de contenido que falta es la misma: sin
/// <see cref="StillRequest.ContinueWithoutMissing"/>, una LUT, transición, efecto o fuente que falta para con
/// <see cref="ExportErrorCode.ContentMissing"/> o <see cref="ExportErrorCode.EffectFailed"/>.
/// </remarks>
public sealed class StillExporter
{
    private static readonly Func<LayerFrame, SourceGeometry> Geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);

    /// <param name="options">Las mismas de la exportación: <c>ffmpeg.exe</c>, contenido de paquetes, fuentes, registro y dispositivo.</param>
    public StillExporter(ExportRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    public ExportRunnerOptions Options { get; }

    /// <summary>Exporta en un hilo propio y vuelve enseguida.</summary>
    /// <exception cref="ExportException">Con su código (también la cancelación, <see cref="ExportErrorCode.Canceled"/>).</exception>
    public Task<StillResult> ExportAsync(StillRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var done = new TaskCompletionSource<StillResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(Export(request, cancellationToken));
            }
#pragma warning disable CA1031 // Todo error del hilo llega a quien espera, con su código.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Rodaje: exportar fotograma",
        };
        thread.Start();
        return done.Task;
    }

    /// <summary>Exporta en este hilo.</summary>
    /// <exception cref="ExportException">Con su código (también la cancelación, <see cref="ExportErrorCode.Canceled"/>).</exception>
    public StillResult Export(StillRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        long? frame = null;
        try
        {
            StillPlan plan = StillPlan.Create(request);
            // Antes de la GPU: el destino se comprueba (no pisa un medio, se puede escribir, no está abierto) al crearlo.
            using var encoder = new StillEncoder(Options.Ffmpeg, plan, request.OutputPath, SourcePaths(request), Options.Journal);
            using GpuDevice gpu = CreateDevice();
            using TitleRasterizer titles = CreateTitles(gpu);
            var options = new OfflineTimelineOptions
            {
                Titles = titles,
                ProjectDirectory = request.ProjectDirectory,
                Content = Options.Content,
                MissingContent = request.ContinueWithoutMissing ? MissingContentPolicy.ContinueWithout : MissingContentPolicy.Fail,
                LookaheadFrames = 1,
                MaxDecodersPerMedia = 1,
            };
            using OfflineTimeline timeline = OfflineTimeline.Open(request.Project, request.SequenceId, gpu, options);
            if (timeline.FrameCount <= 0)
            {
                throw new ExportException(ExportErrorCode.InvalidSettings, "La secuencia está vacía: no hay ningún cuadro que exportar.");
            }

            frame = Math.Min(plan.Frame, timeline.FrameCount - 1);
            Time start = Time.FromFrames(frame.Value, timeline.FrameRate);
            Time end = Time.FromFrames(frame.Value + 1, timeline.FrameRate);
            timeline.Prepare(start, end, cancellationToken);
            CheckFonts(request, titles, timeline.GeneratedContents(start, end));

            using var compositor = new Compositor(gpu, timeline.Width, timeline.Height)
            {
                Resources = Options.Content,
                Titles = titles,
                TransparentBackground = plan.HasAlpha,
            };
            Compose(request, compositor, timeline.Render(frame.Value, cancellationToken));

            StillSettings settings = plan.Settings;
            ExportSourceRegion? region = settings.Crop is { } crop ? new ExportSourceRegion(crop.X, crop.Y, crop.Width, crop.Height) : null;
            using var converter = new ExportFrameConverter(gpu, timeline.Width, timeline.Height, plan.Output, settings.Fit, settings.ScaleFilter,
                ringSize: 2, region: region);
            converter.Submit(compositor.Texture, 0);
            long bytes = 0;
            void Write(in ExportVideoFrame image) => bytes = encoder.Write(in image, cancellationToken);
            if (!converter.TryRead(Write))
            {
                throw new ExportException(ExportErrorCode.GpuFailed, "La GPU no devolvió el cuadro convertido.");
            }

            return new StillResult(Path.GetFullPath(request.OutputPath), bytes, frame.Value, plan.Output.Width, plan.Output.Height);
        }
        catch (Exception ex) when (ex is not ExportException)
        {
            throw ExportErrors.Map(ex, frame);
        }
    }

    private GpuDevice CreateDevice()
    {
        try
        {
            return Options.CreateDevice?.Invoke() ?? GpuDevice.Create();
        }
        catch (Exception ex) when (ex is not ExportException)
        {
            throw new ExportException(ExportErrorCode.GpuFailed, $"No se pudo abrir la GPU para exportar: {ex.Message}", ex);
        }
    }

    private TitleRasterizer CreateTitles(GpuDevice gpu)
    {
        var titles = new TitleRasterizer(gpu);
        try
        {
            if (Options.FontFiles?.Invoke() is { Count: > 0 } fonts)
            {
                titles.RegisterFonts(fonts);
            }
        }
        catch
        {
            titles.Dispose();
            throw;
        }

        return titles;
    }

    // Como en ExportJob: sin permiso para seguir, una fuente que falta para antes de componer.
    private static void CheckFonts(StillRequest request, TitleRasterizer titles, IReadOnlyList<GeneratedContent> contents)
    {
        if (contents.Count == 0 || request.ContinueWithoutMissing)
        {
            return;
        }

        IReadOnlyList<MissingFont> missing = titles.FindMissingFonts(contents);
        if (missing.Count > 0)
        {
            string families = string.Join(", ", missing.Select(m => $"«{m.Family}»").Distinct(StringComparer.Ordinal));
            throw new ExportException(ExportErrorCode.ContentMissing,
                $"Falta{(missing.Count > 1 ? "n" : "")} fuente{(missing.Count > 1 ? "s" : "")} de títulos: {families}. Instala su paquete o exporta con la fuente de respaldo ({missing[0].UsedFamily}).")
            {
                Culprit = $"fuente «{missing[0].Family}»",
            };
        }
    }

    // Como en ExportJob: el compositor estricto, y el permisivo solo si se aceptó exportar sin lo que falta.
    private static void Compose(StillRequest request, Compositor compositor, CompositionFrame frame)
    {
        using (frame)
        {
            CompositeResult result;
            try
            {
                result = compositor.RenderStrict(frame, Geometry);
            }
            catch (ExportException ex) when (ex.Code == ExportErrorCode.EffectFailed && request.ContinueWithoutMissing)
            {
                result = compositor.Render(frame, Geometry);
            }

            if (result.MissingContent > 0 && !request.ContinueWithoutMissing)
            {
                throw new ExportException(ExportErrorCode.ContentMissing,
                    $"Falta contenido de paquetes en {result.MissingContent} capa(s): instala los paquetes o exporta sin él.");
            }
        }
    }

    private static List<string> SourcePaths(StillRequest request)
    {
        string folder = request.ProjectDirectory ?? Environment.CurrentDirectory;
        return [.. request.Project.Media.Where(m => !string.IsNullOrEmpty(m.Path)).Select(m => Path.GetFullPath(m.Path, folder))];
    }
}
