using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Core.Model;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Export;

/// <summary>Recibe un cuadro convertido, prestado mientras dura la llamada.</summary>
public delegate void ExportFrameHandler(in ExportVideoFrame frame);

/// <summary>Miniatura BGRA (alfa 255) de un cuadro de la salida, para el progreso de la exportación.</summary>
/// <param name="Index">Cuadro de la salida del que salió.</param>
public sealed record ExportThumbnail(long Index, int Width, int Height, byte[] Bgra);

/// <summary>
/// De la composición R'G'B' a los cuadros de la salida, en la GPU (D-028): escala, forma (barras, recorte, estirado),
/// Y'CbCr con la matriz y el rango de <see cref="ExportVideoFormat.Color"/>, croma «left», tramado al bajar a 8 bits, y un
/// anillo de búferes de lectura para que la copia a la CPU no detenga a la GPU.
/// </summary>
public sealed unsafe class ExportFrameConverter : IDisposable
{
    public const int DefaultRingSize = 3;

    /// <summary>Ancho máximo de la miniatura (menos si la salida es más estrecha).</summary>
    public const int ThumbnailWidth = 320;

    private const int MaxCachedSources = 4;

    private readonly IGpu _engine;
    private readonly D3D11Gpu? _d3d;
    private readonly TimeProvider _time;
    private readonly ExportLayout _layout;
    private readonly ScalePlan _plan;
    private readonly ScalePlan _flatPlan;
    private readonly byte[] _cpu;
    private readonly Slot[] _slots;
    private readonly List<SourceViews> _sources = [];
    private readonly ShaderCache _shaders;
    private GpuPipeline? _scalePipeline;
    private GpuShader? _scaleShader;
    private GpuPipeline? _convertPipeline;
    private GpuShader? _convertShader;
    private GpuPipeline? _thumbnailPipeline;
    private GpuShader? _thumbnailShader;
    private GpuBuffer? _scaleConstants;
    private GpuBuffer? _convertConstants;
    private GpuBuffer? _thumbnailConstants;
    private GpuSampler? _sampler;
    private GpuBuffer? _frameBuffer;
    private GpuBuffer? _thumbnailBuffer;
    private GpuTexture? _scaled;
    private int _head;
    private long _lastThumbnail;
    private bool _anyThumbnail;
    private long _readWaitTicks;
    private volatile ExportThumbnail? _latestThumbnail;
    private bool _disposed;

    /// <param name="gpu">El dispositivo de la exportación, el mismo del compositor.</param>
    /// <param name="sourceWidth">Ancho de la composición (la secuencia a resolución completa).</param>
    /// <param name="sourceHeight">Alto de la composición.</param>
    /// <param name="output">Tamaño, formato de píxel y color de la salida.</param>
    /// <param name="fit">Cómo cabe la composición si la forma no coincide.</param>
    /// <param name="filter">Filtro de escala.</param>
    /// <param name="ringSize">Búferes de lectura en vuelo (2 a 8).</param>
    /// <param name="region">Recorte de la composición antes de escalar; <c>null</c> la exporta entera.</param>
    /// <param name="source">
    /// Espacio de color de la composición (<see cref="Compositor.ColorSpace"/>): en HDR es luz lineal BT.2020 con 1,0 = 203 nits y se
    /// codifica a PQ/HLG o se recorta a SDR BT.709 según <paramref name="output"/>; en SDR, la de siempre.
    /// </param>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public ExportFrameConverter(GpuDevice gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit = ExportFit.Bars, ExportScaleFilter filter = ExportScaleFilter.Auto, int ringSize = DefaultRingSize,
        ExportSourceRegion? region = null, SequenceColorSpace source = SequenceColorSpace.Sdr709)
        : this(gpu ?? throw new ArgumentNullException(nameof(gpu)), sourceWidth, sourceHeight,
            output, fit, filter, ringSize, ShaderCache.Default, TimeProvider.System, region, source)
    {
    }

    /// <summary>Sobre cualquier motor de <see cref="IGpu"/> (Vulkan en Linux).</summary>
    /// <remarks>shortcut: fuera de D3D11 no hay vistas de un tramo de mips, así que se escala sin mips (el plan de nivel 0); vale igual
    /// salvo al reducir más de la mitad, donde sale con algo de aliasing. Cambiar cuando el contrato tenga vistas de mips.</remarks>
    public ExportFrameConverter(IGpu gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit = ExportFit.Bars, ExportScaleFilter filter = ExportScaleFilter.Auto, int ringSize = DefaultRingSize,
        ExportSourceRegion? region = null, SequenceColorSpace source = SequenceColorSpace.Sdr709)
        : this(gpu, sourceWidth, sourceHeight, output, fit, filter, ringSize, ShaderCache.Default, TimeProvider.System, region, source)
    {
    }

    /// <summary>Con un dispositivo propio (WARP en las pruebas).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    internal ExportFrameConverter(GpuDevice? gpu, nint device, nint context, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit, ExportScaleFilter filter, int ringSize, ShaderCache shaders, TimeProvider time, ExportSourceRegion? region = null,
        SequenceColorSpace source = SequenceColorSpace.Sdr709)
        : this(gpu ?? throw new ArgumentNullException(nameof(gpu)), sourceWidth, sourceHeight, output, fit, filter, ringSize, shaders, time, region, source)
    {
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    internal ExportFrameConverter(GpuDevice gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit, ExportScaleFilter filter, int ringSize, ShaderCache shaders, TimeProvider time, ExportSourceRegion? region = null,
        SequenceColorSpace source = SequenceColorSpace.Sdr709)
        : this(D3D11Gpu.For(gpu ?? throw new ArgumentNullException(nameof(gpu))), sourceWidth, sourceHeight, output, fit, filter, ringSize, shaders,
            time, region, source)
    {
    }

    internal ExportFrameConverter(IGpu gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit, ExportScaleFilter filter, int ringSize, ShaderCache shaders, TimeProvider time, ExportSourceRegion? region = null,
        SequenceColorSpace source = SequenceColorSpace.Sdr709)
    {
        Source = source;
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(output);
        _engine = gpu;
        _d3d = OperatingSystem.IsWindowsVersionAtLeast(7) ? gpu as D3D11Gpu : null;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceWidth, _engine.Caps.MaxTextureSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceHeight, _engine.Caps.MaxTextureSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ringSize, 8);
        ExportSourceRegion window = region ?? new ExportSourceRegion(0, 0, sourceWidth, sourceHeight);
        if (window.X < 0 || window.Y < 0 || window.Width <= 0 || window.Height <= 0
            || window.X + window.Width > sourceWidth || window.Y + window.Height > sourceHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(region), window, $"El recorte se sale de la composición de {sourceWidth}×{sourceHeight}.");
        }

        _shaders = shaders;
        _time = time;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Output = output;
        Fit = fit;
        Filter = filter;
        Capacity = ringSize;
        Region = window;
        ExportPixelFormat pixels = output.PixelFormat;
        Rect = ExportGeometry.Place(window.Width, window.Height, output.Width, output.Height, fit,
            evenX: pixels.NeedsEvenWidth(), evenY: pixels.HalfHeightChroma());
        int maxLevel = ExportGeometry.MaxLevel(sourceWidth, sourceHeight, window);
        bool whole = window.Width == sourceWidth && window.Height == sourceHeight;
        _plan = ExportGeometry.Plan(window.Width, window.Height, output.Width, output.Height, Rect, filter, maxLevel, whole);
        _flatPlan = ExportGeometry.Plan(window.Width, window.Height, output.Width, output.Height, Rect, filter, 0, whole);
        _layout = new ExportLayout(output);
        _cpu = GC.AllocateUninitializedArray<byte>(_layout.TotalBytes);
        _slots = new Slot[ringSize];
        int thumbnailWidth = Math.Min(ThumbnailWidth, output.Width);
        ThumbnailSize = (thumbnailWidth, Math.Max(1, (int)Math.Round((double)thumbnailWidth * output.Height / output.Width)));
        try
        {
            CreateResources();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>El dispositivo de la exportación (solo con el motor D3D11).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public GpuDevice Device => _d3d?.Device ?? throw new InvalidOperationException("El conversor no usa el motor D3D11.");

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>La parte de la composición que se exporta (entera si no hay recorte).</summary>
    public ExportSourceRegion Region { get; }

    public ExportVideoFormat Output { get; }

    /// <summary>Espacio de color de la composición que entra (<see cref="Compositor.ColorSpace"/>).</summary>
    public SequenceColorSpace Source { get; }

    public ExportFit Fit { get; }

    public ExportScaleFilter Filter { get; }

    /// <summary>Cuadros que caben en vuelo.</summary>
    public int Capacity { get; }

    /// <summary>Cuadros encolados que todavía no se leyeron.</summary>
    public int Pending { get; private set; }

    /// <summary>Tramado ordenado de ±0,5 LSB al bajar a 8 bits (por defecto sí); evita bandas en degradados.</summary>
    public bool Dither { get; init; } = true;

    /// <summary>Cada cuánto se toma una miniatura (por defecto 500 ms); <see cref="Timeout.InfiniteTimeSpan"/> no toma ninguna.</summary>
    public TimeSpan ThumbnailInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>La última miniatura leída; <c>null</c> hasta la primera. Se puede leer desde cualquier hilo.</summary>
    public ExportThumbnail? LatestThumbnail => _latestThumbnail;

    /// <summary>Tiempo total que <see cref="TryRead"/> esperó a la GPU: si crece, la GPU es el cuello y no la bajada.</summary>
    public TimeSpan ReadWait => Stopwatch.GetElapsedTime(0, _readWaitTicks);

    internal (int Width, int Height) ThumbnailSize { get; }

    internal ContentRect Rect { get; }

    /// <summary>El plan con mips; una composición sin mips usa el de nivel 0.</summary>
    internal ScalePlan Plan => _plan;

    /// <summary>Convierte la composición <paramref name="compositeTexture"/> (<c>ID3D11Texture2D*</c> de <c>Compositor.Texture</c>) y encola su copia.</summary>
    /// <param name="index">Número del cuadro en la salida; llega igual en <see cref="ExportVideoFrame.Index"/>.</param>
    /// <exception cref="InvalidOperationException">El anillo está lleno.</exception>
    /// <exception cref="ArgumentException">La textura no mide <see cref="SourceWidth"/>×<see cref="SourceHeight"/>.</exception>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public void Submit(nint compositeTexture, long index)
    {
        ThrowIfUnusable(compositeTexture, index);
        Enqueue(compositeTexture, 0, 0, index);
    }

    /// <summary>Convierte la composición <paramref name="compositeTexture"/> (<see cref="Compositor.Output"/>) y encola su copia.</summary>
    public void Submit(GpuTexture compositeTexture, long index)
    {
        ArgumentNullException.ThrowIfNull(compositeTexture);
        if (OperatingSystem.IsWindowsVersionAtLeast(7) && _d3d is not null)
        {
            Submit((nint)((D3D11Texture)compositeTexture).Texture2D, index);
            return;
        }

        ThrowIfUnusable(compositeTexture, index);
        Enqueue(0, index, frame => (_flatPlan, compositeTexture, null));
    }

    /// <summary>
    /// Como <see cref="Submit(nint, long)"/>, pero mezcla dos composiciones (conversión de tasa con mezcla):
    /// <c>(1 − weight)·first + weight·second</c> en R'G'B' antes de convertir.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public void SubmitBlend(nint first, nint second, float weight, long index)
    {
        ThrowIfUnusable(first, index);
        ThrowIfUnusable(second, index);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1f);
        Enqueue(first, second, weight, index);
    }

    /// <summary>Como <see cref="Submit(GpuTexture, long)"/>, pero mezcla dos composiciones.</summary>
    public void SubmitBlend(GpuTexture first, GpuTexture second, float weight, long index)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (OperatingSystem.IsWindowsVersionAtLeast(7) && _d3d is not null)
        {
            SubmitBlend((nint)((D3D11Texture)first).Texture2D, (nint)((D3D11Texture)second).Texture2D, weight, index);
            return;
        }

        ThrowIfUnusable(first, index);
        ThrowIfUnusable(second, index);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1f);
        Enqueue(weight, index, frame => (_flatPlan, first, second));
    }

    /// <summary>Si hay un cuadro encolado, espera a que la GPU lo termine, se lo presta a <paramref name="handler"/> y lo suelta.</summary>
    /// <returns><c>false</c> si no había ninguno.</returns>
    /// <exception cref="RenderException">La GPU se perdió o la lectura no terminó en <see cref="ReadTimeout"/>.</exception>
    public bool TryRead(ExportFrameHandler handler, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (Pending == 0)
        {
            return false;
        }

        ref Slot slot = ref _slots[_head];
        // TryMap sin espera y el candado suelto entre intentos: mientras la GPU termina, el decodificador sigue usando el contexto.
        using (GpuMappedRows rows = MapWhenReady(slot.Staging, cancellation))
        {
            rows.Row(0).CopyTo(_cpu);
        }

        PixelFingerprint.Record("exportacion", _cpu.AsSpan());

        if (slot.HasThumbnail)
        {
            (int width, int height) = ThumbnailSize;
            byte[] bgra = GC.AllocateUninitializedArray<byte>(width * height * 4);
            using (GpuMappedRows thumbRows = MapWhenReady(slot.ThumbnailStaging, cancellation))
            {
                thumbRows.Row(0).CopyTo(bgra);
            }

            PixelFingerprint.Record("miniatura", bgra.AsSpan());
            _latestThumbnail = new ExportThumbnail(slot.Index, width, height, bgra);
        }

        long index = slot.Index;
        _head = (_head + 1) % Capacity;
        Pending--;
        ExportVideoFrame frame = _layout.Frame(index, _cpu);
        handler(in frame);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool locked = OperatingSystem.IsWindowsVersionAtLeast(7) && _d3d is not null && !_d3d.Device.IsDisposed;
        if (locked && OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            _d3d!.Device.LockContext();
        }

        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(7))
            {
                foreach (SourceViews source in _sources)
                {
                    source.Dispose();
                }

                _sources.Clear();
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].Staging?.Dispose();
                _slots[i].ThumbnailStaging?.Dispose();
            }

            _scalePipeline?.Dispose();
            _scaleShader?.Dispose();
            _convertPipeline?.Dispose();
            _convertShader?.Dispose();
            _thumbnailPipeline?.Dispose();
            _thumbnailShader?.Dispose();
            _scaleConstants?.Dispose();
            _convertConstants?.Dispose();
            _thumbnailConstants?.Dispose();
            _sampler?.Dispose();
            _frameBuffer?.Dispose();
            _thumbnailBuffer?.Dispose();
            _scaled?.Dispose();
        }
        finally
        {
            if (locked && OperatingSystem.IsWindowsVersionAtLeast(7))
            {
                _d3d!.Device.UnlockContext();
            }
        }
    }

    private void ThrowIfUnusable(nint texture, long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (Pending >= Capacity)
        {
            throw new InvalidOperationException("El anillo de cuadros está lleno: hay que leer antes de encolar otro.");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private void Enqueue(nint first, nint second, float weight, long index) => Enqueue(weight, index, frame =>
    {
        SourceViews a = Views(first);
        SourceViews? b = second == 0 ? null : Views(second);
        ScalePlan plan = a.CanMip && (b is null || b.CanMip) ? _plan : _flatPlan;
        return (plan, a.Prepare(frame, plan.Level), b?.Prepare(frame, plan.Level));
    });

    private void ThrowIfUnusable(GpuTexture texture, long index)
    {
        ThrowIfUnusable((nint)1, index);
        if (texture.Width != SourceWidth || texture.Height != SourceHeight)
        {
            throw new ArgumentException($"La composición mide {texture.Width}×{texture.Height} y el conversor espera {SourceWidth}×{SourceHeight}.");
        }
    }

    /// <summary>Escala, convierte y encola la copia; <paramref name="prepare"/> da, dentro del cuadro, el plan y las vistas de las composiciones.</summary>
    private void Enqueue(float weight, long index, Func<IGpuFrame, (ScalePlan Plan, GpuTexture First, GpuTexture? Second)> prepare)
    {
        int slotIndex = (_head + Pending) % Capacity;
        ref Slot slot = ref _slots[slotIndex];
        long now = _time.GetTimestamp();
        bool thumbnail = ThumbnailInterval >= TimeSpan.Zero && (!_anyThumbnail || _time.GetElapsedTime(_lastThumbnail, now) >= ThumbnailInterval);
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            (ScalePlan plan, GpuTexture firstView, GpuTexture? secondView) = prepare(frame);
            GpuTexture picture = firstView;
            if (!plan.PassThrough || secondView is not null)
            {
                RunScale(frame, plan, firstView, secondView, weight);
                picture = _scaled!;
            }

            foreach (ExportPlane plane in _layout.Planes)
            {
                RunConvert(frame, picture, plane);
            }

            if (thumbnail)
            {
                RunThumbnail(frame, picture);
            }

            frame.Copy(_frameBuffer!, 0, _layout.TotalBytes, slot.Staging);
            if (thumbnail)
            {
                (int width, int height) = ThumbnailSize;
                frame.Copy(_thumbnailBuffer!, 0, width * height * 4, slot.ThumbnailStaging);
            }

            frame.Flush();
        }

        if (thumbnail)
        {
            _anyThumbnail = true;
            _lastThumbnail = now;
        }

        slot.Index = index;
        slot.HasThumbnail = thumbnail;
        Pending++;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private SourceViews Views(nint texture)
    {
        for (int i = 0; i < _sources.Count; i++)
        {
            if (_sources[i].Texture == texture)
            {
                return _sources[i];
            }
        }

        D3D11_TEXTURE2D_DESC desc;
        ((ID3D11Texture2D*)texture)->GetDesc(&desc);
        if (desc.Width != (uint)SourceWidth || desc.Height != (uint)SourceHeight)
        {
            throw new ArgumentException($"La composición mide {desc.Width}×{desc.Height} y el conversor espera {SourceWidth}×{SourceHeight}.");
        }

        if (_sources.Count >= MaxCachedSources)
        {
            _sources[0].Dispose();
            _sources.RemoveAt(0);
        }

        var views = SourceViews.Create(_d3d!.NativeDevice, (ID3D11Texture2D*)texture, desc, _plan.Level);
        _sources.Add(views);
        return views;
    }

    private void RunScale(IGpuFrame frame, in ScalePlan plan, GpuTexture first, GpuTexture? second, float weight)
    {
        EnsureScaled();
        int levelWidth = SourceWidth >> plan.Level;
        int levelHeight = SourceHeight >> plan.Level;
        float unit = 1f / (1 << plan.Level);
        bool alpha = Output.PixelFormat.HasAlpha();
        var constants = new ScaleConstants
        {
            Rect = new(plan.Rect.X, plan.Rect.Y, plan.Rect.Width, plan.Rect.Height),
            Source = new(levelWidth, levelHeight, plan.WidenX, plan.WidenY),
            Bars = new(0, 0, 0, alpha ? 0 : 1),
            Kernel = (uint)plan.Kernel,
            Blend = second != null ? 1u : 0u,
            Width = (uint)Output.Width,
            Height = (uint)Output.Height,
            Weight = new(weight, 0, 0, 0),
            Window = new(Region.X * unit, Region.Y * unit, Region.Width * unit, Region.Height * unit),
        };
        frame.Write(_scaleConstants!, MemoryMarshal.CreateReadOnlySpan(ref constants, 1));
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _scaleConstants);
        bindings.SetSampler(0, _sampler);
        bindings.SetResource(0, first);
        bindings.SetResource(1, second ?? first);
        bindings.SetStorage(0, _scaled);
        frame.Dispatch(_scalePipeline!, bindings, (Output.Width + 7) / 8, (Output.Height + 7) / 8);
    }

    private void RunConvert(IGpuFrame frame, GpuTexture picture, in ExportPlane plane)
    {
        ExportVideoFormat format = Output;
        ExportPixelFormat pixels = format.PixelFormat;
        int bits = pixels.BitDepth();
        double max = (1 << bits) - 1;
        double unit = Math.Pow(2, bits - 8);
        (double kr, double kb) = format.Color.Matrix switch
        {
            ExportMatrix.Bt601 => (0.299, 0.114),
            ExportMatrix.Bt2020 => (0.2627, 0.0593),
            _ => (0.2126, 0.0722),
        };
        uint flags = format.Color.Transfer switch { ExportTransfer.Pq => 16u, ExportTransfer.Hlg => 32u, _ => 0u };
        if (flags != 0 && Source == SequenceColorSpace.Sdr709)
        {
            flags |= 64;
        }

        if (Dither && bits == 8)
        {
            flags |= 1;
        }

        if (pixels.HasAlpha())
        {
            flags |= 2;
        }

        if (pixels.Chroma() != ChromaSubsampling.Chroma444)
        {
            flags |= 4;
        }

        if (pixels.HalfHeightChroma())
        {
            flags |= 8;
        }

        var constants = new ConvertConstants
        {
            Offset = (uint)plane.Offset,
            Pitch = (uint)plane.Pitch,
            Units = (uint)plane.Units,
            Rows = (uint)plane.Rows,
            Kind = (uint)plane.Kind,
            MaxCode = (uint)max,
            Shift = pixels == ExportPixelFormat.P010 ? 6u : 0u,
            Flags = flags,
            Coefficients = new((float)kr, (float)kb, (float)(1 / (2 * (1 - kb))), (float)(1 / (2 * (1 - kr)))),
            Range = format.Color.FullRange
                ? new((float)max, 0, (float)max, (float)(1 << (bits - 1)))
                : new((float)(219 * unit), (float)(16 * unit), (float)(224 * unit), (float)(128 * unit)),
            Width = (uint)format.Width,
            Height = (uint)format.Height,
        };
        frame.Write(_convertConstants!, MemoryMarshal.CreateReadOnlySpan(ref constants, 1));
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _convertConstants);
        bindings.SetSampler(0, _sampler);
        bindings.SetResource(0, picture);
        bindings.SetStorage(0, _frameBuffer);
        frame.Dispatch(_convertPipeline!, bindings, (plane.Units + 7) / 8, (plane.Rows + 7) / 8);
    }

    private void RunThumbnail(IGpuFrame frame, GpuTexture picture)
    {
        (int width, int height) = ThumbnailSize;
        var constants = new ThumbnailConstants
        {
            Width = (uint)width,
            Height = (uint)height,
            PictureWidth = (uint)Output.Width,
            PictureHeight = (uint)Output.Height,
        };
        frame.Write(_thumbnailConstants!, MemoryMarshal.CreateReadOnlySpan(ref constants, 1));
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _thumbnailConstants);
        bindings.SetSampler(0, _sampler);
        bindings.SetResource(0, picture);
        bindings.SetStorage(0, _thumbnailBuffer);
        frame.Dispatch(_thumbnailPipeline!, bindings, (width + 7) / 8, (height + 7) / 8);
    }

    /// <summary>Lo más que se espera una lectura (como <see cref="GpuReadback.Map"/>): una GPU colgada no deja la exportación esperando siempre.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMinutes(1);

    private GpuMappedRows MapWhenReady(GpuReadback staging, CancellationToken cancellation)
    {
        long start = 0;
        var spin = default(SpinWait);
        GpuMappedRows rows;
        // TryMap lanza si la GPU se perdió (VK_ERROR_DEVICE_LOST en la valla, DXGI_ERROR_DEVICE_REMOVED).
        while (!staging.TryMap(out rows))
        {
            if (start == 0)
            {
                start = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(start) > ReadTimeout)
            {
                throw new RenderException($"La lectura de la GPU no terminó en {ReadTimeout.TotalSeconds:0} s.");
            }

            cancellation.ThrowIfCancellationRequested();
            spin.SpinOnce(sleep1Threshold: -1);
        }

        if (start != 0)
        {
            _readWaitTicks += Stopwatch.GetTimestamp() - start;
        }

        return rows;
    }

    private void CreateResources()
    {
        _scaleShader = _engine.CreateShader(ExportShaders.ScaleVariant, _shaders);
        _scalePipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = _scaleShader });
        _convertShader = _engine.CreateShader(Output.Color.IsHdr ? ExportShaders.ConvertHdrVariant
            : Source != SequenceColorSpace.Sdr709 ? ExportShaders.ConvertToneMapVariant
            : ExportShaders.ConvertVariant, _shaders);
        _convertPipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = _convertShader });
        _thumbnailShader = _engine.CreateShader(Source != SequenceColorSpace.Sdr709 ? ExportShaders.ThumbnailToneMapVariant : ExportShaders.ThumbnailVariant, _shaders);
        _thumbnailPipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = _thumbnailShader });
        _scaleConstants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(ScaleConstants), GpuBufferKind.Constant));
        _convertConstants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(ConvertConstants), GpuBufferKind.Constant));
        _thumbnailConstants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(ThumbnailConstants), GpuBufferKind.Constant));
        _sampler = _engine.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddressMode.Clamp));
        _frameBuffer = _engine.CreateBuffer(new GpuBufferDesc(_layout.TotalBytes, GpuBufferKind.Raw, Storage: true));
        (int width, int height) = ThumbnailSize;
        _thumbnailBuffer = _engine.CreateBuffer(new GpuBufferDesc(width * height * 4, GpuBufferKind.Raw, Storage: true));
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Staging = _engine.CreateReadback(_layout.TotalBytes);
            _slots[i].ThumbnailStaging = _engine.CreateReadback(width * height * 4);
        }

        if (!_plan.PassThrough)
        {
            EnsureScaled();
        }
    }

    private void EnsureScaled()
    {
        if (_scaled != null)
        {
            return;
        }

        _scaled = _engine.CreateTexture(new GpuTextureDesc(Output.Width, Output.Height, GpuFormat.Rgba16Float, GpuTextureUsage.Sampled | GpuTextureUsage.Storage));
    }

    private struct Slot
    {
        public GpuReadback Staging;
        public GpuReadback ThumbnailStaging;
        public long Index;
        public bool HasThumbnail;
    }

    /// <summary>
    /// Vistas de una composición: el nivel que se lee y, si hace falta reducir, la cadena 0..nivel para generar los
    /// mips. Las vistas retienen la textura, así su puntero no se reutiliza mientras está en la caché.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    private sealed class SourceViews : IDisposable
    {
        private D3D11Texture? _chain;
        private D3D11Texture? _level;
        private D3D11Texture? _base;

        private SourceViews(nint texture) => Texture = texture;

        public nint Texture { get; }

        /// <summary>Tiene los mips del plan y se pueden generar.</summary>
        public bool CanMip { get; private set; }

        public static SourceViews Create(ID3D11Device* device, ID3D11Texture2D* texture, in D3D11_TEXTURE2D_DESC desc, int level)
        {
            var views = new SourceViews((nint)texture);
            try
            {
                bool generate = (desc.MiscFlags & (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS) != 0;
                GpuFormat format = D3D11Gpu.FromNative(desc.Format);
                ID3D11ShaderResourceView* baseSrv = CreateView(device, texture, desc.Format, 0, 1);
                views._base = D3D11Texture.View(new GpuTextureDesc((int)desc.Width, (int)desc.Height, format, GpuTextureUsage.Sampled), baseSrv);
                baseSrv->Release();
                if (level > 0 && generate && desc.MipLevels > level)
                {
                    ID3D11ShaderResourceView* chainSrv = CreateView(device, texture, desc.Format, 0, level + 1);
                    views._chain = D3D11Texture.View(new GpuTextureDesc((int)desc.Width, (int)desc.Height, format, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMips, level + 1), chainSrv);
                    chainSrv->Release();

                    ID3D11ShaderResourceView* levelSrv = CreateView(device, texture, desc.Format, level, 1);
                    views._level = D3D11Texture.View(new GpuTextureDesc((int)desc.Width >> level, (int)desc.Height >> level, format, GpuTextureUsage.Sampled), levelSrv);
                    levelSrv->Release();

                    views.CanMip = true;
                }
                else
                {
                    views.CanMip = level == 0;
                }

                return views;
            }
            catch
            {
                views.Dispose();
                throw;
            }
        }

        /// <summary>Deja listo el nivel <paramref name="level"/> (genera los mips si hace falta) y devuelve su vista.</summary>
        public D3D11Texture Prepare(IGpuFrame frame, int level)
        {
            if (level == 0)
            {
                return _base!;
            }

            frame.GenerateMips(_chain!);
            return _level!;
        }

        public void Dispose()
        {
            _chain?.Dispose();
            _chain = null;
            _level?.Dispose();
            _level = null;
            _base?.Dispose();
            _base = null;
        }

        private static ID3D11ShaderResourceView* CreateView(ID3D11Device* device, ID3D11Texture2D* texture, DXGI_FORMAT format, int mostDetailed, int levels)
        {
            var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = format, ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D };
            desc.Texture2D.MostDetailedMip = (uint)mostDetailed;
            desc.Texture2D.MipLevels = (uint)levels;
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, &desc, &view), "CreateShaderResourceView(composición de exportación)");
            return view;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScaleConstants
    {
        public Float4 Rect;
        public Float4 Source;
        public Float4 Bars;
        public uint Kernel;
        public uint Blend;
        public uint Width;
        public uint Height;
        public Float4 Weight;
        public Float4 Window;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvertConstants
    {
        public uint Offset;
        public uint Pitch;
        public uint Units;
        public uint Rows;
        public uint Kind;
        public uint MaxCode;
        public uint Shift;
        public uint Flags;
        public Float4 Coefficients;
        public Float4 Range;
        public uint Width;
        public uint Height;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailConstants
    {
        public uint Width;
        public uint Height;
        public uint PictureWidth;
        public uint PictureHeight;
    }
}
