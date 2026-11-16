using Rodaje.App.Services.Crashes;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Engine.Timeline;
using Rodaje.Export;
using Rodaje.Export.Running;
using Rodaje.Media.Proxies;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Services.RenderCache;

/// <summary>Un plan que el reproductor acaba de abrir o recargar.</summary>
/// <param name="Generation">Sube con cada sesión nueva del reproductor: la revisión vuelve a 0 al abrir otra.</param>
/// <param name="Revision">La <c>TimelineSession.PlanRevision</c> de ese plan.</param>
public sealed record RenderCachePlan(Project Project, string SequenceId, int Generation, long Revision, long CursorFrame);

/// <summary>Lo que el render en caché necesita del reproductor.</summary>
public interface IRenderCachePlayer
{
    PlaybackSnapshot Snapshot { get; }

    /// <summary>Cuadros con refrescos tarde medidos al reproducir (los últimos).</summary>
    IReadOnlyList<long> LateFrames { get; }

    /// <summary>Compone en una GPU de verdad (sin ella, en WARP, todo cuesta unas 54 veces más).</summary>
    bool HasGpu { get; }

    /// <summary>Pone la cobertura en la sesión de esa generación, si sigue abierta y en esa revisión.</summary>
    void ApplyCoverage(int generation, CacheCoverage coverage);
}

/// <summary>Lo que ve el panel de trabajos.</summary>
public sealed record RenderCacheStatus(RenderCacheState State, RenderCacheWork? Current, double Progress, int Pending, int Completed, int Failed);

/// <summary>
/// El render en caché en la app (D-060): calcula las huellas y la previsión de cada plan del reproductor en un hilo
/// aparte, pone la cobertura en la sesión, pinta la franja y lleva el <see cref="RenderCacheService"/> con trabajos
/// <see cref="RenderCacheJob"/> sobre una copia del proyecto. Se usa desde cualquier hilo.
/// </summary>
public sealed partial class RenderCacheHost : IRenderCacheWorker, IDisposable
{
    private static readonly TimeSpan ScanDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);
    private const long GiB = 1024L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly ExportDevicePool _devices = new();
    private readonly IRenderCachePlayer _player;
    private readonly IEffectContent? _content;
    private readonly Func<IReadOnlyList<string>>? _fontFiles;
    private readonly Func<GeneratedContent, bool> _fontMissing;
    private readonly Func<FfmpegExecutable?> _findFfmpeg;
    private readonly TimeProvider _time;
    private readonly Timer _timer;
    private readonly Lazy<FfmpegExecutable?> _ffmpeg;
    private readonly Lazy<AppRecipeSources> _sources;
    private RenderCacheStore _store;
    private RenderCacheService _service;
    private long _capBytes;
    private RenderCachePlan? _plan;
    private RenderCacheScan? _scan;

    // El plan del que salió _scan: los trabajos calculan su proyecto, nunca el de un plan más nuevo aún sin recorrer.
    private RenderCachePlan? _scanPlan;
    private HashSet<RenderCacheKey> _current = [];
    private CancellationTokenSource? _scanCancel;
    private RenderCacheJob? _job;
    private RenderCacheKey[]? _jobKeys;
    private bool _playing;
    private bool _moving;
    private long _lastFrame = -1;
    private int _completed;
    private long _lastCleanup;
    private bool _disposed;

    public RenderCacheHost(IRenderCachePlayer player, RenderCacheMode mode, string? folder, double? capGb, IEffectContent? content,
        Func<IReadOnlyList<string>>? fontFiles, Func<GeneratedContent, bool> fontMissing, Func<FfmpegExecutable?>? findFfmpeg = null,
        TimeProvider? time = null, TimeSpan? tick = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(fontMissing);
        _player = player;
        _content = content;
        _fontFiles = fontFiles;
        _fontMissing = fontMissing;
        _findFfmpeg = findFfmpeg ?? (() => FfmpegExecutable.TryLocate(out _));
        _time = time ?? TimeProvider.System;
        _ffmpeg = new(() => _findFfmpeg(), LazyThreadSafetyMode.ExecutionAndPublication);
        _sources = new(() => new AppRecipeSources(_content, _fontMissing, RenderCacheJob.Chain(_ffmpeg.Value?.Version ?? "")),
            LazyThreadSafetyMode.ExecutionAndPublication);
        (_store, _service, _capBytes) = Open(folder, capGb, mode);
        _lastCleanup = _time.GetTimestamp();
        _timer = new Timer(_ => Tick(), null, tick ?? TimeSpan.FromMilliseconds(250), tick ?? TimeSpan.FromMilliseconds(250));
    }

    /// <summary>La carpeta del proyecto abierto, contra la que se resuelven las rutas relativas (nula sin guardar).</summary>
    public Func<string?> ProjectDirectory { get; init; } = () => null;

    /// <summary>La franja cambió (desde cualquier hilo).</summary>
    public event EventHandler? StripChanged;

    /// <summary>El estado del cálculo cambió (desde cualquier hilo).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Lo que pinta la franja sobre la regla para el último plan.</summary>
    public IReadOnlyList<CacheStripSpan> Strip { get; private set; } = [];

    /// <summary>La tasa de la secuencia de <see cref="Strip"/>.</summary>
    public Rational StripRate { get; private set; } = Rational.Fps30;

    /// <summary>La secuencia de <see cref="Strip"/> (el visor de origen también abre planes).</summary>
    public string? StripSequenceId { get; private set; }

    public string Directory
    {
        get
        {
            lock (_gate)
            {
                return _store.Directory;
            }
        }
    }

    public long CapBytes
    {
        get
        {
            lock (_gate)
            {
                return _capBytes;
            }
        }
    }

    public RenderCacheStatus Status
    {
        get
        {
            lock (_gate)
            {
                double progress = _job?.Export is { } export && _service.Current is { Count: > 0 } work ? Math.Clamp(export.FramesWritten / (double)work.Count, 0, 1) : 0;
                return new RenderCacheStatus(_service.State, _service.Current, progress, _service.Pending.Count, _service.Completed, _service.Failed);
            }
        }
    }

    /// <summary>El tope por omisión: el menor entre 50 GB y el 10 % del espacio libre, con un mínimo de 5 GB.</summary>
    public static long DefaultCap(long freeBytes) => Math.Max(5 * GiB, Math.Min(50 * GiB, freeBytes / 10));

    /// <summary>Preferencias › Rendimiento: el modo cambia en el acto; otra carpeta u otro tope abren el almacén de nuevo.</summary>
    public void Configure(RenderCacheMode mode, string? folder, double? capGb)
    {
        lock (_gate)
        {
            string directory = Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? RenderCacheStore.DefaultDirectory : folder);
            if (!string.Equals(directory, _store.Directory, StringComparison.OrdinalIgnoreCase) || Cap(directory, capGb) != _capBytes)
            {
                _service.Cancel();
                (_store, _service, _capBytes) = Open(folder, capGb, mode);
                _completed = 0;
            }
            else
            {
                _service.Mode = mode;
            }
        }

        Rescan();
    }

    /// <summary>El reproductor abrió o recargó un plan: huellas y previsión 150 ms después, en otro hilo.</summary>
    public void OnPlan(RenderCachePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _plan = plan;
            _service.NotifyEdited();
            _scanCancel?.Cancel();
            _scanCancel?.Dispose();
            _scanCancel = new CancellationTokenSource();
            token = _scanCancel.Token;
        }

        UiTask.Run(ScanAsync(plan, token));
    }

    /// <summary>«Secuencia › Calcular de entrada a salida»: va delante de lo demás (también en modo manual).</summary>
    public void Request(Time start, Time end)
    {
        lock (_gate)
        {
            if (_scan is not { } scan)
            {
                return;
            }

            long first = Math.Clamp(start.ToFrames(scan.FrameRate), 0, scan.Keys.Length);
            long last = Math.Clamp(end.ToFrames(scan.FrameRate), first, scan.Keys.Length);
            long from = -1;
            var spans = new List<(long, long)>();
            for (long f = first; f <= last; f++)
            {
                bool usable = f < last && scan.Keys[f] is { } key && !_store.TryGet(key, null, out _);
                if (usable && from < 0)
                {
                    from = f;
                }
                else if (!usable && from >= 0)
                {
                    spans.Add((from, f));
                    from = -1;
                }
            }

            // Delante va lo primero: se encola de atrás adelante.
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                _service.Request(spans[i].Item1, spans[i].Item2);
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Corta el cálculo en curso y vacía la cola (Trabajos › Cancelar).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _service.Cancel();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Borra todo lo que no se está leyendo; devuelve los bytes liberados.</summary>
    public long Clear()
    {
        long freed;
        lock (_gate)
        {
            _service.Cancel();
            if (_scanPlan is { } shown)
            {
                // Sin cobertura el reproductor suelta los fragmentos que lee; los que aún estén abiertos se borran en la limpieza siguiente.
                _player.ApplyCoverage(shown.Generation, new CacheCoverage(shown.Revision, []));
            }

            freed = _store.Cleanup(new RenderCacheCleanupOptions { MaxTotalBytes = 0, TemporaryMaxAge = TimeSpan.Zero }).BytesFreed;
        }

        Rescan();
        return freed;
    }

    /// <summary>Un fragmento del caché no se pudo leer al reproducir: sale del índice y la cobertura se rehace sin él.</summary>
    public void OnSourceFailed(string path)
    {
        lock (_gate)
        {
            if (!IsInside(path, _store.Directory) || !_store.Forget(path))
            {
                return;
            }
        }

        Rescan();
    }

    /// <summary>La exportación con «Usar el render en caché»: el mismo almacén y las mismas huellas que los fragmentos.</summary>
    public ExportRunnerOptions WithRenderCache(ExportRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            return options with
            {
                RenderCache = new CurrentStore(this),
                RecipeBuilder = request => RecipeBuilder.Create(request.Project, request.SequenceId, _sources.Value.Options(request.ProjectDirectory)),
            };
        }
    }

    async Task<bool> IRenderCacheWorker.RunAsync(RenderCacheWork work, ExportPauseToken pause, CancellationToken cancellationToken)
    {
        RenderCacheJob job;
        lock (_gate)
        {
            if (JobInput(work) is not { } input)
            {
                return false;
            }

            FfmpegExecutable ffmpeg = _ffmpeg.Value ?? throw new InvalidOperationException("No se encontró el ffmpeg.exe LGPL.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            var options = new ExportRunnerOptions { Ffmpeg = ffmpeg, Content = _content, FontFiles = _fontFiles, DevicePool = _devices };
            job = new RenderCacheJob(input.Plan.Project.Clone(), input.Plan.SequenceId, work.FirstFrame, work.Count, input.Keys, _store, options,
                StillCurrent, pause, ProjectDirectory());
            _job = job;
            _jobKeys = input.Keys;
        }

        try
        {
            RenderCacheFragment? fragment = await Task.Run(() => job.RunAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            return fragment is not null;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_job, job))
                {
                    _job = null;
                    _jobKeys = null;
                }
            }
        }
    }

    /// <summary>El plan recorrido y las huellas de un trozo; nulo si el trozo es de otra revisión o le falta alguna huella.</summary>
    internal (RenderCachePlan Plan, RenderCacheKey[] Keys)? JobInput(RenderCacheWork work)
    {
        lock (_gate)
        {
            return _scanPlan is { } plan && _scan is { } scan && scan.Revision == work.Revision && scan.KeysOf(work.FirstFrame, work.Count) is { } keys
                ? (plan, keys)
                : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scanCancel?.Cancel();
            _service.Cancel();
        }

        _timer.Dispose();
        _devices.Dispose();
    }

    /// <summary>Recoge el trabajo, pausa al reproducir o mover el cursor, limpia cada 10 min y rehace la franja si hace falta.</summary>
    internal void Tick()
    {
        bool refresh = false;
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            PlaybackSnapshot snapshot = _player.Snapshot;
            RenderCacheStatus before = StatusCore();
            if (snapshot.IsPlaying != _playing)
            {
                refresh = !snapshot.IsPlaying;
                _playing = snapshot.IsPlaying;
                _service.NotifyPlaying(_playing);
            }

            bool moving = !snapshot.IsPlaying && _lastFrame >= 0 && snapshot.Frame != _lastFrame;
            _lastFrame = snapshot.Frame;
            if (moving != _moving)
            {
                _moving = moving;
                _service.NotifyScrubbing(moving);
            }

            _service.Tick();
            if (_service.Completed != _completed)
            {
                _completed = _service.Completed;
                refresh = true;
            }

            if (_time.GetElapsedTime(_lastCleanup) >= CleanupInterval && _service.State is RenderCacheState.Idle or RenderCacheState.Waiting)
            {
                _lastCleanup = _time.GetTimestamp();
                _store.Cleanup(new RenderCacheCleanupOptions { MaxTotalBytes = _capBytes, ProtectedKeys = _current });
            }

            changed = StatusCore() != before;
        }

        if (refresh)
        {
            Rescan();
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private RenderCacheStatus StatusCore() => new(_service.State, _service.Current, 0, _service.Pending.Count, _service.Completed, _service.Failed);

    private async Task ScanAsync(RenderCachePlan plan, CancellationToken token)
    {
        try
        {
            await Task.Delay(ScanDelay, _time, token).ConfigureAwait(false);
            RenderCacheScan scan = await Task.Run(() => RenderCacheScan.Run(plan.Project, plan.SequenceId, plan.Revision,
                _sources.Value.Options(ProjectDirectory()), _player.HasGpu, token), token).ConfigureAwait(false);
            Publish(plan, scan, setPlan: true);
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Una secuencia que no se puede recorrer se queda sin caché; el reproductor sigue.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>La cobertura y la franja de nuevo con las huellas ya calculadas (tras un fragmento, al parar o al olvidar uno).</summary>
    private void Rescan()
    {
        RenderCachePlan? plan;
        RenderCacheScan? scan;
        lock (_gate)
        {
            plan = ReferenceEquals(_scanPlan, _plan) ? _plan : null;
            scan = _scan;
        }

        if (plan is not null && scan is not null)
        {
            UiTask.Run(Task.Run(() => Publish(plan, scan, setPlan: false)));
        }
    }

    private void Publish(RenderCachePlan plan, RenderCacheScan scan, bool setPlan)
    {
        RenderCacheStore store;
        lock (_gate)
        {
            store = _store;
        }

        CacheCoverage coverage = CacheCoverage.Find(scan.Revision, scan.Keys, store, scan.FrameRate);
        IReadOnlyList<CacheStripSpan> strip = scan.Strip(coverage, _player.LateFrames.ToHashSet());
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_plan, plan) || !ReferenceEquals(_store, store))
            {
                return;
            }

            if (setPlan)
            {
                _scan = scan;
                _scanPlan = plan;
                _current = [.. scan.Keys.OfType<RenderCacheKey>()];
                RenderCacheKey[]? running = _jobKeys;
                _service.SetPlan(scan.Revision, scan.Work(strip), plan.CursorFrame, scan.FrameRate,
                    work => running is not null && scan.KeysOf(work.FirstFrame, work.Count) is { } now && now.AsSpan().SequenceEqual(running),
                    scan.Keys.Length);
            }

            Strip = strip;
            StripRate = scan.FrameRate;
            StripSequenceId = plan.SequenceId;
        }

        _player.ApplyCoverage(plan.Generation, coverage);
        StripChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Un fragmento terminado se publica si sus huellas siguen en la secuencia (aunque se hayan movido de sitio).</summary>
    private bool StillCurrent(IReadOnlyList<RenderCacheKey> keys)
    {
        lock (_gate)
        {
            return keys.All(_current.Contains);
        }
    }

    private (RenderCacheStore Store, RenderCacheService Service, long Cap) Open(string? folder, double? capGb, RenderCacheMode mode)
    {
        var store = new RenderCacheStore(string.IsNullOrWhiteSpace(folder) ? null : folder);
        long cap = Cap(store.Directory, capGb);
        var service = new RenderCacheService(this, _time, () => FreeBytes(store.Directory), Cached(store.TotalBytes), cap) { Mode = mode };
        store.Cleanup(new RenderCacheCleanupOptions { MaxTotalBytes = cap, ProtectedKeys = _current });
        return (store, service, cap);
    }

    private static long Cap(string directory, double? capGb) => capGb is { } gb ? (long)(gb * GiB) : DefaultCap(FreeBytes(directory));

    /// <summary>Espacio libre del disco de la carpeta; si no se puede saber (una ruta de red, por ejemplo), no frena: manda el tope.</summary>
    internal static long FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
#pragma warning disable CA1031 // Cualquier fallo al preguntar al disco cuenta como «no se sabe».
        catch (Exception)
#pragma warning restore CA1031
        {
            return long.MaxValue;
        }
    }

    /// <summary>Lo que ocupa la carpeta, leído como mucho cada 5 s: el servicio lo pregunta en cada tic mientras espera sitio.</summary>
    private Func<long> Cached(Func<long> read)
    {
        long value = 0;
        long at = long.MinValue;
        return () =>
        {
            if (at == long.MinValue || _time.GetElapsedTime(at) >= TimeSpan.FromSeconds(5))
            {
                value = read();
                at = _time.GetTimestamp();
            }

            return value;
        };
    }

    /// <summary>El almacén vigente para la exportación: Preferencias puede cambiar de carpeta después de crear el exportador.</summary>
    private sealed class CurrentStore(RenderCacheHost host) : IRenderCacheStore
    {
        private RenderCacheStore Store
        {
            get
            {
                lock (host._gate)
                {
                    return host._store;
                }
            }
        }

        public string Directory => Store.Directory;

        public int Count => Store.Count;

        public bool TryGet(RenderCacheKey key, RenderCacheHit? previous, out RenderCacheHit hit) => Store.TryGet(key, previous, out hit);

        public string CreateTemporaryPath() => Store.CreateTemporaryPath();

        public RenderCacheFragment Commit(string temporaryMoviePath, RenderCacheFragmentMetadata metadata) => Store.Commit(temporaryMoviePath, metadata);

        public void Forget(RenderCacheFragment fragment) => Store.Forget(fragment);

        public long TotalBytes() => Store.TotalBytes();

        public RenderCacheCleanupReport Cleanup(RenderCacheCleanupOptions? options = null) => Store.Cleanup(options);
    }

    private static bool IsInside(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
