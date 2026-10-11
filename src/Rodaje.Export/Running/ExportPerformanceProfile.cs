using System.Diagnostics;
using Rodaje.Media.Export;

namespace Rodaje.Export.Running;

/// <summary>
/// Cómo se reparte el equipo en cada modo de rendimiento (D-028): prioridad del hilo y de <c>ffmpeg.exe</c>, lectura por
/// adelantado y decodificadores del motor fuera de línea, y qué cede la vista previa.
/// </summary>
/// <param name="PausePreview">Alto rendimiento: la vista previa se pausa al empezar (se puede volver a reproducir).</param>
/// <param name="PreviewProxies">Con menos de <see cref="LowVideoMemoryBytes"/> de VRAM, la vista previa pasa a proxies y Mitad mientras exporta.</param>
public sealed record ExportPerformanceProfile(
    ThreadPriority ThreadPriority,
    ProcessPriorityClass EncoderPriority,
    int LookaheadFrames,
    int MaxDecodersPerMedia,
    bool PausePreview,
    bool PreviewProxies)
{
    public const long LowVideoMemoryBytes = 4L * 1024 * 1024 * 1024;

    /// <param name="dedicatedVideoMemory">VRAM dedicada de la GPU; 0 si no se sabe (no cambia la vista previa).</param>
    public static ExportPerformanceProfile For(ExportPerformance mode, long dedicatedVideoMemory)
    {
        bool lowVram = dedicatedVideoMemory is > 0 and < LowVideoMemoryBytes;
        return mode == ExportPerformance.Fastest
            ? new(ThreadPriority.Normal, FfmpegPipeSinkOptions.PriorityFor(fastest: true), 8, 4, PausePreview: true, lowVram)
            : new(ThreadPriority.BelowNormal, FfmpegPipeSinkOptions.PriorityFor(fastest: false), 3, 2, PausePreview: false, lowVram);
    }
}

/// <summary>Lo que la vista previa de la app cede mientras se exporta. La app lo implementa; sin ella no cambia nada.</summary>
public interface IExportPreview
{
    /// <summary>Cede la vista previa según el perfil; al desechar lo devuelto, vuelve a como estaba (salvo la pausa).</summary>
    /// <remarks>Se llama desde el hilo de la exportación.</remarks>
    IDisposable Yield(ExportPerformanceProfile profile);
}
