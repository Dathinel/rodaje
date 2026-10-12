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
public sealed unsafe class TitleRasterizer : IGeneratedContentCache, IDisposable
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
    private ID2D1Effect* _shadow;
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
            _worker = new Thread(Work) { IsBackground = true, Name = "Rodaje: títulos", Priority = ThreadPriority.BelowNormal };
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
            Com.Release(ref _shadow);
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

    /// <summary>Rasteriza con el candado <see cref="_raster"/> tomado. Nula si no hay nada que dibujar.</summary>
    private TitleTexture? Draw(GeneratedContent content)
    {
        if (content.Kind != GeneratorKinds.Text || _fonts is null)
        {
            return null;
        }

        long started = Stopwatch.GetTimestamp();
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        float size = (float)(style.Size * unit);
        if (!(size > 0) || !float.IsFinite(size))
        {
            return null;
        }

        ResolvedFont font = _fonts.Resolve(style.Font);
        IDWriteTextLayout* layout = null;
        RenderTarget? target = null;
        try
        {
            layout = Layout(content, font, size, out float layoutWidth, out float layoutHeight, out DWRITE_TEXT_METRICS metrics);
            Shape shape = Measure(content, layout, layoutWidth, layoutHeight, metrics);
            double scale = content.RasterScale;
            (int width, int height) = TextureSize(shape, scale);
            bool downscaled = false;
            long bytes = (long)width * height * 4;
            if (bytes > MaxTitleBytes || width > MaxTextureSide || height > MaxTextureSide)
            {
                double fit = Math.Min(Math.Sqrt((double)MaxTitleBytes / bytes),
                    Math.Min((double)MaxTextureSide / width, (double)MaxTextureSide / height));
                scale *= fit * 0.98;
                (width, height) = TextureSize(shape, scale);
                downscaled = true;
            }

            target = CreateTarget(width, height);
            Render(content, layout, shape, target, (float)scale);
            var texture = new TitleTexture(target, width / scale, height / scale, scale, font.Missing, downscaled);
            target = null;
            Rasterized++;
            LastRasterMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return texture;
        }
        finally
        {
            target?.Dispose();
            if (layout != null)
            {
                layout->Release();
            }
        }
    }

    private IDWriteTextLayout* Layout(GeneratedContent content, ResolvedFont font, float size, out float layoutWidth, out float layoutHeight,
        out DWRITE_TEXT_METRICS metrics)
    {
        TextStyle style = content.Style;
        IDWriteTextFormat* format = null;
        IDWriteTextLayout* layout = null;
        IDWriteTextLayout1* spaced = null;
        try
        {
            var weight = (DWRITE_FONT_WEIGHT)Math.Clamp(style.Font.Weight, 1, 999);
            DWRITE_FONT_STYLE slant = style.Font.Italic ? DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC : DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;
            fixed (char* family = font.Family)
            fixed (char* locale = "es-es")
            {
                Check(_dwrite->CreateTextFormat(family, font.Collection, weight, slant, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale,
                    &format), "CreateTextFormat");
            }

            Check(format->SetTextAlignment(style.Align switch
            {
                TextAlign.Left => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
                TextAlign.Right => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
            }), "SetTextAlignment");
            bool wrap = content.BoxWidth > 0;
            Check(format->SetWordWrapping(wrap ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP),
                "SetWordWrapping");
            double lineHeight = double.IsFinite(style.LineHeight) && style.LineHeight > 0 ? style.LineHeight : 1.2;
            float spacing = (float)(lineHeight * size);
            Check(format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD.DWRITE_LINE_SPACING_METHOD_UNIFORM, spacing, spacing * 0.8f), "SetLineSpacing");

            const float Unbounded = 1_000_000;
            string text = content.Text;
            fixed (char* chars = text)
            {
                Check(_dwrite->CreateTextLayout(chars, (uint)text.Length, format, wrap ? (float)content.BoxWidth : Unbounded, Unbounded, &layout),
                    "CreateTextLayout");
            }

            float letterSpacing = (float)(style.LetterSpacing * content.UnitScale);
            if (letterSpacing != 0 && float.IsFinite(letterSpacing) && text.Length > 0
                && layout->QueryInterface(__uuidof<IDWriteTextLayout1>(), (void**)&spaced).SUCCEEDED)
            {
                Check(spaced->SetCharacterSpacing(0, letterSpacing, 0, new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length }),
                    "SetCharacterSpacing");
            }

            DWRITE_TEXT_METRICS measured;
            Check(layout->GetMetrics(&measured), "GetMetrics");
            layoutWidth = wrap ? (float)content.BoxWidth : Math.Max(1, measured.widthIncludingTrailingWhitespace);
            layoutHeight = Math.Max(1, measured.height);
            Check(layout->SetMaxWidth(layoutWidth), "SetMaxWidth");
            Check(layout->SetMaxHeight(layoutHeight), "SetMaxHeight");
            Check(layout->GetMetrics(&measured), "GetMetrics");
            metrics = measured;
            IDWriteTextLayout* result = layout;
            layout = null;
            return result;
        }
        finally
        {
            Com.Release(ref spaced);
            Com.Release(ref layout);
            Com.Release(ref format);
        }
    }

    /// <summary>Rectángulos del título en píxeles de la secuencia, con el origen en la esquina de la caja del texto.</summary>
    private static Shape Measure(GeneratedContent content, IDWriteTextLayout* layout, float layoutWidth, float layoutHeight, DWRITE_TEXT_METRICS metrics)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        DWRITE_OVERHANG_METRICS overhang;
        Check(layout->GetOverhangMetrics(&overhang), "GetOverhangMetrics");
        var ink = new Rect(-overhang.left, -overhang.top, layoutWidth + overhang.right, layoutHeight + overhang.bottom);
        var text = new Rect(metrics.left, metrics.top, metrics.left + metrics.width, metrics.top + metrics.height);
        ink = ink.IsEmpty ? text : ink;
        float stroke = style.Stroke is { Width: > 0 } s && double.IsFinite(s.Width) ? (float)(s.Width * unit) : 0;
        Rect bounds = ink.Union(text);
        Rect outlined = stroke > 0 ? ink.Inflate(stroke * (style.Stroke!.Join == StrokeJoin.Miter ? 2 : 1) + 1) : ink;
        bounds = bounds.Union(outlined);

        Rect? background = null;
        if (style.Background is { } b)
        {
            float px = (float)((b.Padding.Count > 0 ? b.Padding[0] : 0) * unit);
            float py = (float)((b.Padding.Count > 1 ? b.Padding[1] : 0) * unit);
            background = text.Inflate(Math.Max(0, px), Math.Max(0, py));
            bounds = bounds.Union(background.Value);
        }

        float sigma = 0;
        if (style.Shadow is { } shadow)
        {
            sigma = (float)(Math.Clamp(double.IsFinite(shadow.Blur) ? shadow.Blur : 0, 0, 64) * unit / 2);
            Rect cast = outlined.Offset((float)(shadow.Dx * unit), (float)(shadow.Dy * unit)).Inflate((3 * sigma) + 1);
            bounds = bounds.Union(cast);
        }

        var center = new System.Numerics.Vector2(layoutWidth / 2, layoutHeight / 2);
        float halfWidth = Math.Max(Math.Abs(bounds.Left - center.X), Math.Abs(bounds.Right - center.X));
        float halfHeight = Math.Max(Math.Abs(bounds.Top - center.Y), Math.Abs(bounds.Bottom - center.Y));
        return new Shape(center, halfWidth, halfHeight, background, stroke, sigma);
    }

    private static (int Width, int Height) TextureSize(Shape shape, double scale) =>
        (Math.Max(1, (int)Math.Ceiling(2 * shape.HalfWidth * scale) + 2), Math.Max(1, (int)Math.Ceiling(2 * shape.HalfHeight * scale) + 2));

    private RenderTarget CreateTarget(int width, int height)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(título)");
        ID3D11ShaderResourceView* srv = null;
        HRESULT hr = _device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv);
        if (hr.FAILED)
        {
            texture->Release();
            Check(hr, "CreateShaderResourceView(título)");
        }

        return new RenderTarget(texture, null, srv, width, height, desc.Format, RenderTarget.BytesFor(width, height, desc.Format));
    }

    private void Render(GeneratedContent content, IDWriteTextLayout* layout, Shape shape, RenderTarget target, float scale)
    {
        TextStyle style = content.Style;
        double unit = content.UnitScale;
        IDXGISurface* surface = null;
        ID2D1Bitmap1* bitmap = null;
        ID2D1SolidColorBrush* fill = null;
        ID2D1SolidColorBrush* stroke = null;
        ID2D1SolidColorBrush* background = null;
        ID2D1StrokeStyle* strokeStyle = null;
        ID2D1GeometryGroup* outline = null;
        ID2D1Bitmap1* layer = null;
        List<nint> glyphs = [];
        try
        {
            Check(target.Texture->QueryInterface(__uuidof<IDXGISurface>(), (void**)&surface), "QueryInterface(IDXGISurface)");
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            };
            Check(_dc->CreateBitmapFromDxgiSurface(surface, &properties, &bitmap), "CreateBitmapFromDxgiSurface");

            System.Numerics.Matrix3x2 toTexture = System.Numerics.Matrix3x2.CreateScale(scale)
                * System.Numerics.Matrix3x2.CreateTranslation((target.Width / 2f) - (shape.Center.X * scale), (target.Height / 2f) - (shape.Center.Y * scale));
            D2D_MATRIX_3X2_F world = Matrix(toTexture);
            D2D_MATRIX_3X2_F identity = Matrix(System.Numerics.Matrix3x2.Identity);
            fill = Brush(style.Fill.Color);
            if (style.Stroke is { } s && shape.Stroke > 0)
            {
                stroke = Brush(s.Color);
                var strokeProperties = new D2D1_STROKE_STYLE_PROPERTIES
                {
                    startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                    endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                    dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                    lineJoin = s.Join switch
                    {
                        StrokeJoin.Miter => D2D1_LINE_JOIN.D2D1_LINE_JOIN_MITER_OR_BEVEL,
                        StrokeJoin.Bevel => D2D1_LINE_JOIN.D2D1_LINE_JOIN_BEVEL,
                        _ => D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
                    },
                    miterLimit = 2,
                    dashStyle = D2D1_DASH_STYLE.D2D1_DASH_STYLE_SOLID,
                };
                Check(((ID2D1Factory*)_d2d)->CreateStrokeStyle(&strokeProperties, null, 0, &strokeStyle), "CreateStrokeStyle");
                glyphs = GlyphOutlines.Collect(layout, (ID2D1Factory*)_d2d, toTexture);
                if (glyphs.Count > 0)
                {
                    fixed (nint* items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(glyphs))
                    {
                        Check(((ID2D1Factory*)_d2d)->CreateGeometryGroup(D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING, (ID2D1Geometry**)items,
                            (uint)glyphs.Count, &outline), "CreateGeometryGroup");
                    }
                }
            }

            bool shadow = style.Shadow is not null;

            if (style.Background is { } bg && shape.Background is not null)
            {
                background = Brush(bg.Color);
            }

            float strokeWidth = 2 * shape.Stroke * scale;
            var origin = new D2D_POINT_2F(0, 0);
            _gpu.LockContext();
            try
            {
                if (shadow)
                {
                    // Borde y relleno en una capa aparte: la sombra sale de su alfa sin dibujar dos veces el contorno.
                    var layerProperties = properties with { bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET };
                    Check(_dc->CreateBitmap(new D2D_SIZE_U((uint)target.Width, (uint)target.Height), null, 0, &layerProperties, &layer),
                        "CreateBitmap(capa del título)");
                    _dc->SetTarget((ID2D1Image*)layer);
                    _dc->BeginDraw();
                    var none = default(DXGI_RGBA);
                    _dc->Clear(&none);
                    DrawText(layout, outline, stroke, fill, strokeStyle, strokeWidth, &world, &identity);
                    Check(_dc->EndDraw(null, null), "EndDraw(capa del título)");
                }

                _dc->SetTarget((ID2D1Image*)bitmap);
                _dc->BeginDraw();
                var clear = default(DXGI_RGBA);
                _dc->Clear(&clear);
                if (background != null)
                {
                    Rect box = shape.Background!.Value;
                    float radius = (float)(Math.Max(0, style.Background!.Radius) * unit);
                    var rounded = new D2D1_ROUNDED_RECT
                    {
                        rect = new D2D_RECT_F(box.Left, box.Top, box.Right, box.Bottom),
                        radiusX = radius,
                        radiusY = radius,
                    };
                    _dc->SetTransform(&world);
                    _dc->FillRoundedRectangle(&rounded, (ID2D1Brush*)background);
                }

                _dc->SetTransform(&identity);
                if (layer != null)
                {
                    StyleShadow cast = style.Shadow!;
                    var offset = new D2D_POINT_2F((float)(cast.Dx * unit * scale), (float)(cast.Dy * unit * scale));
                    if (_shadow == null)
                    {
                        Guid clsid = ShadowEffect;
                        ID2D1Effect* created;
                        Check(_dc->CreateEffect(&clsid, &created), "CreateEffect(sombra)");
                        _shadow = created;
                    }

                    float sigma = shape.ShadowSigma * scale;
                    DXGI_RGBA color = TitleColor.Parse(cast.Color);
                    _shadow->SetInput(0, (ID2D1Image*)layer, false);
                    Check(_shadow->SetValue(0, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_FLOAT, (byte*)&sigma, sizeof(float)), "SetValue(desviación)");
                    Check(_shadow->SetValue(1, D2D1_PROPERTY_TYPE.D2D1_PROPERTY_TYPE_VECTOR4, (byte*)&color, (uint)sizeof(DXGI_RGBA)), "SetValue(color)");
                    ID2D1Image* blurred;
                    _shadow->GetOutput(&blurred);
                    _dc->DrawImage(blurred, &offset, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
                        D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
                    blurred->Release();
                    _dc->DrawImage((ID2D1Image*)layer, &origin, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR,
                        D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_OVER);
                }
                else
                {
                    DrawText(layout, outline, stroke, fill, strokeStyle, strokeWidth, &world, &identity);
                }

                HRESULT hr = _dc->EndDraw(null, null);
                if (_shadow != null)
                {
                    _shadow->SetInput(0, null, false);
                }

                _dc->SetTarget(null);

                // Direct2D deja su estado en el contexto inmediato, que comparten el compositor y FFmpeg.
                ((ID3D11DeviceContext*)_gpu.NativeContext)->ClearState();
                Check(hr, "EndDraw(título)");
            }
            finally
            {
                _gpu.UnlockContext();
            }
        }
        finally
        {
            GlyphOutlines.Release(glyphs);
            Com.Release(ref layer);
            Com.Release(ref outline);
            Com.Release(ref strokeStyle);
            Com.Release(ref background);
            Com.Release(ref stroke);
            Com.Release(ref fill);
            Com.Release(ref bitmap);
            Com.Release(ref surface);
        }
    }

    /// <summary>El borde (debajo, con el doble de ancho) y el relleno encima, con el destino ya enlazado.</summary>
    private void DrawText(IDWriteTextLayout* layout, ID2D1GeometryGroup* outline, ID2D1SolidColorBrush* stroke, ID2D1SolidColorBrush* fill,
        ID2D1StrokeStyle* strokeStyle, float strokeWidth, D2D_MATRIX_3X2_F* world, D2D_MATRIX_3X2_F* identity)
    {
        if (outline != null)
        {
            _dc->SetTransform(identity);
            _dc->DrawGeometry((ID2D1Geometry*)outline, (ID2D1Brush*)stroke, strokeWidth, strokeStyle);
        }

        _dc->SetTransform(world);
        _dc->DrawTextLayout(new D2D_POINT_2F(0, 0), layout, (ID2D1Brush*)fill,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NO_SNAP | D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
    }

    private ID2D1SolidColorBrush* Brush(string color)
    {
        DXGI_RGBA rgba = TitleColor.Parse(color);
        ID2D1SolidColorBrush* brush;
        Check(_dc->CreateSolidColorBrush(&rgba, null, &brush), "CreateSolidColorBrush");
        return brush;
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

    /// <summary>El bloque medido: su centro, la mitad del rectángulo que cubre todo, el fondo, el borde y la sigma de la sombra.</summary>
    private readonly record struct Shape(System.Numerics.Vector2 Center, float HalfWidth, float HalfHeight, Rect? Background, float Stroke,
        float ShadowSigma);

    private readonly record struct Rect(float Left, float Top, float Right, float Bottom)
    {
        public bool IsEmpty => !(Right > Left && Bottom > Top);

        public Rect Union(Rect other) => IsEmpty ? other : other.IsEmpty ? this
            : new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

        public Rect Inflate(float by) => Inflate(by, by);

        public Rect Inflate(float x, float y) => new(Left - x, Top - y, Right + x, Bottom + y);

        public Rect Offset(float x, float y) => new(Left + x, Top + y, Right + x, Bottom + y);
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
