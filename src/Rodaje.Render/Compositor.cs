using System.Globalization;
using System.Numerics;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;
using Rodaje.Render.Text;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Formato del destino de la composición.</summary>
public enum CompositeFormat
{
    /// <summary>
    /// <c>R16G16B16A16_FLOAT</c>: conserva los 10 bits de P010 y no cuantiza a 8 bits en cada mezcla; deja margen
    /// para mezclar en luz lineal y para efectos. Ocupa el doble de memoria y de ancho de banda.
    /// </summary>
    Rgba16Float,

    /// <summary><c>R8G8B8A8_UNORM</c>: la mitad de memoria; cada capa translúcida redondea a 8 bits.</summary>
    Rgba8Unorm,
}

/// <summary>Resultado de <see cref="Compositor.Render"/>.</summary>
/// <param name="LayersDrawn">Capas dibujadas.</param>
/// <param name="LayersSkipped">Capas sin textura D3D11 utilizable (contenido de CPU, liberado o de otro formato).</param>
/// <param name="EffectsApplied">Efectos, mezclas distintas de la normal y transiciones aplicados.</param>
/// <param name="EffectsSkipped">
/// Los que no se aplicaron en este cuadro: shader todavía sin compilar, pool lleno, código desconocido o repetido.
/// </param>
/// <param name="MissingContent">LUT que todavía cargan o cuyo paquete falta, y transiciones de paquete sin resolver.</param>
/// <param name="PoolBytes">Memoria de los intermedios de efectos al terminar el cuadro.</param>
/// <param name="TitlesPending">
/// Títulos que todavía no estaban rasterizados: el cuadro sale sin ellos (también cuentan en <paramref name="LayersSkipped"/>).
/// </param>
public readonly record struct CompositeResult(int LayersDrawn, int LayersSkipped, int EffectsApplied = 0, int EffectsSkipped = 0,
    int MissingContent = 0, long PoolBytes = 0, int TitlesPending = 0);

/// <summary>
/// Compone un <see cref="CompositionFrame"/> en una textura propia: fondo negro y cada capa de abajo arriba,
/// convertida de Y'CbCr a R'G'B', ajustada al cuadro, transformada y mezclada con "over" premultiplicado.
/// </summary>
/// <remarks>
/// <para>
/// La mezcla se hace sobre valores con la curva del video (no en luz lineal), igual que la mayoría de editores en
/// modo de 8 bits: una opacidad de 0,5 da el promedio de los códigos. El muestreo es bilineal sobre el plano del
/// decodificador, que no tiene mipmaps: al reducir una capa a menos de la mitad aparece aliasing. El presentador sí
/// usa mipmaps de la composición.
/// </para>
/// <para>
/// El tamaño del compositor puede ser menor que el de la secuencia (vista previa a media resolución): las posiciones
/// de <see cref="LayerTransform"/> siguen en píxeles de la secuencia y se escalan al destino.
/// </para>
/// </remarks>
public sealed unsafe partial class Compositor : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly QuadPipeline _pipeline;
    private readonly PlaneViewCache _views;
    private readonly EffectPipeline _effects;
    private readonly RenderTargetPool _pool;
    private readonly LutTextures _luts;
    private readonly LayerRenderer _layers;
    private RenderTarget? _output;
    private ID3D11Texture2D* _staging;
    private TitleRasterizer? _titles;

    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float)
        : this(gpu, width, height, format, TimeProvider.System)
    {
    }

    /// <param name="shaders">Caché de los shaders de efectos; por omisión <see cref="ShaderCache.Default"/>.</param>
    /// <param name="poolCapBytes">Tope de los intermedios; por omisión según la VRAM (<see cref="PoolCapFor"/>).</param>
    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, ShaderCache? shaders, long? poolCapBytes = null)
        : this(gpu, width, height, format, TimeProvider.System, shaders, poolCapBytes)
    {
    }

    internal Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, TimeProvider time,
        ShaderCache? shaders = null, long? poolCapBytes = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        Width = width;
        Height = height;
        Format = format;
        MipLevels = 1 + (int)Math.Log2(Math.Max(width, height));
        _views = new PlaneViewCache(_device, time);
        _pool = new RenderTargetPool(_device, poolCapBytes ?? PoolCapFor(gpu.DedicatedVideoMemory));
        _luts = new LutTextures(_device);
        _pipeline = new QuadPipeline(_device, _context);
        try
        {
            _effects = new EffectPipeline(_device, _context, shaders ?? ShaderCache.Default);
            _layers = new LayerRenderer(gpu, _pipeline, _effects, _views, _pool, _luts);
            CreateTarget();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public GpuDevice Device => _gpu;

    public int Width { get; }

    public int Height { get; }

    public CompositeFormat Format { get; }

    /// <summary>Niveles de mipmap de <see cref="Texture"/>; solo el 0 se dibuja y el presentador genera el resto.</summary>
    public int MipLevels { get; }

    /// <summary>
    /// <c>ID3D11Texture2D*</c> con el último cuadro compuesto, premultiplicado (alfa 1 en todo el cuadro salvo con
    /// <see cref="TransparentBackground"/>). Vale hasta <see cref="Dispose"/>.
    /// </summary>
    public nint Texture => _output is null ? 0 : (nint)_output.Texture;

    /// <summary><c>ID3D11ShaderResourceView*</c> de <see cref="Texture"/>, con todos sus mipmaps.</summary>
    public nint ShaderResourceView => _output is null ? 0 : (nint)_output.Srv;

    /// <summary>
    /// Compone sobre transparente en vez de sobre negro, para exportar con alfa: donde no hay capas el alfa es 0. Las
    /// mezclas por shader suponen un fondo opaco.
    /// </summary>
    public bool TransparentBackground { get; set; }

    /// <summary>Vistas de superficies del decodificador guardadas, para diagnóstico.</summary>
    public int CachedViewCount => _views.Count;

    /// <summary>De dónde salen las LUT. Sin proveedor, una capa con LUT sale sin ella y se cuenta en <see cref="CompositeResult.MissingContent"/>.</summary>
    public IEffectResources? Resources
    {
        get => _layers.Resources;
        set => _layers.Resources = value;
    }

    /// <summary>
    /// Quien rasteriza los títulos (capas con <see cref="LayerFrame.Generated"/>), en este mismo dispositivo. Sin él, los
    /// títulos se saltan. Un título que todavía no está se pide y se cuenta en <see cref="CompositeResult.TitlesPending"/>;
    /// uno con la fuente cambiada por el respaldo cuenta en <see cref="CompositeResult.MissingContent"/>.
    /// </summary>
    /// <exception cref="ArgumentException">El rasterizador es de otro dispositivo.</exception>
    public TitleRasterizer? Titles
    {
        get => _titles;
        set
        {
            if (value is not null && value.Device != _gpu)
            {
                throw new ArgumentException("El rasterizador de títulos es de otro dispositivo.", nameof(value));
            }

            _titles = value;
        }
    }

    /// <summary>Termina cuando todos los shaders de efectos están listos. Mientras tanto los efectos se saltan y se cuentan.</summary>
    public Task ShadersReady => _effects.Ready;

    /// <summary>Tope de memoria de los intermedios de efectos.</summary>
    public long PoolCapBytes => _pool.CapBytes;

    /// <summary>Tope de los intermedios según la VRAM dedicada: 7,5 %, entre 96 y 300 MB.</summary>
    public static long PoolCapFor(long dedicatedVideoMemory) => RenderTargetPool.CapFor(dedicatedVideoMemory);

    /// <summary>Compone <paramref name="frame"/>.</summary>
    /// <param name="geometry">Relación de aspecto del píxel y rotación de cada capa; sin ella, píxeles cuadrados sin rotar.</param>
    /// <exception cref="ArgumentException">Una capa vive en otro dispositivo.</exception>
    public CompositeResult Render(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry = null) => Compose(frame, geometry, strict: false);

    private CompositeResult Compose(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry, bool strict)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_output is null, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frame.Height);
        int drawn;
        var stats = default(FrameStats);
        var destination = new Destination(_output, Width, Height, frame.Width, frame.Height);
        TitleRasterizer? titles = _titles;
        if (titles is not null)
        {
            titles.BeginFrame();
            if (strict)
            {
                // La exportación espera a sus títulos; se rasterizan antes de enlazar nada en el contexto.
                RasterizeTitles(titles, frame.Layers);
            }
        }

        _gpu.LockContext();
        try
        {
            PreviewBypass bypass = strict ? PreviewBypass.None : Bypass;
            _layers.Bypass = bypass;
            _layers.MaskView = strict ? null : MaskView;
            _layers.ShowQualifierMask = !strict && ShowQualifierMask;
            drawn = DrawLayers(frame, geometry, destination, titles, ref stats);
            if (!strict && CompareSplit is { } split && split > 0 && (bypass & PreviewBypass.Grading) == 0)
            {
                DrawCompare(frame, geometry, titles, Math.Clamp(split, 0, 1), bypass);
            }
        }
        finally
        {
            _layers.Bypass = PreviewBypass.None;
            _layers.MaskView = null;
            _layers.ShowQualifierMask = false;
            _layers.EndFrame();
            _pipeline.Unbind();
            _views.EndFrame();
            _pool.EndFrame();
            _gpu.UnlockContext();
        }

        return new CompositeResult(drawn, stats.LayersSkipped, stats.EffectsApplied, stats.EffectsSkipped, stats.MissingContent, _pool.Bytes,
            stats.TitlesPending);
    }

    /// <summary>
    /// Solo vista previa: la parte izquierda del cuadro (hasta esta fracción del ancho) se ve sin color, LUT, viñeta, ruedas
    /// ni curvas, para comparar antes y después. Nulo o 0, sin comparar. La exportación (<see cref="RenderStrict"/>) lo ignora.
    /// </summary>
    public float? CompareSplit { get; set; }

    /// <summary>Solo vista previa: efectos que se dejan fuera en todo el cuadro (el cuentagotas del croma mira la imagen sin él).</summary>
    public PreviewBypass Bypass { get; set; }

    /// <summary>Solo vista previa: una máscara que se ve en gris o superpuesta mientras se edita (la exportación la ignora).</summary>
    public MaskPreview? MaskView { get; set; }
    /// <summary>Solo vista previa: el calificador HSL se ve como su máscara en gris (blanco = elegido). La exportación lo ignora.</summary>
    public bool ShowQualifierMask { get; set; }

    private int DrawLayers(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination,
        TitleRasterizer? titles, ref FrameStats stats) =>
        DrawLayers(frame.Layers, geometry, destination, titles, TransparentBackground, ref stats);

    private int DrawLayers(IReadOnlyList<LayerFrame> layers, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination,
        TitleRasterizer? titles, bool transparent, ref FrameStats stats)
    {
        int drawn = 0;
        RenderTarget target = destination.Target;
        float pixelScale = destination.PixelScale;
        int sequenceWidth = destination.SequenceWidth;
        int sequenceHeight = destination.SequenceHeight;
        _pipeline.Bind(target.Rtv, destination.Width, destination.Height, QuadPipeline.Shader.Layer);
        float* black = stackalloc float[] { 0f, 0f, 0f, transparent ? 0f : 1f };
        _context->ClearRenderTargetView(target.Rtv, black);
        bool bound = true;
        for (int i = 0; i < layers.Count; i++)
        {
            LayerFrame layer = layers[i];
            if (layer.MotionSamples is { Count: >= 2 } samples && layer.Transition is null)
            {
                bound = false;
                if (DrawMotion(layer, samples, geometry, destination, titles, ref stats))
                {
                    drawn++;
                }

                continue;
            }

            if (layer.Generated is { } generated)
            {
                bound = false;
                if (DrawTitle(layer, generated, titles, destination, ref stats))
                {
                    drawn++;
                }

                continue;
            }

            if (layer.Nested is not null || layer.Transition?.Incoming.Nested is not null)
            {
                bound = false;
                if (DrawWithNested(layer, geometry, destination, titles, ref stats))
                {
                    drawn++;
                }

                continue;
            }

            if (_layers.Usable(layer) is not { } payload)
            {
                stats.LayersSkipped++;
                continue;
            }

            if (layer.IsPlain)
            {
                if (!bound)
                {
                    _pipeline.Bind(target.Rtv, destination.Width, destination.Height, QuadPipeline.Shader.Layer);
                    bound = true;
                }

                if (_layers.DrawPlain(layer, payload, geometry?.Invoke(layer) ?? SourceGeometry.Square, sequenceWidth, sequenceHeight, pixelScale))
                {
                    drawn++;
                }

                continue;
            }

            bound = false;
            LayerSource? incoming = layer.Transition is { } transition && _layers.Usable(transition.Incoming) is { } entering
                ? LayerSource.Of(entering)
                : null;
            if (_layers.Draw(layer, LayerSource.Of(payload), incoming, geometry, destination, ref stats))
            {
                drawn++;
            }
        }

        return drawn;
    }

    /// <summary>
    /// Desenfoque de movimiento: la capa se dibuja una vez por muestra del obturador, con su transformación de ese
    /// instante y un peso de 1/n, sumando en un intermedio transparente; el promedio se dibuja después con la mezcla de la
    /// capa. Una capa compuesta se compone una sola vez. Sin memoria para el intermedio, la capa sale sin desenfoque.
    /// </summary>
    private bool DrawMotion(LayerFrame layer, IReadOnlyList<LayerTransform> samples, Func<LayerFrame, SourceGeometry>? geometry,
        in Destination destination, TitleRasterizer? titles, ref FrameStats stats)
    {
        RenderTarget? accumulation = _pool.Rent(destination.Width, destination.Height, LayerRenderer.IntermediateFormat);
        if (accumulation is null)
        {
            stats.EffectsSkipped++;
            return DrawStill(layer, geometry, destination, titles, ref stats);
        }

        RenderTarget? nested = null;
        try
        {
            LayerSource source = default;
            if (layer.Generated is null)
            {
                if (Source(layer, geometry, destination, titles, ref stats, ref nested) is not { } drawable)
                {
                    stats.LayersSkipped++;
                    return false;
                }

                source = drawable;
            }

            float* clear = stackalloc float[] { 0f, 0f, 0f, 0f };
            _context->ClearRenderTargetView(accumulation.Rtv, clear);
            var inner = new Destination(accumulation, destination.Width, destination.Height, destination.SequenceWidth, destination.SequenceHeight);
            float weight = 1f / samples.Count;
            bool any = false;
            for (int i = 0; i < samples.Count; i++)
            {
                var sample = default(FrameStats);
                _layers.BeginMotionSample(samples[i], weight);
                any |= layer.Generated is { } generated
                    ? DrawTitle(layer, generated, titles, inner, ref sample)
                    : _layers.Draw(layer, source, null, geometry, inner, ref sample);
                if (i == 0)
                {
                    stats.LayersSkipped += sample.LayersSkipped;
                    stats.EffectsApplied += sample.EffectsApplied;
                    stats.MissingContent += sample.MissingContent;
                    stats.TitlesPending += sample.TitlesPending;
                }

                stats.EffectsSkipped += sample.EffectsSkipped;
            }

            _layers.EndMotion();
            return any && _layers.DrawImage(new ImageView(accumulation, destination.Width, destination.Height), layer.Blend, destination, ref stats);
        }
        finally
        {
            _layers.EndMotion();
            _pool.Return(nested);
            _pool.Return(accumulation);
        }
    }

    /// <summary>La capa sin desenfoque de movimiento, por el camino que le toca.</summary>
    private bool DrawStill(LayerFrame layer, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination, TitleRasterizer? titles,
        ref FrameStats stats)
    {
        if (layer.Generated is { } generated)
        {
            return DrawTitle(layer, generated, titles, destination, ref stats);
        }

        if (layer.Nested is not null)
        {
            return DrawWithNested(layer, geometry, destination, titles, ref stats);
        }

        if (_layers.Usable(layer) is not { } payload)
        {
            stats.LayersSkipped++;
            return false;
        }

        return _layers.Draw(layer, LayerSource.Of(payload), null, geometry, destination, ref stats);
    }

    /// <summary>
    /// Una capa compuesta, o una transición con una de cada lado: cada capa compuesta se compone antes en un intermedio
    /// transparente del tamaño del destino (recursivo) y se dibuja como una capa más con sus efectos y su transición.
    /// </summary>
    private bool DrawWithNested(LayerFrame layer, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination, TitleRasterizer? titles,
        ref FrameStats stats)
    {
        RenderTarget? outgoing = null;
        RenderTarget? incoming = null;
        try
        {
            LayerSource? source = Source(layer, geometry, destination, titles, ref stats, ref outgoing);
            if (source is not { } drawable)
            {
                stats.LayersSkipped++;
                return false;
            }

            LayerSource? entering = layer.Transition is { } transition ? Source(transition.Incoming, geometry, destination, titles, ref stats, ref incoming) : null;
            return _layers.Draw(layer, drawable, entering, geometry, destination, ref stats);
        }
        finally
        {
            _pool.Return(outgoing);
            _pool.Return(incoming);
        }
    }

    /// <summary>Lo que dibuja la capa: sus planos o, si es compuesta, su intermedio recién compuesto (que queda en <paramref name="rented"/>).</summary>
    private LayerSource? Source(LayerFrame layer, Func<LayerFrame, SourceGeometry>? geometry, in Destination destination, TitleRasterizer? titles,
        ref FrameStats stats, ref RenderTarget? rented)
    {
        if (layer.Nested is not { } nested)
        {
            return _layers.Usable(layer) is { } payload ? LayerSource.Of(payload) : null;
        }

        rented = _pool.Rent(destination.Width, destination.Height, LayerRenderer.IntermediateFormat);
        if (rented is null)
        {
            stats.EffectsSkipped++;
            return null;
        }

        var inner = new Destination(rented, destination.Width, destination.Height, destination.SequenceWidth, destination.SequenceHeight);
        DrawLayers(nested, geometry, inner, titles, transparent: true, ref stats);
        return LayerSource.Of(new ImageView(rented, destination.Width, destination.Height));
    }

    private static void RasterizeTitles(TitleRasterizer titles, IReadOnlyList<LayerFrame> layers)
    {
        foreach (LayerFrame layer in layers)
        {
            if (layer.Generated is { } content)
            {
                titles.Rasterize(content);
            }

            if (layer.Nested is { } nested)
            {
                RasterizeTitles(titles, nested);
            }

            if (layer.Transition?.Incoming is { } incoming)
            {
                RasterizeTitles(titles, [incoming]);
            }
        }
    }

    /// <summary>Compone otra vez sin el color en un intermedio y copia su parte izquierda sobre la salida.</summary>
    private void DrawCompare(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry, TitleRasterizer? titles, float split,
        PreviewBypass bypass)
    {
        int width = Math.Clamp((int)MathF.Round(split * Width), 0, Width);
        RenderTarget? before = width == 0 ? null : _pool.Rent(Width, Height, _output!.Format);
        if (before is null)
        {
            return;
        }

        try
        {
            var stats = default(FrameStats);
            _layers.Bypass = bypass | PreviewBypass.Grading;
            DrawLayers(frame, geometry, new Destination(before, Width, Height, frame.Width, frame.Height), titles, ref stats);
            _pipeline.Unbind();
            var box = new D3D11_BOX { left = 0, top = 0, right = (uint)width, bottom = (uint)Height, front = 0, back = 1 };
            _context->CopySubresourceRegion((ID3D11Resource*)_output!.Texture, 0, 0, 0, 0, (ID3D11Resource*)before.Texture, 0, &box);
        }
        finally
        {
            _pool.Return(before);
        }
    }

    private bool DrawTitle(LayerFrame layer, GeneratedContent content, TitleRasterizer? titles, in Destination destination, ref FrameStats stats)
    {
        if (titles is null || titles.TryGet(content) is not { } title)
        {
            stats.LayersSkipped++;
            if (titles is not null && !titles.IsReady(content))
            {
                stats.TitlesPending++;
            }

            return false;
        }

        if (title.FontMissing)
        {
            stats.MissingContent++;
        }

        return _layers.DrawTitle(layer, title, destination, ref stats);
    }

    /// <summary>
    /// Como <see cref="Render"/>, para la exportación: espera a que compilen los shaders de efectos y, si alguno se saltó
    /// (por ejemplo, con el pool lleno), vacía el pool y compone otra vez. <see cref="CompositeResult.MissingContent"/> lo
    /// decide quien llama (se puede «continuar sin»).
    /// </summary>
    /// <param name="shaderTimeout">Lo más que se espera a los shaders; por omisión, 2 minutos.</param>
    /// <exception cref="ExportException">
    /// Con <see cref="ExportErrorCode.EffectFailed"/>: los shaders fallaron o no llegaron a tiempo, o un efecto sigue sin
    /// aplicarse tras vaciar el pool.
    /// </exception>
    public CompositeResult RenderStrict(CompositionFrame frame, Func<LayerFrame, SourceGeometry>? geometry = null, TimeSpan? shaderTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        WaitForShaders(shaderTimeout ?? TimeSpan.FromMinutes(2));
        CompositeResult result = Compose(frame, geometry, strict: true);
        if (result.EffectsSkipped == 0)
        {
            return result;
        }

        _gpu.LockContext();
        try
        {
            _pool.Trim();
        }
        finally
        {
            _gpu.UnlockContext();
        }

        result = Compose(frame, geometry, strict: true);
        if (result.EffectsSkipped > 0)
        {
            throw new ExportException(ExportErrorCode.EffectFailed, string.Create(CultureInfo.InvariantCulture,
                $"{result.EffectsSkipped} efecto(s) no se pudieron aplicar ni tras liberar la memoria de efectos (tope {PoolCapBytes / (1024 * 1024)} MB): un efecto desconocido o repetido, o falta memoria de video."));
        }

        return result;
    }

    /// <summary>
    /// Copia el último cuadro compuesto a la CPU en RGBA de coma flotante (media precisión en Rgba16Float), fila tras fila:
    /// para medir lo que una lectura de 8 bits redondea (bandas en 10 bits).
    /// </summary>
    public float[] ReadbackRgbaFloat()
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        var pixels = new float[Width * Height * 4];
        _gpu.LockContext();
        try
        {
            ID3D11Texture2D* staging = Staging();
            _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)_output.Texture, 0, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura)");
            try
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* row = (byte*)mapped.pData + ((nuint)y * mapped.RowPitch);
                    Span<float> target = pixels.AsSpan(y * Width * 4, Width * 4);
                    if (Format == CompositeFormat.Rgba8Unorm)
                    {
                        for (int i = 0; i < target.Length; i++)
                        {
                            target[i] = row[i] / 255f;
                        }

                        continue;
                    }

                    var halves = new ReadOnlySpan<Half>(row, Width * 4);
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = (float)halves[i];
                    }
                }
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        return pixels;
    }

    /// <summary>Copia el último cuadro compuesto a la CPU en RGBA de 8 bits, fila tras fila (para pruebas y miniaturas).</summary>
    public byte[] ReadbackRgba()
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        var pixels = new byte[Width * Height * 4];
        _gpu.LockContext();
        try
        {
            ID3D11Texture2D* staging = Staging();
            _context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)_output.Texture, 0, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura)");
            try
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* row = (byte*)mapped.pData + ((nuint)y * mapped.RowPitch);
                    Span<byte> target = pixels.AsSpan(y * Width * 4, Width * 4);
                    if (Format == CompositeFormat.Rgba8Unorm)
                    {
                        new ReadOnlySpan<byte>(row, Width * 4).CopyTo(target);
                        continue;
                    }

                    var halves = new ReadOnlySpan<Half>(row, Width * 4);
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = (byte)Math.Round(Math.Clamp((float)halves[i], 0f, 1f) * 255f);
                    }
                }
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        return pixels;
    }

    /// <summary>
    /// Suelta ya las vistas de decodificadores cerrados y las que llevan tiempo sin usarse. <see cref="Render"/> lo hace
    /// solo; en pausa, cuando no se compone, quien dibuja lo llama de vez en cuando para no retener esos pools.
    /// </summary>
    public void TrimCachedViews()
    {
        _gpu.LockContext();
        try
        {
            _views.Trim();
            _pool.Trim();
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    /// <summary>Suelta las vistas guardadas de superficies del decodificador (por ejemplo, al cerrar una fuente).</summary>
    public void ReleaseCachedViews()
    {
        _gpu.LockContext();
        try
        {
            _views.Clear();
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    public void Dispose()
    {
        _views.Dispose();
        _pool.Dispose();
        _luts.Dispose();
        _layers?.Dispose();
        _effects?.Dispose();
        _pipeline?.Dispose();
        Com.Release(ref _staging);
        _output?.Dispose();
        _output = null;
    }

    private void WaitForShaders(TimeSpan timeout)
    {
        bool ready;
        try
        {
            ready = ShadersReady.Wait(timeout);
        }
        catch (AggregateException error)
        {
            throw new ExportException(ExportErrorCode.EffectFailed, "No se pudieron compilar los shaders de efectos.", error.InnerException ?? error);
        }

        if (!ready)
        {
            throw new ExportException(ExportErrorCode.EffectFailed,
                string.Create(CultureInfo.InvariantCulture, $"Los shaders de efectos no terminaron de compilar en {timeout.TotalSeconds:0} s."));
        }
    }

    private void CreateTarget()
    {
        DXGI_FORMAT format = Format == CompositeFormat.Rgba8Unorm
            ? DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM
            : DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = (uint)MipLevels,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS,
        };
        ID3D11Texture2D* target;
        Check(_device->CreateTexture2D(&desc, null, &target), "CreateTexture2D(composición)");
        ID3D11RenderTargetView* rtv = null;
        ID3D11ShaderResourceView* srv = null;
        try
        {
            var rtvDesc = new D3D11_RENDER_TARGET_VIEW_DESC { Format = format, ViewDimension = D3D11_RTV_DIMENSION.D3D11_RTV_DIMENSION_TEXTURE2D };
            rtvDesc.Texture2D.MipSlice = 0;
            Check(_device->CreateRenderTargetView((ID3D11Resource*)target, &rtvDesc, &rtv), "CreateRenderTargetView(composición)");
            Check(_device->CreateShaderResourceView((ID3D11Resource*)target, null, &srv), "CreateShaderResourceView(composición)");
        }
        catch
        {
            Com.Release(ref rtv);
            Com.Release(ref target);
            throw;
        }

        _output = new RenderTarget(target, rtv, srv, Width, Height, format, RenderTarget.BytesFor(Width, Height, format));
    }

    private ID3D11Texture2D* Staging()
    {
        if (_staging != null)
        {
            return _staging;
        }

        D3D11_TEXTURE2D_DESC desc;
        _output!.Texture->GetDesc(&desc);
        desc.MipLevels = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(_device->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(lectura)");
        _staging = staging;
        return staging;
    }
}
