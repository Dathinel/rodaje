using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Text;

namespace Rodaje.Render.Text;

public sealed partial class SkiaTitleRasterizer : ITitleRasterizer
{
    private readonly IGpu _gpu;
    private readonly object _gate = new();
    private readonly Lock _raster = new();
    private readonly Dictionary<GeneratedContent, Entry> _cache = [];
    private readonly HashSet<GeneratedContent> _queued = [];
    private readonly Queue<GeneratedContent> _queue = new();
    private readonly List<RenderTarget> _retired = [];
    private readonly Thread? _worker;
    private TitleFontSet? _fonts = new();
    private long _frame;
    private long _bytes;
    private bool _disposed;

    public SkiaTitleRasterizer(IGpu gpu, TitleRasterizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        options ??= new TitleRasterizerOptions();
        _gpu = gpu;
        long pool = RenderTargetPool.CapFor(0);
        CacheCapBytes = options.CacheCapBytes ?? pool;
        MaxTitleBytes = options.MaxTitleBytes ?? pool / 4;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CacheCapBytes, nameof(options.CacheCapBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTitleBytes, nameof(options.MaxTitleBytes));
        if (options.BackgroundThread)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Rodaje: Skia", Priority = options.WorkerPriority };
            _worker.Start();
        }
    }

    public event EventHandler? Changed;

    public IGpu Engine => _gpu;

    public long CacheCapBytes { get; }

    public long MaxTitleBytes { get; }

    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count(e => e.Value.Texture is not null);
            }
        }
    }

    public long Rasterized { get; private set; }

    public double LastRasterMilliseconds { get; private set; }

    public int Failures { get; private set; }

    public string? LastError { get; private set; }

    public IReadOnlyList<string> RegisterFonts(IEnumerable<string> fontFiles)
    {
        ArgumentNullException.ThrowIfNull(fontFiles);
        IReadOnlyList<string> rejected;
        lock (_raster)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            rejected = _fonts!.Register(fontFiles);
        }

        lock (_gate)
        {
            foreach (Entry entry in _cache.Values)
            {
                Retire(entry);
            }

            _cache.Clear();
        }

        return rejected;
    }

    public IReadOnlyList<MissingFont> FindMissingFonts(IEnumerable<GeneratedContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        TitleFontSet fonts = Volatile.Read(ref _fonts) ?? throw new ObjectDisposedException(nameof(SkiaTitleRasterizer));
        var missing = new List<MissingFont>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeneratedContent content in contents)
        {
            if (content.Kind != GeneratorKinds.Text)
            {
                continue;
            }

            StyleFont font = content.Style.Font;
            var resolved = fonts.Resolve(font);
            if (resolved.Missing && seen.Add(font.Family + "" + content.ClipId))
            {
                missing.Add(new MissingFont(font.Family, font.Source, content.ClipId, resolved.Font.Family));
            }
        }

        return missing;
    }

    internal Lock RasterLock => _raster;

    internal ThreadPriority? WorkerThreadPriority => _worker?.Priority;

    public bool Prepare(GeneratedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            if (_disposed)
            {
                return true;
            }

            if (_cache.TryGetValue(content, out Entry? entry))
            {
                entry.LastUsed = _frame;
                return true;
            }

            if (_worker is not null && _queued.Add(content))
            {
                _queue.Enqueue(content);
                Monitor.PulseAll(_gate);
            }

            return false;
        }
    }

    public TitleTexture? TryGet(GeneratedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            if (_cache.TryGetValue(content, out Entry? entry))
            {
                entry.LastUsed = _frame;
                return entry.Texture;
            }
        }

        Prepare(content);
        return null;
    }

    public bool IsReady(GeneratedContent content)
    {
        lock (_gate)
        {
            return _cache.ContainsKey(content);
        }
    }

    public TitleTexture? Rasterize(GeneratedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(content, out Entry? cached))
            {
                cached.LastUsed = _frame;
                return cached.Texture;
            }
        }

        lock (_raster)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(content, out Entry? done))
                {
                    done.LastUsed = _frame;
                    return done.Texture;
                }
            }

            TitleTexture? texture = Draw(content);
            Store(content, texture);
            return texture;
        }
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            _frame++;
            ReleaseRetired();
        }
    }

    public void Trim()
    {
        lock (_gate)
        {
            foreach ((GeneratedContent content, Entry entry) in _cache.Where(e => e.Value.LastUsed < _frame - 1).ToList())
            {
                Retire(entry);
                _cache.Remove(content);
            }

            ReleaseRetired();
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
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }

        _worker?.Join();
        lock (_raster)
        {
            lock (_gate)
            {
                foreach (Entry entry in _cache.Values)
                {
                    Retire(entry);
                }

                _cache.Clear();
                ReleaseRetired();
            }

            _fonts?.Dispose();
            _fonts = null;

        }
    }

    private void Work()
    {
        while (true)
        {
            GeneratedContent content;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_disposed)
                {
                    Monitor.Wait(_gate);
                }

                if (_disposed)
                {
                    return;
                }

                content = _queue.Dequeue();
            }

            bool drawn = false;
            lock (_raster)
            {
                bool cached;
                lock (_gate)
                {
                    cached = _disposed || _cache.ContainsKey(content);
                }

                if (!cached)
                {
                    TitleTexture? texture = null;
                    try
                    {
                        texture = Draw(content);
                    }
                    catch (Exception error) when (error is RenderException or OutOfMemoryException or ArgumentException or InvalidOperationException)
                    {
                        Failures++;
                        LastError = error.Message;
                    }

                    Store(content, texture);
                    drawn = true;
                }
            }

            lock (_gate)
            {
                _queued.Remove(content);
            }

            if (drawn)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Store(GeneratedContent content, TitleTexture? texture)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                if (texture is not null)
                {
                    _retired.Add(texture.Target);
                    ReleaseRetired();
                }

                return;
            }

            long bytes = texture?.Bytes ?? 0;
            while (_bytes + bytes > CacheCapBytes && EvictOldest())
            {
            }

            _cache[content] = new Entry(texture) { LastUsed = _frame };
            _bytes += bytes;
        }
    }

    private bool EvictOldest()
    {
        GeneratedContent? oldest = null;
        long oldestUse = long.MaxValue;
        foreach ((GeneratedContent content, Entry entry) in _cache)
        {
            if (entry.Texture is not null && entry.LastUsed < _frame - 1 && entry.LastUsed < oldestUse)
            {
                oldest = content;
                oldestUse = entry.LastUsed;
            }
        }

        if (oldest is null)
        {
            return false;
        }

        Retire(_cache[oldest]);
        _cache.Remove(oldest);
        return true;
    }

    private void Retire(Entry entry)
    {
        if (entry.Texture is { } texture)
        {
            _retired.Add(texture.Target);
            _bytes -= texture.Bytes;
        }
    }

    private void ReleaseRetired()
    {
        foreach (RenderTarget target in _retired)
        {
            target.Dispose();
        }

        _retired.Clear();
    }

    private sealed class Entry(TitleTexture? texture)
    {
        public TitleTexture? Texture { get; } = texture;
        public long LastUsed { get; set; }
    }
}
