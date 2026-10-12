namespace Rodaje.Media.Export;

/// <summary>Un codificador del catálogo y qué dijo su prueba.</summary>
public sealed record ExportEncoderStatus(EncoderSpec Encoder, ExportProbeResult Result)
{
    public bool Works => Result.Works;

    /// <summary>Por qué no funciona, o <c>null</c> si funciona.</summary>
    public string? Reason => Result.Works ? null : Result.Reason;
}

/// <summary>Resultado de probar todo el catálogo.</summary>
public sealed class ExportEncoderReport
{
    internal ExportEncoderReport(IReadOnlyList<ExportEncoderStatus> all, DateTimeOffset probedAt)
    {
        All = all;
        ProbedAt = probedAt;
        Available = [.. all.Where(s => s.Works).Select(s => s.Encoder)];
    }

    /// <summary>Todos los codificadores del catálogo, en su orden, con su estado.</summary>
    public IReadOnlyList<ExportEncoderStatus> All { get; }

    /// <summary>Solo los que funcionan, en el orden del catálogo.</summary>
    public IReadOnlyList<EncoderSpec> Available { get; }

    /// <summary>Cuándo terminó la última prueba (la completa o el reintento de los fallos).</summary>
    public DateTimeOffset ProbedAt { get; }

    /// <summary>Si alguno falló al probarse (no cuentan «no compilado» ni «sin dispositivo»): esos se reintentan.</summary>
    public bool HasFailures => All.Any(s => s.Result.Status == ExportProbeStatus.Failed);

    public ExportEncoderStatus? Find(string id) => All.FirstOrDefault(s => string.Equals(s.Encoder.Id, id, StringComparison.Ordinal));

    public ExportEncoderChoice? ChooseDefault() => ExportEncoderChooser.ChooseDefault(Available);

    public ExportEncoderChoice? ChooseDefaultAudio() => ExportEncoderChooser.ChooseDefaultAudio(Available);

    public ExportEncoderChoice? Choose(ExportCodec requested) => ExportEncoderChooser.Choose(Available, requested);

    public ExportEncoderChoice? ChooseById(string id) => ExportEncoderChooser.ChooseById(Available, id);
}

/// <summary>
/// Prueba el catálogo con varias sondas a la vez y guarda el resultado; reintenta los que fallaron pasado un rato.
/// </summary>
/// <remarks>
/// <para>Las sondas reales tardan segundos (lanzan procesos y abren la GPU), así que se cachea: todos los que piden
/// <see cref="GetAsync"/> mientras corre comparten la misma tarea y los siguientes reciben el resultado guardado.</para>
/// <para>Un codificador que <see cref="ExportProbeStatus.Failed">falló</see> se vuelve a probar en la primera petición
/// que llegue pasado <c>failureRetryInterval</c> (la GPU pudo estar ocupada); los que funcionan, los que no están
/// compilados y los de hardware <see cref="ExportProbeStatus.NoDevice">sin dispositivo</see> no se repiten (abrir la GPU
/// que no hay cada 2 minutos no aporta nada).</para>
/// <para><see cref="Invalidate"/> obliga a probar todo de nuevo (por ejemplo tras cambiar el controlador de la GPU). Si
/// llega con una prueba en curso, quien ya esperaba recibe esa prueba; la nueva empieza cuando la anterior acaba, para
/// no abrir la GPU dos veces a la vez.</para>
/// </remarks>
public sealed class ExportEncoderAvailability
{
    private readonly IExportEncoderProbe _probe;
    private readonly IReadOnlyList<EncoderSpec> _catalog;
    private readonly int _maxParallelism;
    private readonly TimeProvider _time;
    private readonly TimeSpan _failureRetryInterval;
    private readonly object _gate = new();
    private Task<ExportEncoderReport>? _cached;

    // La última prueba lanzada, terminada o no: la siguiente la espera para no abrir la GPU dos veces a la vez.
    private Task _lastRun = Task.CompletedTask;

    /// <param name="probe">Sonda real o doble.</param>
    /// <param name="maxParallelism">Sondas a la vez (4 por defecto: no satura la GPU ni la CPU con libaom).</param>
    /// <param name="catalog">Codificadores a probar; <see cref="ExportEncoderCatalog.All"/> si es <c>null</c>.</param>
    /// <param name="time">Reloj; el del sistema si es <c>null</c>.</param>
    /// <param name="failureRetryInterval">Cuánto esperar antes de reintentar los fallos; 2 minutos si es <c>null</c>.</param>
    public ExportEncoderAvailability(IExportEncoderProbe probe, int maxParallelism = 4, IEnumerable<EncoderSpec>? catalog = null,
        TimeProvider? time = null, TimeSpan? failureRetryInterval = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);
        _probe = probe;
        _maxParallelism = maxParallelism;
        _catalog = catalog is null ? ExportEncoderCatalog.All : [.. catalog];
        _time = time ?? TimeProvider.System;
        _failureRetryInterval = failureRetryInterval ?? TimeSpan.FromMinutes(2);
        ArgumentOutOfRangeException.ThrowIfLessThan(_failureRetryInterval, TimeSpan.Zero, nameof(failureRetryInterval));
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
            if (_cached is null || _cached.IsFaulted || _cached.IsCanceled)
            {
                _cached = StartRun(previous: null);
            }
            else if (_cached.IsCompletedSuccessfully && _cached.Result is { HasFailures: true } report
                && _time.GetUtcNow() - report.ProbedAt >= _failureRetryInterval)
            {
                _cached = StartRun(previous: report);
            }

            task = _cached;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Descarta el resultado guardado (y lo que guarde la sonda); la próxima petición vuelve a probar todo.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }

        _probe.Reset();
    }

    // Con «previous», solo se vuelven a probar sus fallos y se conserva lo demás. Se llama con _gate tomado.
    private Task<ExportEncoderReport> StartRun(ExportEncoderReport? previous)
    {
        Task before = _lastRun;
        Task<ExportEncoderReport> run = Task.Run(async () =>
        {
            // Solo importa que termine, no cómo.
            await before.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).ConfigureAwait(false);
            return await RunAsync(previous).ConfigureAwait(false);
        }, CancellationToken.None);
        _lastRun = run;
        return run;
    }

    private async Task<ExportEncoderReport> RunAsync(ExportEncoderReport? previous)
    {
        using var limit = new SemaphoreSlim(_maxParallelism);
        Task<ExportEncoderStatus>[] tasks = [.. _catalog.Select(async encoder =>
        {
            if (previous?.Find(encoder.Id) is { } known && known.Result.Status != ExportProbeStatus.Failed)
            {
                return known;
            }

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
        return new ExportEncoderReport(results, _time.GetUtcNow());
    }

    // Una sonda que lanza no puede tumbar a las demás: cuenta como fallo (reintentable) con el motivo.
    private ExportEncoderStatus ProbeOne(EncoderSpec encoder)
    {
        try
        {
            ExportProbeResult result = _probe.Probe(encoder);
            return new ExportEncoderStatus(encoder,
                result.Works || !string.IsNullOrWhiteSpace(result.Reason) ? result : result with { Reason = "Sin motivo." });
        }
#pragma warning disable CA1031 // Cualquier fallo de una sonda debe quedar como «no disponible».
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ExportEncoderStatus(encoder, ExportProbeResult.Fail($"La prueba de {encoder.Id} falló: {ex.Message}"));
        }
    }
}
