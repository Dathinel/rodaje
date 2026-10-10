using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.App.Services.Media;

public sealed record ClipVisualsOptions
{
    /// <summary>Carpeta de la caché en disco (<c>miniaturas</c> y <c>ondas</c> dentro); nula para no usar disco.</summary>
    public string? CacheDirectory { get; init; }

    public long MemoryBudgetBytes { get; init; } = 128L << 20;

    public long ThumbnailDiskBudgetBytes { get; init; } = 512L << 20;

    public long WaveformDiskBudgetBytes { get; init; } = 128L << 20;

    /// <summary>Hilos que hacen miniaturas; cada uno con sus decodificadores.</summary>
    public int ThumbnailWorkers { get; init; } = 2;

    /// <summary>Decodificadores de miniaturas abiertos a la vez por hilo (uno por archivo).</summary>
    public int MaxOpenDecoders { get; init; } = 3;

    /// <summary>Formas de onda que se guardan en memoria.</summary>
    public int MaxWaveformsInMemory { get; init; } = 64;

    /// <summary>Un decodificador sin usar este tiempo se cierra.</summary>
    public TimeSpan DecoderIdleTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Carpeta de siempre: <c>%LOCALAPPDATA%\Rodaje\cache</c> (o la de <c>RODAJE_DATOS</c>).</summary>
    public static string DefaultCacheDirectory => Path.Combine(AppDataPaths.Root, "cache");
}

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

    private void EnqueueThumbnails(string path, int height, List<long> times)
    {
        long[] sorted = [.. times.Distinct().Order()];
        string signature = $"{path}|{height}|{sorted[0]}|{sorted[^1]}|{sorted.Length}";
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_batchesBySignature.Remove(signature, out Batch? existing))
            {
                _batches.Remove(existing);
            }

            var batch = new Batch(path, height, sorted, ++_sequence, signature);
            _batches.Add(batch);
            _batchesBySignature[signature] = batch;
            while (_batches.Count > MaxQueuedBatches)
            {
                Batch oldest = _batches.Max!;
                _batches.Remove(oldest);
                _batchesBySignature.Remove(oldest.Signature);
            }

            Monitor.PulseAll(_gate);
        }
    }

    private void EnqueueWaveform(string path)
    {
        lock (_gate)
        {
            if (_disposed || !_waveformQueued.Add(path))
            {
                return;
            }

            _waveformQueue.AddFirst(path);
            Monitor.PulseAll(_gate);
        }
    }

    private void ThumbnailLoop()
    {
        var readers = new List<OpenReader>();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                Batch? batch;
                lock (_gate)
                {
                    while (_batches.Count == 0 && !_disposed)
                    {
                        if (!Monitor.Wait(_gate, TimeSpan.FromSeconds(2)))
                        {
                            CloseIdle(readers);
                        }
                    }

                    if (_disposed)
                    {
                        return;
                    }

                    batch = _batches.Min!;
                    _batches.Remove(batch);
                    _batchesBySignature.Remove(batch.Signature);
                }

                ProcessBatch(batch, readers);
                CloseIdle(readers);
            }
        }
        finally
        {
            foreach (OpenReader open in readers)
            {
                DisposeQuietly(open.Reader);
            }
        }
    }

    private void ProcessBatch(Batch batch, List<OpenReader> readers)
    {
        string? fingerprint = null;
        for (int i = batch.Next; i < batch.Times.Length; i++)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            if (i > batch.Next && YieldTo(batch, i))
            {
                return;
            }

            var key = new ThumbnailKey(batch.Path, batch.Times[i], batch.Height);
            if (_thumbnails.Contains(key) || !Claim(key))
            {
                // Hecha, o la está haciendo otro hilo: llegará con su aviso.
                continue;
            }

            try
            {
                fingerprint ??= FingerprintOf(batch.Path);
                BgraImage? image = _thumbnailDisk?.TryRead(fingerprint, batch.Height, batch.Times[i]);
                if (image is not null)
                {
                    Interlocked.Increment(ref _diskHits);
                }
                else
                {
                    IThumbnailReader reader = ReaderFor(batch.Path, readers);
                    image = reader.Decode(Time.FromTicks(batch.Times[i]), batch.Height, _stop.Token);
                    if (image is null)
                    {
                        // Sin ningún cuadro decodificable: se recuerda para no volver a abrirlo en cada redibujo.
                        Fail(batch.Path, "El archivo no tiene cuadros de video.", readers);
                        return;
                    }

                    Interlocked.Increment(ref _decodedCount);
                    _thumbnailDisk?.Write(fingerprint, batch.Height, batch.Times[i], image);
                }

                _thumbnails.Add(key, image);
                lock (_gate)
                {
                    string index = IndexKey(key.Path, key.Height);
                    if (!_indexByFile.TryGetValue(index, out SortedSet<long>? times))
                    {
                        _indexByFile[index] = times = [];
                    }

                    times.Add(key.TimeTicks);
                }

                MarkChanged(batch.Path, ClipVisualKind.Thumbnails);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Cualquier fallo de un archivo, incluso inesperado, se queda en ese archivo: el hilo sigue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Fail(batch.Path, ex.Message, readers);
                return;
            }
            finally
            {
                Release(key);
            }
        }
    }

    private void Fail(string path, string reason, List<OpenReader> readers)
    {
        Close(path, readers);
        _failedThumbnails[path] = reason;
        MarkChanged(path, ClipVisualKind.Thumbnails);
    }

    private static string IndexKey(string path, int height) => path.ToUpperInvariant() + "|" + height.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long? Nearest(SortedSet<long> times, long target)
    {
        if (times.Count == 0)
        {
            return null;
        }

        long? below = times.GetViewBetween(long.MinValue, target) is { Count: > 0 } low ? low.Max : null;
        long? above = times.GetViewBetween(target, long.MaxValue) is { Count: > 0 } high ? high.Min : null;
        return (below, above) switch
        {
            ({ } b, { } a) => target - b <= a - target ? b : a,
            ({ } b, null) => b,
            (null, { } a) => a,
            _ => null,
        };
    }

    private bool Claim(ThumbnailKey key)
    {
        lock (_gate)
        {
            return _inFlight.Add(key);
        }
    }

    private void Release(ThumbnailKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
        }
    }

    /// <summary>Si llegó una tanda más nueva, la actual vuelve a la cola desde <paramref name="next"/> y cede el paso.</summary>
    private bool YieldTo(Batch batch, int next)
    {
        lock (_gate)
        {
            if (_batches.Count == 0 || _batches.Min!.Sequence < batch.Sequence)
            {
                return false;
            }

            if (!_batchesBySignature.ContainsKey(batch.Signature))
            {
                batch.Next = next;
                _batches.Add(batch);
                _batchesBySignature[batch.Signature] = batch;
            }

            return true;
        }
    }

    private IThumbnailReader ReaderFor(string path, List<OpenReader> readers)
    {
        int index = readers.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            OpenReader found = readers[index];
            readers[index] = found with { LastUsed = Stopwatch.GetTimestamp() };
            return found.Reader;
        }

        while (readers.Count >= Math.Max(1, _options.MaxOpenDecoders))
        {
            OpenReader oldest = readers.MinBy(r => r.LastUsed)!;
            DisposeQuietly(oldest.Reader);
            readers.Remove(oldest);
        }

        IThumbnailReader reader = _sources.OpenThumbnails(path);
        readers.Add(new OpenReader(path, reader, Stopwatch.GetTimestamp()));
        return reader;
    }

    private static void Close(string path, List<OpenReader> readers)
    {
        int index = readers.FindIndex(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            DisposeQuietly(readers[index].Reader);
            readers.RemoveAt(index);
        }
    }

    private static void DisposeQuietly(IThumbnailReader reader)
    {
        try
        {
            reader.Dispose();
        }
#pragma warning disable CA1031 // Un decodificador que falla al cerrarse no debe tumbar el hilo.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void CloseIdle(List<OpenReader> readers)
    {
        for (int i = readers.Count - 1; i >= 0; i--)
        {
            if (Stopwatch.GetElapsedTime(readers[i].LastUsed) > _options.DecoderIdleTimeout)
            {
                DisposeQuietly(readers[i].Reader);
                readers.RemoveAt(i);
            }
        }
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

    private void WaveformLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            string path;
            lock (_gate)
            {
                while (_waveformQueue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                path = _waveformQueue.First!.Value;
                _waveformQueue.RemoveFirst();
            }

            try
            {
                ComputeWaveform(path);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Como con las miniaturas: el fallo se queda en ese archivo.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _waveforms.TryRemove(path, out _);
                _noWaveform[path] = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _waveformQueued.Remove(path);
                }

                MarkChanged(path, ClipVisualKind.Waveform);
            }
        }
    }

    private void ComputeWaveform(string path)
    {
        string fingerprint = FingerprintOf(path);
        string? cached = _waveformDirectory is null ? null : Path.Combine(_waveformDirectory, fingerprint + WaveformExtension);
        if (cached is not null && File.Exists(cached) && WaveformPeaks.Deserialize(File.ReadAllBytes(cached)) is { } stored)
        {
            Publish(path, stored);
            return;
        }

        using IAudioSampleReader? reader = _sources.OpenAudio(path);
        if (reader is null)
        {
            _noWaveform[path] = "sin audio";
            return;
        }

        var peaks = new WaveformPeaks(reader.SampleRate, reader.ExpectedSamples);
        Publish(path, peaks);
        float[] buffer = new float[16_384 * reader.Channels];
        long lastNotice = Stopwatch.GetTimestamp();
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            _stop.Token.ThrowIfCancellationRequested();
            peaks.Append(buffer.AsSpan(0, read * reader.Channels), reader.Channels);
            if (Stopwatch.GetElapsedTime(lastNotice) > TimeSpan.FromMilliseconds(300))
            {
                lastNotice = Stopwatch.GetTimestamp();
                MarkChanged(path, ClipVisualKind.Waveform);
            }
        }

        peaks.Complete();
        if (cached is not null)
        {
            try
            {
                Directory.CreateDirectory(_waveformDirectory!);
                string temp = cached + ".tmp";
                File.WriteAllBytes(temp, peaks.Serialize());
                File.Move(temp, cached, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Publish(string path, WaveformPeaks peaks)
    {
        _waveforms[path] = peaks;
        _waveformOrder.Enqueue(path);
        while (_waveforms.Count > Math.Max(1, _options.MaxWaveformsInMemory) && _waveformOrder.TryDequeue(out string? old))
        {
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
            {
                _waveforms.TryRemove(old, out _);
            }
        }
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

    private sealed class Batch(string path, int height, long[] times, long sequence, string signature)
    {
        public string Path { get; } = path;

        public int Height { get; } = height;

        public long[] Times { get; } = times;

        public long Sequence { get; } = sequence;

        public string Signature { get; } = signature;

        public int Next { get; set; }
    }

    private sealed record OpenReader(string Path, IThumbnailReader Reader, long LastUsed);
}
