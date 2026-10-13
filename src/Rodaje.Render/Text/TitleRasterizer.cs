using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Rodaje.Render.Text;

/// <summary>Opciones de <see cref="TitleRasterizer"/>.</summary>
public sealed record TitleRasterizerOptions
{
    /// <summary>Memoria de todas las texturas de títulos; por omisión la misma que el pool de intermedios (7,5 % de la VRAM).</summary>
    public long? CacheCapBytes { get; init; }

    /// <summary>Tope de un título; por omisión el 25 % del pool de intermedios. Si no cabe, se rasteriza más chico.</summary>
    public long? MaxTitleBytes { get; init; }

    /// <summary>Rasteriza lo que pide <see cref="TitleRasterizer.Prepare"/> en un hilo propio; sin él, solo bajo pedido.</summary>
    public bool BackgroundThread { get; init; } = true;

    /// <summary>
    /// Prioridad del hilo propio; por omisión normal. Antes iba debajo de lo normal, pero el hilo solo corre cuando alguien
    /// espera un título (en reposo duerme en <see cref="Monitor.Wait(object)"/> y no gasta CPU): la vista previa no da por
    /// completa la composición tras un seek hasta que llega, y reproduciendo lo cuenta como tarde. Además rasteriza con el
    /// candado del contexto de la GPU, el mismo que necesita el hilo de render (por encima de lo normal). Con la CPU llena
    /// de hilos normales, uno de prioridad baja solo corre cuando Windows lo rescata tras unos segundos sin turno, así que
    /// un título nuevo tardaba segundos en verse y, si lo desalojaban con el candado tomado, frenaba también al render
    /// (en el runner de Windows saturado, un título tardó más de un minuto). Cada título son unos milisegundos de CPU.
    /// </summary>
    public ThreadPriority WorkerPriority { get; init; } = ThreadPriority.Normal;
}

/// <summary>Una fuente pedida por un título que no está en el equipo: se dibuja con <see cref="UsedFamily"/>.</summary>
public sealed record MissingFont(string Family, string? Source, string ClipId, string UsedFamily);

/// <summary>
/// Rasteriza títulos (<see cref="GeneratedContent"/>) con DirectWrite y Direct2D sobre el dispositivo de un
/// <see cref="GpuDevice"/>: relleno, borde, sombra, fondo, alineación y saltos de línea, a una textura BGRA premultiplicada
/// del tamaño de la secuencia × la escala máxima del título. Hay uno por dispositivo (la vista previa y la exportación
/// tienen el suyo), con su colección privada de fuentes y su caché de texturas, fuera del soltado del pool de efectos.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Prepare"/> nunca espera: encola y lo rasteriza el hilo propio (con el candado del contexto, unos pocos
/// milisegundos); <see cref="Changed"/> avisa cuando hay uno nuevo. <see cref="Rasterize"/> lo hace en el acto, para la
/// exportación. Las texturas que salen de la caché se sueltan al empezar el cuadro siguiente del compositor: un mismo
/// rasterizador sirve a un solo compositor a la vez.
/// </para>
/// <para>
/// El borde sale del contorno de los glifos (<see cref="GlyphOutlines"/>, una tabla COM propia apta para AOT) dibujado
/// con el doble del ancho debajo del relleno; la sombra, del texto (y el borde) en su color con un desenfoque gaussiano
/// de Direct2D (sigma = desenfoque / 2). Antialias en escala de grises, sin ajuste a píxel.
/// </para>
/// </remarks>
public sealed unsafe partial class TitleRasterizer : IGeneratedContentCache, IDisposable
{
    /// <summary>Un título no pasa de esto por lado aunque quepa en bytes (el límite de Direct3D 11).</summary>
    public const int MaxTextureSide = 16384;

    private static readonly Guid ShadowEffect = new(0xc67ea361, 0x1863, 0x4e69, 0x89, 0xdb, 0x69, 0x5d, 0x3e, 0x9a, 0x5b, 0x6b);

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly object _gate = new();
    private readonly Lock _raster = new();
    private readonly Dictionary<GeneratedContent, Entry> _cache = [];
    private readonly HashSet<GeneratedContent> _queued = [];
    private readonly Queue<GeneratedContent> _queue = new();
    private readonly List<RenderTarget> _retired = [];
    private readonly Thread? _worker;
    private ID2D1Factory1* _d2d;
    private ID2D1Device* _d2dDevice;
    private ID2D1DeviceContext* _dc;
    private readonly nint[] _shadows = new nint[1 + Rodaje.Core.Model.ModelValidator.MaxExtraShadows];
    private IDWriteFactory5* _dwrite;
    private TitleFonts? _fonts;
    private long _frame;
    private long _bytes;
    private bool _disposed;

    public TitleRasterizer(GpuDevice gpu, TitleRasterizerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        options ??= new TitleRasterizerOptions();
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        long pool = RenderTargetPool.CapFor(gpu.DedicatedVideoMemory);
        CacheCapBytes = options.CacheCapBytes ?? pool;
        MaxTitleBytes = options.MaxTitleBytes ?? pool / 4;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CacheCapBytes, nameof(options.CacheCapBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTitleBytes, nameof(options.MaxTitleBytes));
        try
        {
            CreateFactories();
        }
        catch
        {
            Dispose();
            throw;
        }

        if (options.BackgroundThread)
        {
            _worker = new Thread(Work) { IsBackground = true, Name = "Rodaje: títulos", Priority = options.WorkerPriority };
            _worker.Start();
        }
    }

    /// <summary>Terminó de rasterizar un título pedido con <see cref="Prepare"/>: en pausa hay que volver a componer. Llega desde otro hilo.</summary>
    public event EventHandler? Changed;

    public GpuDevice Device => _gpu;

    public long CacheCapBytes { get; }

    public long MaxTitleBytes { get; }

    /// <summary>Memoria de las texturas en caché.</summary>
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

    /// <summary>Títulos rasterizados desde que se creó.</summary>
    public long Rasterized { get; private set; }

    /// <summary>Lo que tardó el último rasterizado, en milisegundos (CPU y GPU hasta <c>EndDraw</c>).</summary>
    public double LastRasterMilliseconds { get; private set; }

    /// <summary>Títulos que no se pudieron rasterizar (se ven sin título) y el último motivo.</summary>
    public int Failures { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Registra los archivos de fuente de un paquete en la colección privada (no en Windows). Devuelve los que DirectWrite
    /// rechazó o que pasan de 20 MB: su familia queda con el respaldo. Vacía la caché, porque un título puede cambiar de
    /// fuente.
    /// </summary>
    public IReadOnlyList<string> RegisterFonts(IEnumerable<string> fontFiles)
    {
        ArgumentNullException.ThrowIfNull(fontFiles);
        List<string> rejected;
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

    /// <summary>
    /// La pasada que solo resuelve fuentes: las de <paramref name="contents"/> que no están (una por familia y clip).
    /// La exportación aplica con esto su <c>MissingContentPolicy</c>; la vista previa avisa en el inspector. No espera a
    /// que termine el rasterizado en curso: solo pregunta a las colecciones de fuentes.
    /// </summary>
    public IReadOnlyList<MissingFont> FindMissingFonts(IEnumerable<GeneratedContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        TitleFonts fonts = Volatile.Read(ref _fonts) ?? throw new ObjectDisposedException(nameof(TitleRasterizer));
        var missing = new List<MissingFont>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (GeneratedContent content in contents)
        {
            if (content.Kind != GeneratorKinds.Text)
            {
                continue;
            }

            StyleFont font = content.Style.Font;
            ResolvedFont resolved = fonts.Resolve(font);
            if (resolved.Missing && seen.Add(font.Family + "" + content.ClipId))
            {
                missing.Add(new MissingFont(font.Family, font.Source, content.ClipId, resolved.Family));
            }
        }

        return missing;
    }

    /// <summary>Para las pruebas: el candado que toma el rasterizado.</summary>
    internal Lock RasterLock => _raster;

    /// <summary>Para las pruebas: la prioridad con que corre el hilo propio; nula si no hay.</summary>
    internal ThreadPriority? WorkerThreadPriority => _worker?.Priority;

    /// <summary>Verdadero si ya está rasterizado (o no se puede: no hay nada que esperar); si no, lo encola.</summary>
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

    /// <summary>
    /// La textura si ya está; si no, la pide (como <see cref="Prepare"/>) y devuelve nula sin esperar. También nula si el
    /// título no se puede dibujar.
    /// </summary>
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

    /// <summary>Si ya está, o se rasterizó y no se pudo dibujar: no hace falta esperar más.</summary>
    public bool IsReady(GeneratedContent content)
    {
        lock (_gate)
        {
            return _cache.ContainsKey(content);
        }
    }

    /// <summary>Rasteriza en el acto si no está en caché (para la exportación, que espera). Nula si no se puede dibujar.</summary>
    /// <exception cref="RenderException">Direct2D falló (por ejemplo, el dispositivo se perdió).</exception>
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

    /// <summary>Lo llama el compositor al empezar cada cuadro: suelta las texturas que salieron de la caché antes.</summary>
    internal void BeginFrame()
    {
        lock (_gate)
        {
            _frame++;
            ReleaseRetired();
        }
    }

    /// <summary>Suelta las texturas que no se usaron en los últimos cuadros (en pausa, por ejemplo).</summary>
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
            if (_disposed && _d2d == null)
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
            ReleaseShadows();
            Com.Release(ref _dc);
            Com.Release(ref _d2dDevice);
            Com.Release(ref _d2d);
            Com.Release(ref _dwrite);
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
                    catch (Exception error) when (error is RenderException or OutOfMemoryException or ArgumentException)
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

    /// <summary>Saca de la caché la textura usada hace más tiempo, salvo las de este cuadro y el anterior.</summary>
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

    private void CreateFactories()
    {
        var options = new D2D1_FACTORY_OPTIONS { debugLevel = D2D1_DEBUG_LEVEL.D2D1_DEBUG_LEVEL_NONE };
        ID2D1Factory1* factory;
        Check(D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, __uuidof<ID2D1Factory1>(), &options, (void**)&factory),
            "D2D1CreateFactory");
        _d2d = factory;

        IDXGIDevice* dxgi = null;
        try
        {
            Check(_device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgi), "QueryInterface(IDXGIDevice)");
            ID2D1Device* device;
            Check(_d2d->CreateDevice(dxgi, &device), "ID2D1Factory1::CreateDevice");
            _d2dDevice = device;
        }
        finally
        {
            Com.Release(ref dxgi);
        }

        ID2D1DeviceContext* dc;
        Check(_d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc), "CreateDeviceContext");
        _dc = dc;
        _dc->SetUnitMode(D2D1_UNIT_MODE.D2D1_UNIT_MODE_PIXELS);
        _dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

        IDWriteFactory5* dwrite;
        Check(DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, __uuidof<IDWriteFactory5>(), (IUnknown**)&dwrite),
            "DWriteCreateFactory");
        _dwrite = dwrite;
        _fonts = new TitleFonts(dwrite);
    }

    private static D2D_MATRIX_3X2_F Matrix(System.Numerics.Matrix3x2 m)
    {
        D2D_MATRIX_3X2_F result = default;
        result.m11 = m.M11;
        result.m12 = m.M12;
        result.m21 = m.M21;
        result.m22 = m.M22;
        result.dx = m.M31;
        result.dy = m.M32;
        return result;
    }

    private sealed class Entry(TitleTexture? texture)
    {
        public TitleTexture? Texture { get; } = texture;

        public long LastUsed { get; set; }
    }

    internal readonly record struct Rect(float Left, float Top, float Right, float Bottom)
    {
        public bool IsEmpty => !(Right > Left && Bottom > Top);

        public Rect Union(Rect other) => IsEmpty ? other : other.IsEmpty ? this
            : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

        public Rect Inflate(float by) => Inflate(by, by);

        public Rect Inflate(float x, float y) => new(Left - x, Top - y, Right + x, Bottom + y);

        public Rect Offset(float x, float y) => new(Left + x, Top + y, Right + x, Bottom + y);

        public float Width => Right - Left;

        public float Height => Bottom - Top;

        public System.Numerics.Vector2 Center => new((Left + Right) / 2, (Top + Bottom) / 2);
    }
}

/// <summary>Colores del estilo: <c>#RRGGBB</c> o <c>#RRGGBBAA</c>, en valores con gamma (sin premultiplicar).</summary>
internal static class TitleColor
{
    public static DXGI_RGBA Parse(string? color)
    {
        if (color is not { Length: 7 or 9 } || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return new DXGI_RGBA { r = 1, g = 1, b = 1, a = 1 };
        }

        if (color.Length == 7)
        {
            value = (value << 8) | 0xFF;
        }

        return new DXGI_RGBA
        {
            r = ((value >> 24) & 0xFF) / 255f,
            g = ((value >> 16) & 0xFF) / 255f,
            b = ((value >> 8) & 0xFF) / 255f,
            a = (value & 0xFF) / 255f,
        };
    }
}
