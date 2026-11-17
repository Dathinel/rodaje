using System.Globalization;
using System.Numerics;
using Rodaje.Core.Model;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using Rodaje.Render.Effects;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Interpolation;
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
    private readonly GpuDevice? _gpu;
    private readonly IGpu _engine;
    private readonly QuadPipeline _pipeline;
    private readonly GpuSurfaceCache _views;
    private readonly EffectPipeline _effects;
    private readonly RenderTargetPool _pool;
    private readonly LutTextures _luts;
    private readonly LayerRenderer _layers;
    private readonly FrameInterpolator? _interpolator;
    private RenderTarget? _output;
    private GpuReadback? _readback;
    private TitleRasterizer? _titles;

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float)
        : this(gpu, width, height, format, TimeProvider.System)
    {
    }

    /// <param name="shaders">Caché de los shaders de efectos; por omisión <see cref="ShaderCache.Default"/>.</param>
    /// <param name="poolCapBytes">Tope de los intermedios; por omisión según la VRAM (<see cref="PoolCapFor"/>).</param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, ShaderCache? shaders, long? poolCapBytes = null)
        : this(gpu, width, height, format, TimeProvider.System, shaders, poolCapBytes)
    {
    }

    /// <summary>
    /// Sobre cualquier motor de <see cref="IGpu"/>. En uno que no es D3D11 no hay interpolación de cuadros ni títulos (llegan con las
    /// superficies Vulkan y el dibujo de títulos en Unix): esas capas salen sin interpolar o se saltan.
    /// </summary>
    public Compositor(IGpu gpu, int width, int height, CompositeFormat format = CompositeFormat.Rgba16Float, ShaderCache? shaders = null,
        long? poolCapBytes = null)
        : this(gpu, DeviceOf(gpu), width, height, format, TimeProvider.System, shaders, poolCapBytes)
    {
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    internal Compositor(GpuDevice gpu, int width, int height, CompositeFormat format, TimeProvider time,
        ShaderCache? shaders = null, long? poolCapBytes = null)
        : this(D3D11Gpu.For(gpu ?? throw new ArgumentNullException(nameof(gpu))), gpu, width, height, format, time, shaders, poolCapBytes)
    {
    }

    private Compositor(IGpu engine, GpuDevice? gpu, int width, int height, CompositeFormat format, TimeProvider time,
        ShaderCache? shaders, long? poolCapBytes)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, engine.Caps.MaxTextureSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, engine.Caps.MaxTextureSize);
        _gpu = gpu;
        _engine = engine;
        Width = width;
        Height = height;
        Format = format;
        MipLevels = 1 + (int)Math.Log2(Math.Max(width, height));
        _views = _engine.CreateSurfaceCache(time);
        _pool = new RenderTargetPool(_engine, poolCapBytes ?? PoolCapFor(gpu is not null && OperatingSystem.IsWindowsVersionAtLeast(7) ? gpu.DedicatedVideoMemory : 0));
        _luts = new LutTextures(_engine);
        _pipeline = new QuadPipeline(_engine);
        try
        {
            _effects = new EffectPipeline(_pipeline, shaders ?? ShaderCache.Default);
            _layers = new LayerRenderer(gpu, _pipeline, _effects, _views, _pool, _luts);
            if (OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is not null)
            {
                _interpolator = new FrameInterpolator(gpu, ((D3D11SurfaceCache)_views).Views, _pool, shaders ?? ShaderCache.Default);
            }

            CreateTarget();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>El dispositivo D3D11 del compositor (solo con el motor D3D11).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public GpuDevice Device => _gpu ?? throw new InvalidOperationException("El compositor no usa el motor D3D11.");

    /// <summary>El motor de la GPU.</summary>
    public IGpu Engine => _engine;

    private static GpuDevice? DeviceOf(IGpu gpu) => OperatingSystem.IsWindowsVersionAtLeast(7) && gpu is D3D11Gpu d3d ? d3d.Device : null;

    public int Width { get; }

    public int Height { get; }

    public CompositeFormat Format { get; }

    /// <summary>Niveles de mipmap de <see cref="Texture"/>; solo el 0 se dibuja y el presentador genera el resto.</summary>
    public int MipLevels { get; }

    /// <summary>
    /// <c>ID3D11Texture2D*</c> con el último cuadro compuesto, premultiplicado (alfa 1 en todo el cuadro salvo con
    /// <see cref="TransparentBackground"/>). Vale hasta <see cref="Dispose"/>.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public nint Texture => _output is null ? 0 : (nint)_output.Texture;

    /// <summary>El último cuadro compuesto para <see cref="IGpu"/> (la misma textura que <see cref="Texture"/>).</summary>
    internal GpuTexture? Output => _output?.Gpu;

    /// <summary><c>ID3D11ShaderResourceView*</c> de <see cref="Texture"/>, con todos sus mipmaps.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public nint ShaderResourceView => _output is null ? 0 : (nint)_output.Srv;

    /// <summary>
    /// Compone sobre transparente en vez de sobre negro, para exportar con alfa: donde no hay capas el alfa es 0. Las
    /// mezclas por shader suponen un fondo opaco.
    /// </summary>
    public bool TransparentBackground { get; set; }

    /// <summary>
    /// Espacio de color de la secuencia. En HDR la composición es luz lineal BT.2020 con 1,0 = 203 nits y sin recorte por arriba
    /// (<see cref="QuadPipeline.HdrMode"/>; D-055): cada capa entra por su curva, la mezcla y los efectos van en lineal, y el
    /// presentador o el conversor de exportación la codifican a PQ, HLG, scRGB o SDR. En SDR nada cambia (D-016, D-052).
    /// </summary>
    public SequenceColorSpace ColorSpace { get; set; }

    /// <summary>Vistas de superficies del decodificador guardadas, para diagnóstico.</summary>
    public int CachedViewCount => _views.Count;

    internal int InterpolationTextureCount => OperatingSystem.IsWindowsVersionAtLeast(7) ? _interpolator?.TextureCount ?? 0 : 0;

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
        [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
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
        // Los títulos se dibujan con Direct2D: en otro motor no hay rasterizador y esas capas se saltan.
        TitleRasterizer? titles = OperatingSystem.IsWindowsVersionAtLeast(7) ? _titles : null;
        if (titles is not null && OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            titles.BeginFrame();
            if (strict)
            {
                // La exportación espera a sus títulos; se rasterizan antes de enlazar nada en el contexto.
                RasterizeTitles(titles, frame.Layers);
            }
        }

        IGpuFrame gpuFrame = _engine.BeginFrame();
        _pipeline.Frame = gpuFrame;
        try
        {
            _pipeline.HdrMode = ColorSpace != SequenceColorSpace.Sdr709 ? 1 : 0;
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
            if (OperatingSystem.IsWindowsVersionAtLeast(7))
            {
                _interpolator?.EndFrame();
            }

            _views.EndFrame();
            _pool.EndFrame();
            _pipeline.Frame = null;
            gpuFrame.Dispose();
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
        _pipeline.Bind(target.Gpu, destination.Width, destination.Height, QuadPipeline.Shader.Layer);
        _pipeline.CurrentFrame.Clear(target.Gpu, new GpuColor(0f, 0f, 0f, transparent ? 0f : 1f));
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

            if (Usable(layer) is not { } payload)
            {
                stats.LayersSkipped++;
                continue;
            }

            if (layer.IsPlain)
            {
                if (!bound)
                {
                    _pipeline.Bind(target.Gpu, destination.Width, destination.Height, QuadPipeline.Shader.Layer);
                    bound = true;
                }

                if (_layers.DrawPlain(layer, payload, geometry?.Invoke(layer) ?? SourceGeometry.Square, sequenceWidth, sequenceHeight, pixelScale))
                {
                    drawn++;
                }

                continue;
            }

            bound = false;
            LayerSource? incoming = layer.Transition is { } transition && Usable(transition.Incoming) is { } entering
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

            _pipeline.CurrentFrame.Clear(accumulation.Gpu, GpuColor.TransparentBlack);
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

        if (Usable(layer) is not { } payload)
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
            return Usable(layer) is { } payload ? LayerSource.Of(payload) : null;
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

    private IDecoderSurface? Usable(LayerFrame layer)
    {
        IDecoderSurface? payload = _layers.Usable(layer);
        if (OperatingSystem.IsWindowsVersionAtLeast(7) && _interpolator is { } interpolator && payload is D3D11TexturePayload current
            && layer.Sampling is FrameSampling.Blend or FrameSampling.OpticalFlow && layer.Next?.Payload is D3D11TexturePayload next)
        {
            return (layer.Sampling == FrameSampling.OpticalFlow
                ? interpolator.Flow(current, next, layer.Fraction)
                : interpolator.Blend(current, next, layer.Fraction)) ?? payload;
        }

        return payload;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
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
            _pipeline.CurrentFrame.Copy(before.Gpu, new GpuBox(0, 0, width, Height), _output!.Gpu, 0, 0);
        }
        finally
        {
            _pool.Return(before);
        }
    }

    private bool DrawTitle(LayerFrame layer, GeneratedContent content, TitleRasterizer? titles, in Destination destination, ref FrameStats stats)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(7) || titles is null || titles.TryGet(content) is not { } title)
        {
            stats.LayersSkipped++;
            if (OperatingSystem.IsWindowsVersionAtLeast(7) && titles is not null && !titles.IsReady(content))
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

        using (_engine.BeginFrame())
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(7))
            {
                _interpolator?.Trim();
            }

            _pool.Trim();
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
        using (GpuMappedRows rows = ReadOutput())
        {
            for (int y = 0; y < Height; y++)
            {
                ReadOnlySpan<byte> row = rows.Row(y);
                Span<float> target = pixels.AsSpan(y * Width * 4, Width * 4);
                if (Format == CompositeFormat.Rgba8Unorm)
                {
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = row[i] / 255f;
                    }

                    continue;
                }

                ReadOnlySpan<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(row);
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = (float)halves[i];
                }
            }
        }

        PixelFingerprint.Record("composicion-float", pixels.AsSpan());
        return pixels;
    }

    /// <summary>Copia el último cuadro compuesto a la CPU en RGBA de 8 bits, fila tras fila (para pruebas y miniaturas).</summary>
    public byte[] ReadbackRgba()
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        var pixels = new byte[Width * Height * 4];
        using (GpuMappedRows rows = ReadOutput())
        {
            for (int y = 0; y < Height; y++)
            {
                ReadOnlySpan<byte> row = rows.Row(y);
                Span<byte> target = pixels.AsSpan(y * Width * 4, Width * 4);
                if (Format == CompositeFormat.Rgba8Unorm)
                {
                    row.CopyTo(target);
                    continue;
                }

                ReadOnlySpan<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(row);
                if (ColorSpace != SequenceColorSpace.Sdr709)
                {
                    LinearToSdr(halves, target);
                    continue;
                }

                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = (byte)Math.Round(Math.Clamp((float)halves[i], 0f, 1f) * 255f);
                }
            }
        }

        PixelFingerprint.Record("composicion", pixels.AsSpan());
        return pixels;
    }

    /// <summary>
    /// Suelta ya las vistas de decodificadores cerrados y las que llevan tiempo sin usarse. <see cref="Render"/> lo hace
    /// solo; en pausa, cuando no se compone, quien dibuja lo llama de vez en cuando para no retener esos pools.
    /// </summary>
    public void TrimCachedViews()
    {
        using (_engine.BeginFrame())
        {
            _views.Trim();
            if (OperatingSystem.IsWindowsVersionAtLeast(7))
            {
                _interpolator?.Trim();
            }

            _pool.Trim();
        }
    }

    /// <summary>Suelta las vistas guardadas de superficies del decodificador (por ejemplo, al cerrar una fuente).</summary>
    public void ReleaseCachedViews()
    {
        using (_engine.BeginFrame())
        {
            _views.Clear();
        }
    }

    public void Dispose()
    {
        _views?.Dispose();
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _interpolator?.Dispose();
        }

        _pool.Dispose();
        _luts.Dispose();
        _layers?.Dispose();
        _effects?.Dispose();
        _pipeline?.Dispose();
        _readback?.Dispose();
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
        GpuFormat format = Format == CompositeFormat.Rgba8Unorm ? GpuFormat.Rgba8Unorm : GpuFormat.Rgba16Float;
        GpuTexture target = _engine.CreateTexture(new GpuTextureDesc(Width, Height, format,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMips, MipLevels));
        _output = new RenderTarget(target, RenderTarget.BytesFor(Width, Height, format));
    }

    /// <summary>Una fila de la composición HDR (lineal BT.2020, 1,0 = 203 nits) a SDR BT.709 de 8 bits, como <c>PresentHdr</c> y la variante TONEMAP de la exportación.</summary>
    private static void LinearToSdr(ReadOnlySpan<Half> source, Span<byte> target)
    {
        for (int i = 0; i + 3 < target.Length; i += 4)
        {
            double r = (float)source[i];
            double g = (float)source[i + 1];
            double b = (float)source[i + 2];
            target[i] = Bt709Code((1.660491 * r) - (0.587641 * g) - (0.072850 * b));
            target[i + 1] = Bt709Code((-0.124550 * r) + (1.132900 * g) - (0.008350 * b));
            target[i + 2] = Bt709Code((-0.018151 * r) - (0.100579 * g) + (1.118730 * b));
            target[i + 3] = (byte)Math.Round(Math.Clamp((float)source[i + 3], 0f, 1f) * 255f);
        }
    }

    private static byte Bt709Code(double light)
    {
        double x = Math.Clamp(light, 0, 1);
        return (byte)Math.Round((x < 0.018 ? 4.5 * x : (1.099 * Math.Pow(x, 0.45)) - 0.099) * 255);
    }

    /// <summary>Copia la salida (mip 0) a la CPU y espera; las filas retienen el candado del contexto hasta soltarlas.</summary>
    private GpuMappedRows ReadOutput()
    {
        _readback ??= _engine.CreateReadback(Width, Height, _output!.Format);
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            frame.Copy(_output!.Gpu, new GpuBox(0, 0, Width, Height), _readback);
        }

        return _readback.Map();
    }
}
