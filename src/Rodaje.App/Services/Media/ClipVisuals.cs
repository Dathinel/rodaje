using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

/// <summary>
/// <see cref="IClipVisuals"/> con hilos de trabajo de baja prioridad: dos hacen miniaturas (cada uno con sus propios
/// decodificadores, en la CPU) y otro calcula formas de onda. Lo hecho queda en memoria y en disco
/// (<c>cache\miniaturas</c>, <c>cache\ondas</c>), con la huella del original en la clave.
/// </summary>
/// <remarks>
/// Las miniaturas se piden en tandas (una tira, o una portada): la tanda más reciente va primero y una tanda vieja cede
/// el paso en cuanto llega otra, así al desplazar la línea de tiempo se hace primero lo que está a la vista. Dentro de una
/// tanda se va en orden de tiempo, para que el decodificador avance sin buscar.
/// </remarks>
public sealed partial class ClipVisuals : IClipVisuals, IDisposable
{
    private const string ThumbnailFolder = "miniaturas";
    private const string WaveformFolder = "ondas";
    private const string WaveformExtension = ".picos";
    private const int MaxQueuedBatches = 512;

    private readonly Func<string, string?> _resolve;
    private readonly IVisualsSources _sources;
    private readonly ClipVisualsOptions _options;
    private readonly SynchronizationContext? _context;
    private readonly ThumbnailMemoryCache _thumbnails;
    private readonly ThumbnailDiskCache? _thumbnailDisk;
    private readonly string? _waveformDirectory;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread[] _thumbnailThreads;
    private readonly Thread _waveformThread;

    // object y no Lock: los hilos de trabajo esperan con Monitor.Wait.
    private readonly object _gate = new();
    private readonly SortedSet<Batch> _batches = new(Comparer<Batch>.Create((a, b) => b.Sequence.CompareTo(a.Sequence)));
    private readonly Dictionary<string, Batch> _batchesBySignature = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _waveformQueue = new();
    private readonly HashSet<string> _waveformQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WaveformPeaks> _waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _waveformOrder = new();
    private readonly ConcurrentDictionary<string, string> _failedThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _noWaveform = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _idsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Path, ClipVisualKind Kind)> _changed = [];
    private readonly HashSet<ThumbnailKey> _inFlight = [];
    private readonly Dictionary<string, SortedSet<long>> _indexByFile = new(StringComparer.Ordinal);
    private long _sequence;
    private long _decodedCount;
    private long _diskHits;
    private bool _flushPosted;
    private bool _disposed;

    /// <param name="resolveMediaPath">Ruta absoluta del archivo de un medio del proyecto, o <c>null</c> si no existe el medio.</param>
    /// <param name="sources">Decodificadores; <see cref="FfmpegVisualsSources"/> en la app.</param>
    /// <param name="options">Cachés y límites.</param>
    /// <param name="context">Contexto del hilo de la interfaz, donde se lanza <see cref="Changed"/>; nulo para lanzarlo en el hilo de trabajo.</param>
    public ClipVisuals(Func<string, string?> resolveMediaPath, IVisualsSources sources, ClipVisualsOptions? options = null, SynchronizationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(resolveMediaPath);
        ArgumentNullException.ThrowIfNull(sources);
        _resolve = resolveMediaPath;
        _sources = sources;
        _options = options ?? new ClipVisualsOptions();
        _context = context;
        _thumbnails = new ThumbnailMemoryCache(_options.MemoryBudgetBytes);
        if (_options.CacheDirectory is { } cache)
        {
            _thumbnailDisk = new ThumbnailDiskCache(Path.Combine(cache, ThumbnailFolder));
            _waveformDirectory = Path.Combine(cache, WaveformFolder);
        }

        _thumbnailThreads = [.. Enumerable.Range(0, Math.Max(1, _options.ThumbnailWorkers)).Select(i =>
            new Thread(ThumbnailLoop) { IsBackground = true, Name = $"Rodaje miniaturas {i + 1}", Priority = ThreadPriority.BelowNormal })];
        _waveformThread = new Thread(WaveformLoop) { IsBackground = true, Name = "Rodaje ondas", Priority = ThreadPriority.BelowNormal };
        foreach (Thread thread in _thumbnailThreads)
        {
            thread.Start();
        }

        _waveformThread.Start();
    }

    public event EventHandler<ClipVisualsChangedEventArgs>? Changed;

    /// <summary>Miniaturas hechas desde que se creó (para medir).</summary>
    public long ThumbnailsDecoded => Interlocked.Read(ref _decodedCount);

    /// <summary>Miniaturas que salieron de la caché en disco (para medir).</summary>
    public long ThumbnailsFromDisk => Interlocked.Read(ref _diskHits);

    /// <summary>Recorta las cachés en disco a su presupuesto, en segundo plano.</summary>
    public Task TrimDiskCacheAsync() => Task.Run(() =>
    {
        _thumbnailDisk?.Trim(_options.ThumbnailDiskBudgetBytes);
        if (_waveformDirectory is not null)
        {
            DiskCacheTrimmer.Trim(_waveformDirectory, "*" + WaveformExtension, _options.WaveformDiskBudgetBytes);
        }
    });

    public Filmstrip GetFilmstrip(string mediaId, Time sourceStart, Time sourceEnd, int count, int height)
    {
        if (Resolve(mediaId) is not { } path)
        {
            return Filmstrip.Empty;
        }

        Time[] times = FilmstripLayout.Times(sourceStart, sourceEnd, count);
        return Lookup(path, times, FilmstripLayout.HeightClass(height));
    }

    public BgraImage? GetThumbnail(string mediaId, Time sourceTime, int height)
    {
        if (Resolve(mediaId) is not { } path)
        {
            return null;
        }

        Time time = FilmstripLayout.Snap(sourceTime < Time.Zero ? Time.Zero : sourceTime, FilmstripLayout.BaseStep);
        return Lookup(path, [time], FilmstripLayout.HeightClass(height)).Frames[0];
    }

    public WaveformSlice GetWaveform(string mediaId, Time sourceStart, Time sourceEnd, int buckets)
    {
        if (Resolve(mediaId) is not { } path || buckets <= 0 || _noWaveform.ContainsKey(path))
        {
            return WaveformSlice.None;
        }

        if (!_waveforms.TryGetValue(path, out WaveformPeaks? peaks))
        {
            EnqueueWaveform(path);
            return new WaveformSlice(new float[buckets], new float[buckets], false, true);
        }

        double start = sourceStart.ToSeconds() * peaks.SampleRate;
        double end = sourceEnd.ToSeconds() * peaks.SampleRate;
        bool complete = peaks.IsComplete;
        (float[] min, float[] max) = peaks.Query(start, end, buckets);
        return new WaveformSlice(min, max, complete, true);
    }

    public BgraImage? GetNearestThumbnail(string mediaId, Time sourceTime, Time spacing, int height)
    {
        if (Resolve(mediaId) is not { } path)
        {
            return null;
        }

        int heightClass = FilmstripLayout.HeightClass(height);
        Time step = FilmstripLayout.GridStep(spacing);
        Time time = FilmstripLayout.Snap(sourceTime < Time.Zero ? Time.Zero : sourceTime, step);
        if (Lookup(path, [time], heightClass).Frames[0] is { } exact)
        {
            return exact;
        }

        long? nearest;
        lock (_gate)
        {
            nearest = _indexByFile.TryGetValue(IndexKey(path, heightClass), out SortedSet<long>? times) ? Nearest(times, time.Ticks) : null;
        }

        return nearest is { } ticks && _thumbnails.TryGet(new ThumbnailKey(path, ticks, heightClass), out BgraImage? image) ? image : null;
    }

    /// <summary>Por qué un archivo no da miniaturas, o <c>null</c>.</summary>
    public string? ThumbnailProblem(string path) => _failedThumbnails.TryGetValue(path, out string? reason) ? reason : null;

    /// <summary>Olvida lo que se sabe de un archivo (por ejemplo, porque cambió en disco): se vuelve a leer al pedirlo.</summary>
    public void Forget(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _failedThumbnails.TryRemove(path, out _);
        _noWaveform.TryRemove(path, out _);
        _waveforms.TryRemove(path, out _);
        _fingerprints.TryRemove(path, out _);
        _thumbnails.RemoveFile(path);
        string indexPrefix = path.ToUpperInvariant() + "|";
        lock (_gate)
        {
            foreach (Batch batch in _batches.Where(b => string.Equals(b.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _batches.Remove(batch);
                _batchesBySignature.Remove(batch.Signature);
            }

            foreach (string key in _indexByFile.Keys.Where(k => k.StartsWith(indexPrefix, StringComparison.Ordinal)).ToList())
            {
                _indexByFile.Remove(key);
            }

            for (LinkedListNode<string>? node = _waveformQueue.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value, path, StringComparison.OrdinalIgnoreCase))
                {
                    // Si ya se está calculando, sigue en _waveformQueued hasta que termine: así no se calcula dos veces.
                    _waveformQueue.Remove(node);
                    _waveformQueued.Remove(path);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Suelta lo de los medios que ya no existen (salieron del proyecto o de la biblioteca): sus avisos dejan de lanzarse y,
    /// si ningún otro medio usa el mismo archivo, se olvida el archivo. Si el medio vuelve (deshacer), se lee de nuevo,
    /// casi siempre de la caché en disco.
    /// </summary>
    public void ForgetRemovedMedia()
    {
        List<string> orphans = [];
        lock (_gate)
        {
            foreach ((string path, HashSet<string> ids) in _idsByPath)
            {
                ids.RemoveWhere(id => !string.Equals(_resolve(id), path, StringComparison.OrdinalIgnoreCase));
                if (ids.Count == 0)
                {
                    orphans.Add(path);
                }
            }

            foreach (string path in orphans)
            {
                _idsByPath.Remove(path);
            }
        }

        foreach (string path in orphans)
        {
            Forget(path);
        }
    }

    /// <summary>Pide a los hilos que paren y no los espera más que un instante: cada uno cierra lo suyo al salir.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop.Cancel();
            Monitor.PulseAll(_gate);
        }

        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        bool stopped = true;
        foreach (Thread thread in _thumbnailThreads.Append(_waveformThread))
        {
            TimeSpan left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            stopped &= thread.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        if (stopped)
        {
            // Si un hilo sigue en una lectura larga, todavía mira el token: se deja para el recolector.
            _stop.Dispose();
        }
    }

    private string? Resolve(string mediaId)
    {
        if (string.IsNullOrEmpty(mediaId) || _resolve(mediaId) is not { Length: > 0 } path)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_idsByPath.TryGetValue(path, out HashSet<string>? ids))
            {
                _idsByPath[path] = ids = new HashSet<string>(StringComparer.Ordinal);
            }

            ids.Add(mediaId);
        }

        return path;
    }

    private Filmstrip Lookup(string path, Time[] times, int height)
    {
        var frames = new BgraImage?[times.Length];
        List<long>? missing = null;
        for (int i = 0; i < times.Length; i++)
        {
            if (_thumbnails.TryGet(new ThumbnailKey(path, times[i].Ticks, height), out BgraImage? image))
            {
                frames[i] = image;
            }
            else
            {
                (missing ??= []).Add(times[i].Ticks);
            }
        }

        bool failed = _failedThumbnails.ContainsKey(path);
        if (missing is not null && !failed)
        {
            EnqueueThumbnails(path, height, missing);
        }

        return new Filmstrip(frames, times, missing is null || failed);
    }

    private string FingerprintOf(string path)
    {
        if (_fingerprints.TryGetValue(path, out string? known))
        {
            return known;
        }

        string key = _sources.Fingerprint(path);
        _fingerprints[path] = key;
        return key;
    }

    private void MarkChanged(string path, ClipVisualKind kind)
    {
        bool post;
        lock (_gate)
        {
            _changed.Add((path, kind));
            post = !_flushPosted;
            _flushPosted = true;
        }

        if (!post)
        {
            return;
        }

        if (_context is null)
        {
            Flush(null);
        }
        else
        {
            _context.Post(Flush, null);
        }
    }

    private void Flush(object? state)
    {
        List<(string Id, ClipVisualKind Kind)> events = [];
        lock (_gate)
        {
            _flushPosted = false;
            if (_disposed)
            {
                _changed.Clear();
                return;
            }

            foreach ((string path, ClipVisualKind kind) in _changed)
            {
                if (_idsByPath.TryGetValue(path, out HashSet<string>? ids))
                {
                    events.AddRange(ids.Select(id => (id, kind)));
                }
            }

            _changed.Clear();
        }

        foreach ((string id, ClipVisualKind kind) in events)
        {
            Changed?.Invoke(this, new ClipVisualsChangedEventArgs(id, kind));
        }
    }
}
