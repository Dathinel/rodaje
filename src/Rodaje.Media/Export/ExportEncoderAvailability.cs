using System.Collections.Immutable;

namespace Rodaje.Media.Export;

/// <summary>Un codificador del catálogo y si funciona en esta máquina.</summary>
public sealed record ExportEncoderStatus(ExportEncoderInfo Encoder, bool Works, string? Reason);

/// <summary>Resultado de probar todo el catálogo.</summary>
public sealed class ExportEncoderReport
{
    internal ExportEncoderReport(ImmutableArray<ExportEncoderStatus> all) => All = all;

    /// <summary>Todos los codificadores del catálogo, en su orden, con su estado.</summary>
    public ImmutableArray<ExportEncoderStatus> All { get; }

    /// <summary>Solo los que funcionan, en el orden del catálogo.</summary>
    public ImmutableArray<ExportEncoderInfo> Available => [.. All.Where(s => s.Works).Select(s => s.Encoder)];

    public ExportEncoderChoice? ChooseDefault() => ExportEncoderChooser.ChooseDefault(Available);

    public ExportEncoderChoice? Choose(ExportCodec requested) => ExportEncoderChooser.Choose(Available, requested);
}

/// <summary>
/// Prueba el catálogo una sola vez por instancia, con varias sondas a la vez, y guarda el resultado.
/// </summary>
/// <remarks>
/// Las sondas reales tardan segundos (lanzan procesos y abren la GPU), así que se cachea: todos los que piden
/// <see cref="GetAsync"/> mientras corre comparten la misma tarea y los siguientes reciben el resultado guardado.
/// <see cref="Invalidate"/> obliga a probar de nuevo (por ejemplo tras cambiar el controlador de la GPU).
/// </remarks>
public sealed class ExportEncoderAvailability
{
    private readonly IExportEncoderProbe _probe;
    private readonly ImmutableArray<ExportEncoderInfo> _catalog;
    private readonly int _maxParallelism;
    private readonly object _gate = new();
    private Task<ExportEncoderReport>? _cached;

    public ExportEncoderAvailability(IExportEncoderProbe probe, int maxParallelism = 4, IEnumerable<ExportEncoderInfo>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);
        _probe = probe;
        _maxParallelism = maxParallelism;
        _catalog = catalog is null ? ExportEncoderCatalog.All : [.. catalog];
    }

    /// <summary>
    /// El informe, probando si hace falta. Cancelar <paramref name="cancellationToken"/> deja de esperar, pero la
    /// prueba compartida sigue para los demás.
    /// </summary>
    public Task<ExportEncoderReport> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<ExportEncoderReport> task;
        lock (_gate)
        {
            task = _cached ??= Task.Run(RunAsync, CancellationToken.None);
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Descarta el resultado guardado; la próxima petición vuelve a probar.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private async Task<ExportEncoderReport> RunAsync()
    {
        using var limit = new SemaphoreSlim(_maxParallelism);
        Task<ExportEncoderStatus>[] tasks = [.. _catalog.Select(async encoder =>
        {
            await limit.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => ProbeOne(encoder)).ConfigureAwait(false);
            }
            finally
            {
                limit.Release();
            }
        })];
        ExportEncoderStatus[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ExportEncoderReport([.. results]);
    }

    // Una sonda que lanza no puede tumbar a las demás: cuenta como «no funciona» con el motivo.
    private ExportEncoderStatus ProbeOne(ExportEncoderInfo encoder)
    {
        try
        {
            ExportProbeResult result = _probe.Probe(encoder);
            return new ExportEncoderStatus(encoder, result.Works, result.Works ? null : result.Reason ?? "Sin motivo.");
        }
#pragma warning disable CA1031 // Cualquier fallo de una sonda debe quedar como «no disponible».
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ExportEncoderStatus(encoder, false, $"La prueba de {encoder.Id} falló: {ex.Message}");
        }
    }
}
