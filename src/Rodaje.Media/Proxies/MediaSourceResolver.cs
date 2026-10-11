using Rodaje.Core.Model;
using Rodaje.Media.Probe;

namespace Rodaje.Media.Proxies;

/// <summary>Para qué se va a decodificar un medio.</summary>
public enum MediaPurpose
{
    /// <summary>Vista previa: usa el proxy si está listo.</summary>
    Preview,

    /// <summary>Exportación: siempre el original.</summary>
    Export,
}

/// <summary>Estado del proxy de un original, tal como lo muestra la interfaz.</summary>
public enum ProxyState
{
    /// <summary>Todavía no se evaluó ni se buscó en el almacén.</summary>
    Unknown,

    /// <summary>La política dice que el original se reproduce bien sin proxy.</summary>
    NotNeeded,

    /// <summary>Conviene un proxy y no hay uno listo.</summary>
    Recommended,

    Queued,

    Generating,

    Ready,

    Failed,

    /// <summary>No se pueden generar proxies (falta el ffmpeg.exe LGPL o ningún codificador funciona).</summary>
    Unavailable,
}

/// <summary>Estado del proxy de un original.</summary>
/// <param name="SourcePath">Ruta absoluta del original.</param>
/// <param name="State">Estado.</param>
/// <param name="Progress">Avance de 0 a 1 (1 si está listo).</param>
/// <param name="Recommendation">Última evaluación de la política, si se hizo.</param>
/// <param name="Proxy">El proxy listo, si lo hay.</param>
/// <param name="Message">Motivo del fallo o explicación para el usuario.</param>
/// <param name="Detail">Último avance del generador.</param>
public sealed record ProxyStatus(
    string SourcePath,
    ProxyState State,
    double Progress = 0,
    ProxyRecommendation? Recommendation = null,
    ProxyEntry? Proxy = null,
    string? Message = null,
    ProxyProgress? Detail = null);

public sealed class ProxyStatusChangedEventArgs(ProxyStatus status) : EventArgs
{
    public ProxyStatus Status { get; } = status;
}

/// <summary>Qué archivo decodificar para un medio.</summary>
/// <param name="VideoPath">Proxy o original.</param>
/// <param name="AudioPath">Siempre el original: el proxy no lleva audio.</param>
/// <param name="IsProxy">El video sale del proxy; el compositor lo escala al tamaño del original.</param>
/// <param name="Status">Estado del proxy en ese momento.</param>
public sealed record MediaSourceResolution(string VideoPath, string AudioPath, bool IsProxy, ProxyStatus Status)
{
    public ProxyEntry? Proxy => IsProxy ? Status.Proxy : null;
}

/// <summary>
/// Decide qué archivo decodifica la vista previa de cada medio (el proxy si está listo, si no el original) y
/// coordina la generación de proxies, con un estado por original y un evento para la interfaz.
/// </summary>
/// <remarks>
/// La exportación recibe siempre el original. Si el original cambia en disco (tamaño o fecha), su proxy deja de
/// usarse en la siguiente resolución. El evento <see cref="StatusChanged"/> llega desde hilos de trabajo; la
/// interfaz debe pasarlo a su hilo.
/// </remarks>
public sealed class MediaSourceResolver : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProxyGenerator? _generator;
    private readonly string? _unavailableReason;
    private bool _disposed;

    /// <param name="store">Almacén donde se buscan los proxies.</param>
    /// <param name="generator">Generador, o <c>null</c> si no hay ffmpeg.exe (solo se usan proxies ya hechos).</param>
    /// <param name="policy">Política para <see cref="Assess(string, ProxyCandidate)"/>.</param>
    /// <param name="settings">Ajustes de los proxies que se buscan y se generan.</param>
    /// <param name="unavailableReason">Por qué no hay generador, para mostrarlo.</param>
    public MediaSourceResolver(ProxyStore store, ProxyGenerator? generator, ProxyPolicy? policy = null,
        ProxySettings? settings = null, string? unavailableReason = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        _generator = generator;
        Policy = policy ?? new ProxyPolicy();
        Settings = settings ?? ProxySettings.Default;
        Settings.Validate();
        _unavailableReason = generator is null
            ? unavailableReason ?? "No hay un ffmpeg.exe LGPL junto a las DLL de FFmpeg; no se pueden generar proxies."
            : null;
    }

    public event EventHandler<ProxyStatusChangedEventArgs>? StatusChanged;

    public ProxyStore Store { get; }

    public ProxyPolicy Policy { get; }

    public ProxySettings Settings { get; }

    /// <summary>Si es <c>false</c>, la vista previa también usa los originales (para comparar o si el usuario lo pide).</summary>
    public bool PreviewUsesProxies { get; set; } = true;

    /// <summary>
    /// Si es <c>true</c>, <see cref="Assess(string, ProxyCandidate)"/> encola el proxy en cuanto la política lo recomienda.
    /// Los obligatorios (<see cref="ProxyRecommendation.IsRequired"/>, la reversa por encima de 1080p) se encolan siempre.
    /// </summary>
    public bool GenerateWhenRecommended { get; set; }

    public bool CanGenerate => _generator is not null;

    /// <summary>Resuelve un medio del proyecto.</summary>
    public MediaSourceResolution Resolve(MediaRef media, string projectDirectory, MediaPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(media);
        return Resolve(media.ResolvePath(projectDirectory), purpose);
    }

    /// <summary>
    /// Archivo que hay que decodificar. Rápido si ya se resolvió antes; la primera vez lee 2 MiB del original
    /// para buscar su proxy en el almacén.
    /// </summary>
    public MediaSourceResolution Resolve(string sourcePath, MediaPurpose purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        ProxyStatus status = Refresh(source);
        bool useProxy = purpose == MediaPurpose.Preview && PreviewUsesProxies && status is { State: ProxyState.Ready, Proxy: not null }
            && File.Exists(status.Proxy.ProxyPath);
        return new MediaSourceResolution(useProxy ? status.Proxy!.ProxyPath : source, source, useProxy, status);
    }

    /// <summary>Estado actual del proxy de un original (sin leer el disco si ya se conoce).</summary>
    public ProxyStatus GetStatus(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        lock (_gate)
        {
            return _sources.TryGetValue(source, out SourceState? state) ? state.Status : new ProxyStatus(source, ProxyState.Unknown);
        }
    }

    /// <summary>Estados de todos los originales conocidos.</summary>
    public IReadOnlyList<ProxyStatus> Snapshot()
    {
        lock (_gate)
        {
            return _sources.Values.Select(s => s.Status).ToList();
        }
    }

    /// <summary>
    /// Evalúa con la política si el original necesita proxy y lo anota en su estado; si
    /// <see cref="GenerateWhenRecommended"/> está activo y no hay proxy, lo encola.
    /// </summary>
    public ProxyRecommendation Assess(string sourcePath, ProxyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        string source = Path.GetFullPath(sourcePath);
        ProxyRecommendation recommendation = Policy.Evaluate(candidate);
        ProxyStatus status = Refresh(source);
        bool generate = false;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            ProxyState next = state.Status.State switch
            {
                ProxyState.Unknown or ProxyState.NotNeeded or ProxyState.Recommended =>
                    recommendation.IsRecommended ? (CanGenerate ? ProxyState.Recommended : ProxyState.Unavailable) : ProxyState.NotNeeded,
                _ => state.Status.State,
            };
            string? message = next == ProxyState.Unavailable ? _unavailableReason : state.Status.Message;
            status = state.Status with { State = next, Recommendation = recommendation, Message = message };
            state.Status = status;
            // Uno obligatorio (la reversa por encima de 1080p) se genera aunque no se generen solos los recomendados.
            generate = (GenerateWhenRecommended || recommendation.IsRequired) && next == ProxyState.Recommended;
        }

        Raise(status);
        if (generate)
        {
            _ = RequestProxyAsync(source).ContinueWith(
                static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        return recommendation;
    }

    /// <summary>Evalúa con lo que dice el archivo y, si se tienen, las medidas de estructura y velocidad.</summary>
    public ProxyRecommendation Assess(MediaInfo info, StreamStructure? structure = null, double? decodeSpeed = null, int layers = 1)
    {
        ArgumentNullException.ThrowIfNull(info);
        return Assess(info.Path, ProxyCandidate.From(info, structure, decodeSpeed, layers));
    }

    /// <summary>
    /// Pide el proxy del original; si ya se está generando devuelve la misma tarea. Al terminar, la vista previa
    /// pasa a usarlo en la siguiente resolución.
    /// </summary>
    /// <exception cref="InvalidOperationException">No hay generador (ver <see cref="CanGenerate"/>).</exception>
    public Task<ProxyEntry> RequestProxyAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string source = Path.GetFullPath(sourcePath);
        if (_generator is null)
        {
            Update(source, s => s with { State = ProxyState.Unavailable, Message = _unavailableReason });
            throw new InvalidOperationException(_unavailableReason);
        }

        ProxyStatus initial;
        Job job;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            if (state.Job is { } running)
            {
                return running.Task;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            job = new Job(cancellation);
            state.Job = job;
            initial = state.Status = state.Status with { State = ProxyState.Queued, Progress = 0, Message = null, Detail = null };
            job.Task = RunJob(source, job);
        }

        Raise(initial);
        return job.Task;
    }

    /// <summary>Cancela la generación del proxy de ese original; devuelve <c>false</c> si no se estaba generando.</summary>
    public bool Cancel(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        lock (_gate)
        {
            if (_sources.TryGetValue(source, out SourceState? state) && state.Job is { } job)
            {
                job.Cancellation.Cancel();
                return true;
            }
        }

        return false;
    }

    /// <summary>Olvida lo que se sabe del original (por ejemplo, después de borrar su proxy a mano).</summary>
    public void Forget(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        lock (_gate)
        {
            if (_sources.TryGetValue(source, out SourceState? state) && state.Job is null)
            {
                _sources.Remove(source);
            }
        }
    }

    public void Dispose()
    {
        List<Job> jobs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            jobs = _sources.Values.Select(s => s.Job).OfType<Job>().ToList();
        }

        foreach (Job job in jobs)
        {
            job.Cancellation.Cancel();
        }
    }

    private async Task<ProxyEntry> RunJob(string source, Job job)
    {
        await Task.Yield();
        var progress = new InlineProgress<ProxyProgress>(p =>
        {
            // La cola ya se anunció al pedir el proxy.
            if (p.Phase != ProxyJobPhase.Queued)
            {
                Update(source, s => s with { State = ProxyState.Generating, Progress = p.Fraction, Detail = p });
            }
        });
        try
        {
            ProxyEntry entry = await _generator!.GenerateAsync(source, Settings, progress, job.Cancellation.Token).ConfigureAwait(false);
            SourceFingerprint fingerprint = new(entry.Metadata.SourcePath, entry.Metadata.SourceSize, entry.Metadata.SourceLastWriteUtc, entry.Metadata.SourceKey);
            Finish(source, job, s => s with { State = ProxyState.Ready, Progress = 1, Proxy = entry, Message = null }, fingerprint);
            return entry;
        }
        catch (OperationCanceledException)
        {
            Finish(source, job, s => s with
            {
                State = s.Recommendation?.IsRecommended == true ? ProxyState.Recommended : ProxyState.Unknown,
                Progress = 0,
                Message = "Generación cancelada.",
            }, null);
            throw;
        }
        catch (Exception ex) when (ex is ProxyGenerationException or MediaException or IOException or UnauthorizedAccessException or FfmpegNotFoundException)
        {
            Finish(source, job, s => s with { State = ProxyState.Failed, Progress = 0, Message = ex.Message }, null);
            throw;
        }
        finally
        {
            job.Cancellation.Dispose();
        }
    }

    // Comprueba en el disco si el estado sigue valiendo: el original pudo cambiar y el proxy pudo aparecer o borrarse.
    private ProxyStatus Refresh(string source)
    {
        SourceFingerprint? known;
        ProxyStatus current;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            if (state.Job is not null)
            {
                return state.Status;
            }

            known = state.Fingerprint;
            current = state.Status;
        }

        SourceFingerprint? fingerprint = null;
        ProxyEntry? entry = null;
        try
        {
            if (known is not null && known.MatchesQuick(source))
            {
                if (current is { State: ProxyState.Ready, Proxy: not null } && File.Exists(current.Proxy.ProxyPath))
                {
                    return current;
                }

                // Sin releer el original: solo se mira si el proxy apareció (otro proceso, o un Commit externo).
                fingerprint = known;
                entry = Store.TryGet(known, Settings);
                if (entry is null && current.State != ProxyState.Ready)
                {
                    return current;
                }
            }
            else
            {
                fingerprint = SourceFingerprint.Compute(source);
                entry = Store.TryGet(fingerprint, Settings);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        ProxyStatus updated;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            if (state.Job is not null)
            {
                return state.Status;
            }

            bool changed = known is not null && (fingerprint is null || fingerprint.Key != known.Key);
            ProxyRecommendation? recommendation = changed ? null : state.Status.Recommendation;
            ProxyState next = entry is not null ? ProxyState.Ready
                : changed ? ProxyState.Unknown
                : state.Status.State == ProxyState.Ready ? (recommendation?.IsRecommended == true ? ProxyState.Recommended : ProxyState.Unknown)
                : state.Status.State;
            state.Fingerprint = fingerprint;
            updated = state.Status = state.Status with
            {
                State = next,
                Progress = entry is not null ? 1 : 0,
                Proxy = entry,
                Recommendation = recommendation,
                Message = fingerprint is null ? "No se pudo leer el original." : state.Status.Message,
            };
        }

        if (updated != current)
        {
            Raise(updated);
        }

        return updated;
    }

    private void Update(string source, Func<ProxyStatus, ProxyStatus> change)
    {
        ProxyStatus status;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            status = state.Status = change(state.Status);
        }

        Raise(status);
    }

    private void Finish(string source, Job job, Func<ProxyStatus, ProxyStatus> change, SourceFingerprint? fingerprint)
    {
        ProxyStatus status;
        lock (_gate)
        {
            SourceState state = StateOf(source);
            if (state.Job == job)
            {
                state.Job = null;
            }

            if (fingerprint is not null)
            {
                state.Fingerprint = fingerprint;
            }

            status = state.Status = change(state.Status);
        }

        Raise(status);
    }

    private SourceState StateOf(string source)
    {
        if (!_sources.TryGetValue(source, out SourceState? state))
        {
            state = new SourceState(new ProxyStatus(source, ProxyState.Unknown));
            _sources[source] = state;
        }

        return state;
    }

    private void Raise(ProxyStatus status) => StatusChanged?.Invoke(this, new ProxyStatusChangedEventArgs(status));

    private sealed class SourceState(ProxyStatus status)
    {
        public ProxyStatus Status { get; set; } = status;

        public SourceFingerprint? Fingerprint { get; set; }

        public Job? Job { get; set; }
    }

    private sealed class Job(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task<ProxyEntry> Task { get; set; } = null!;
    }

    // Progress<T> publica en el contexto de sincronización del que lo crea; aquí el evento ya documenta que llega de otro hilo.
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
