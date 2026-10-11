using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Offline;

/// <summary>Qué hacer con lo que la vista previa tolera y la exportación no (D-028).</summary>
public enum MissingContentPolicy
{
    /// <summary>Falla con <see cref="ExportException"/> (<see cref="ExportErrorCode.ContentMissing"/>). Por defecto.</summary>
    Fail,

    /// <summary>El usuario eligió «continuar sin»: la LUT o la transición que falta se omite, como en la vista previa.</summary>
    ContinueWithout,
}

/// <summary>Opciones de <see cref="OfflineTimeline"/>.</summary>
public sealed record OfflineTimelineOptions
{
    /// <summary>Carpeta contra la que se resuelven las rutas relativas de los medios.</summary>
    public string? ProjectDirectory { get; init; }

    public IEffectContent? Content { get; init; }

    public bool AllowHardwareDecoding { get; init; } = true;

    /// <summary>Presupuesto de VRAM propio de la exportación; <c>null</c>, según la del dispositivo.</summary>
    public long? VramBudgetBytes { get; init; }

    public int MaxDecodersPerMedia { get; init; } = 4;

    /// <summary>Cuánto puede tardar un cuadro en estar exacto antes de dar <see cref="ExportErrorCode.DecodeFailed"/>.</summary>
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public MissingContentPolicy MissingContent { get; init; } = MissingContentPolicy.Fail;
}

/// <summary>
/// La secuencia cuadro a cuadro, exacta y sin reloj, para exportar: <see cref="Render"/> espera a que todas las capas
/// tengan su cuadro exacto (sin proxies, sin keyframe cercano, sin <c>SkipNonReference</c>).
/// </summary>
/// <remarks>
/// <para>Recibe una copia del proyecto (<see cref="Project.Clone"/>): la edición puede seguir mientras exporta.</para>
/// <para>
/// Se usa desde un solo hilo, el de la exportación. Los cuadros se piden en orden creciente; un salto hacia atrás es
/// válido pero cuesta un seek. La composición que devuelve es de quien la recibe y la desecha tras componerla.
/// </para>
/// <para>Contrato del paso 0 de la fase 6; el motor lo implementa el agente A.</para>
/// </remarks>
public sealed class OfflineTimeline : IDisposable
{
    private readonly TimelinePlan _plan;
    private readonly GpuDevice? _device;
    private readonly OfflineTimelineOptions _options;
    private bool _disposed;

    internal OfflineTimeline(Project project, string sequenceId, GpuDevice? device, OfflineTimelineOptions options, IClipBackend backend)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        _options = options;
        _device = device;
        _plan = TimelinePlan.Build(project, sequenceId, options.ProjectDirectory ?? Environment.CurrentDirectory, options.Content);
        Backend = backend;
    }

    /// <summary>Abre la secuencia para exportar.</summary>
    /// <param name="device">El dispositivo propio de la exportación (no el de la vista previa); sin él se decodifica en la CPU.</param>
    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static OfflineTimeline Open(Project project, string sequenceId, GpuDevice? device, OfflineTimelineOptions? options = null) =>
        new(project, sequenceId, device, options ?? new OfflineTimelineOptions(), MediaClipBackend.Instance);

    public string SequenceId => _plan.SequenceId;

    public int Width => _plan.Width;

    public int Height => _plan.Height;

    public Rational FrameRate => _plan.FrameRate;

    public Time Duration => _plan.Duration;

    public long FrameCount => _plan.FrameCount;

    /// <summary>LUT y transiciones de paquete que la secuencia usa y no están; con <see cref="MissingContentPolicy.Fail"/> impiden exportar.</summary>
    public int MissingContent => _plan.MissingContent;

    public GpuDevice? Device => _device;

    public OfflineTimelineOptions Options => _options;

    internal IClipBackend Backend { get; }

    /// <summary>Las capas exactas del cuadro <paramref name="frame"/> de la secuencia, de abajo arriba.</summary>
    /// <exception cref="ArgumentOutOfRangeException">El cuadro está fuera de la secuencia.</exception>
    /// <exception cref="ExportException">Un medio falta, un cuadro no se decodifica o falta contenido (según la política).</exception>
    /// <exception cref="OperationCanceledException">Se canceló mientras esperaba a un decodificador.</exception>
    public CompositionFrame Render(long frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frame, FrameCount);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("OfflineTimeline.Render es del agente A de la fase 6.");
    }

    /// <summary>
    /// Las capas exactas en el instante <paramref name="time"/> de la secuencia, para la conversión de tasa: la salida a
    /// otra tasa pide el instante de cada cuadro suyo (repetición o descarte) o dos instantes para mezclarlos.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">El instante está fuera de <c>[0, Duration)</c>.</exception>
    public CompositionFrame RenderAt(Time time, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (time < Time.Zero || time >= Duration)
        {
            throw new ArgumentOutOfRangeException(nameof(time), time, "El instante está fuera de la secuencia.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("OfflineTimeline.RenderAt es del agente A de la fase 6.");
    }

    public void Dispose() => _disposed = true;
}
