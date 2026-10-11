using Rodaje.Core.Model;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;

namespace Rodaje.Export.Running;

/// <summary>Lo que la orquestación necesita del entorno.</summary>
public sealed record ExportRunnerOptions
{
    public required FfmpegExecutable Ffmpeg { get; init; }

    /// <summary>LUT y transiciones de paquetes (el mismo contenido que la vista previa).</summary>
    public IEffectContent? Content { get; init; }

    /// <summary>Registro de temporales para limpiarlos tras un apagón; <c>null</c> no registra.</summary>
    public ExportJournal? Journal { get; init; }

    public IExportPreview? Preview { get; init; }

    /// <summary>Carpeta del WAV de la mezcla y los registros de dos pasadas; por omisión, la temporal del sistema.</summary>
    public string? TemporaryDirectory { get; init; }

    /// <summary>Comprueba el archivo con ffprobe al terminar (cuadros, tasa, color, audio, <c>moov</c>).</summary>
    public bool Verify { get; init; } = true;

    /// <summary>El dispositivo propio de la exportación; por omisión, uno nuevo en la GPU por defecto.</summary>
    public Func<GpuDevice>? CreateDevice { get; init; }
}

/// <summary>
/// La orquestación de la exportación (D-028): una copia del proyecto → <c>OfflineAudio</c> al WAV temporal (con la
/// sonoridad medida antes si se normaliza) → por cada cuadro <c>OfflineTimeline</c> → <c>Compositor.RenderStrict</c> →
/// <c>ExportFrameConverter</c> → <c>FfmpegPipeSink</c>, en un hilo propio con la prioridad del modo de rendimiento, y
/// <c>ExportVerifier</c> al final.
/// </summary>
public sealed class ExportRunner
{
    public ExportRunner(ExportRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    public ExportRunnerOptions Options { get; }

    /// <summary>Al arrancar la app: borra los temporales de exportaciones de procesos muertos (apagón, cierre forzado).</summary>
    /// <returns>Rutas borradas.</returns>
    public static IReadOnlyList<string> CleanupStale(ExportJournal? journal = null)
    {
        try
        {
            return (journal ?? ExportJournal.Default).CleanupStale();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Arranca la exportación en su hilo y vuelve enseguida.</summary>
    /// <param name="request">Con una copia del proyecto (<see cref="Project.Clone"/>).</param>
    /// <param name="encoders">Los que pasaron la sonda, en orden de preferencia.</param>
    public ExportJob Start(ExportRequest request, IReadOnlyList<EncoderSpec> encoders)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(encoders);
        var job = new ExportJob(request, encoders, Options);
        job.Launch();
        return job;
    }
}
