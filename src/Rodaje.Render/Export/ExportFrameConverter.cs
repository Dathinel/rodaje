using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;

namespace Rodaje.Render.Export;

/// <summary>Recibe un cuadro convertido, prestado mientras dura la llamada.</summary>
public delegate void ExportFrameHandler(in ExportVideoFrame frame);

/// <summary>Miniatura BGRA (alfa 255) de un cuadro de la salida, para el progreso de la exportación.</summary>
/// <param name="Index">Cuadro de la salida del que salió.</param>
public sealed record ExportThumbnail(long Index, int Width, int Height, byte[] Bgra);

/// <summary>
/// De la composición R'G'B' a los cuadros de la salida, en la GPU (D-028): escala, forma (barras, recorte, estirado),
/// Y'CbCr con la matriz y el rango de <see cref="ExportVideoFormat.Color"/>, croma «left», tramado al bajar a 8 bits, y un
/// anillo de búferes <c>Staging</c> para que la copia a la CPU no detenga a la GPU.
/// </summary>
/// <remarks>
/// <para>
/// Uso, desde el hilo de la exportación: <see cref="Submit"/> encola la conversión y la copia de un cuadro (no espera);
/// <see cref="TryRead"/> mapea el más antiguo, lo copia a un búfer propio (reservado una vez), suelta el mapeo y se lo
/// presta al manejador (normalmente <c>IExportSink.WriteVideo</c>). Con el anillo lleno, <see cref="Submit"/> lanza:
/// primero se lee. El patrón es encolar el cuadro n y leer el n − 2 (con 3 en vuelo), así la GPU ya terminó cuando se lee.
/// </para>
/// <para>
/// Escala: con <see cref="ExportScaleFilter.Auto"/>, una reducción a la mitad exacta (o a un cuarto…) lee el nivel de
/// mip, que es el promedio 2×2; lo demás va con Catmull-Rom, ensanchado 1/escala al reducir. Los otros filtros leen
/// siempre el nivel 0. Una composición sin mips (sin <c>GENERATE_MIPS</c>) también va por el nivel 0.
/// </para>
/// <para>
/// Con un formato con alfa la composición debe venir con fondo transparente (<see cref="Compositor.TransparentBackground"/>);
/// el alfa sale recto, no premultiplicado, y las barras son transparentes. Sin alfa, las barras son negras.
/// </para>
/// </remarks>
public sealed unsafe class ExportFrameConverter : IDisposable
{
    public const int DefaultRingSize = 3;

    /// <summary>Ancho máximo de la miniatura (menos si la salida es más estrecha).</summary>
    public const int ThumbnailWidth = 320;

    private const int MaxCachedSources = 4;
    private const uint DoNotWait = 0x100000;

    private readonly GpuDevice? _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly TimeProvider _time;
    private readonly ExportLayout _layout;
    private readonly ScalePlan _plan;
    private readonly ScalePlan _flatPlan;
    private readonly byte[] _cpu;
    private readonly Slot[] _slots;
    private readonly List<SourceViews> _sources = [];
    private readonly ShaderCache _shaders;
    private ID3D11ComputeShader* _scale;
    private ID3D11ComputeShader* _convert;
    private ID3D11ComputeShader* _thumbnail;
    private ID3D11Buffer* _scaleConstants;
    private ID3D11Buffer* _convertConstants;
    private ID3D11Buffer* _thumbnailConstants;
    private ID3D11SamplerState* _sampler;
    private ID3D11Buffer* _frameBuffer;
    private ID3D11UnorderedAccessView* _frameUav;
    private ID3D11Buffer* _thumbnailBuffer;
    private ID3D11UnorderedAccessView* _thumbnailUav;
    private ID3D11Texture2D* _scaled;
    private ID3D11UnorderedAccessView* _scaledUav;
    private ID3D11ShaderResourceView* _scaledSrv;
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
    /// <param name="ringSize">Búferes <c>Staging</c> en vuelo (2 a 8).</param>
    public ExportFrameConverter(GpuDevice gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit = ExportFit.Bars, ExportScaleFilter filter = ExportScaleFilter.Auto, int ringSize = DefaultRingSize)
        : this(gpu ?? throw new ArgumentNullException(nameof(gpu)), gpu.NativeDevice, gpu.NativeContext, sourceWidth, sourceHeight,
            output, fit, filter, ringSize, ShaderCache.Default, TimeProvider.System)
    {
    }

    /// <summary>Con un dispositivo propio (WARP en las pruebas): sin <paramref name="gpu"/> no se toma ningún candado.</summary>
    internal ExportFrameConverter(GpuDevice? gpu, nint device, nint context, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit, ExportScaleFilter filter, int ringSize, ShaderCache shaders, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfZero(device);
        ArgumentOutOfRangeException.ThrowIfZero(context);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceWidth, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceHeight, D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ringSize, 8);
        _gpu = gpu;
        _device = (ID3D11Device*)device;
        _context = (ID3D11DeviceContext*)context;
        _shaders = shaders;
        _time = time;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Output = output;
        Fit = fit;
        Filter = filter;
        Capacity = ringSize;
        ExportPixelFormat pixels = output.PixelFormat;
        Rect = ExportGeometry.Place(sourceWidth, sourceHeight, output.Width, output.Height, fit,
            evenX: pixels.NeedsEvenWidth(), evenY: pixels.HalfHeightChroma());
        int maxLevel = (int)Math.Log2(Math.Max(sourceWidth, sourceHeight));
        _plan = ExportGeometry.Plan(sourceWidth, sourceHeight, output.Width, output.Height, Rect, filter, maxLevel);
        _flatPlan = ExportGeometry.Plan(sourceWidth, sourceHeight, output.Width, output.Height, Rect, filter, 0);
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

    /// <summary>El dispositivo de la exportación.</summary>
    /// <exception cref="InvalidOperationException">El conversor se creó sobre un dispositivo propio (pruebas).</exception>
    public GpuDevice Device => _gpu ?? throw new InvalidOperationException("Este conversor usa un dispositivo propio, no un GpuDevice.");

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public ExportVideoFormat Output { get; }

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
    public void Submit(nint compositeTexture, long index)
    {
        ThrowIfUnusable(compositeTexture, index);
        Enqueue(compositeTexture, 0, 0, index);
    }

    /// <summary>
    /// Como <see cref="Submit"/>, pero mezcla dos composiciones (conversión de tasa con mezcla):
    /// <c>(1 − weight)·first + weight·second</c> en R'G'B' antes de convertir.
    /// </summary>
    public void SubmitBlend(nint first, nint second, float weight, long index)
    {
        ThrowIfUnusable(first, index);
        ThrowIfUnusable(second, index);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1f);
        Enqueue(first, second, weight, index);
    }

    /// <summary>Si hay un cuadro encolado, espera a que la GPU lo termine, se lo presta a <paramref name="handler"/> y lo suelta.</summary>
    /// <returns><c>false</c> si no había ninguno.</returns>
    public bool TryRead(ExportFrameHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (Pending == 0)
        {
            return false;
        }

        ref Slot slot = ref _slots[_head];
        // Map con DO_NOT_WAIT y el candado suelto entre intentos: mientras la GPU termina, el decodificador sigue usando el contexto.
        byte* data = MapWhenReady(slot.Staging);
        try
        {
            new ReadOnlySpan<byte>(data, _cpu.Length).CopyTo(_cpu);
        }
        finally
        {
            Unmap(slot.Staging);
        }

        if (slot.HasThumbnail)
        {
            (int width, int height) = ThumbnailSize;
            byte[] bgra = GC.AllocateUninitializedArray<byte>(width * height * 4);
            byte* thumb = MapWhenReady(slot.ThumbnailStaging);
            try
            {
                new ReadOnlySpan<byte>(thumb, bgra.Length).CopyTo(bgra);
            }
            finally
            {
                Unmap(slot.ThumbnailStaging);
            }

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
        Lock();
        try
        {
            foreach (SourceViews source in _sources)
            {
                source.Dispose();
            }

            _sources.Clear();
            for (int i = 0; i < _slots.Length; i++)
            {
                Com.Release(ref _slots[i].Staging);
                Com.Release(ref _slots[i].ThumbnailStaging);
            }

            Com.Release(ref _scale);
            Com.Release(ref _convert);
            Com.Release(ref _thumbnail);
            Com.Release(ref _scaleConstants);
            Com.Release(ref _convertConstants);
            Com.Release(ref _thumbnailConstants);
            Com.Release(ref _sampler);
            Com.Release(ref _frameUav);
            Com.Release(ref _frameBuffer);
            Com.Release(ref _thumbnailUav);
            Com.Release(ref _thumbnailBuffer);
            Com.Release(ref _scaledUav);
            Com.Release(ref _scaledSrv);
            Com.Release(ref _scaled);
        }
        finally
        {
            Unlock();
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

    private void Enqueue(nint first, nint second, float weight, long index)
    {
        int slotIndex = (_head + Pending) % Capacity;
        ref Slot slot = ref _slots[slotIndex];
        long now = _time.GetTimestamp();
        bool thumbnail = ThumbnailInterval >= TimeSpan.Zero && (!_anyThumbnail || _time.GetElapsedTime(_lastThumbnail, now) >= ThumbnailInterval);
        Lock();
        try
        {
            SourceViews a = Views(first);
            SourceViews? b = second == 0 ? null : Views(second);
            ScalePlan plan = a.CanMip && (b is null || b.CanMip) ? _plan : _flatPlan;
            ID3D11ShaderResourceView* firstView = a.Prepare(_context, plan.Level);
            ID3D11ShaderResourceView* secondView = b is null ? null : b.Prepare(_context, plan.Level);
            ID3D11ShaderResourceView* picture = firstView;
            if (!plan.PassThrough || b is not null)
            {
                RunScale(plan, firstView, secondView, weight);
                picture = _scaledSrv;
            }

            foreach (ExportPlane plane in _layout.Planes)
            {
                RunConvert(picture, plane);
            }

            if (thumbnail)
            {
                RunThumbnail(picture);
            }

            UnbindCompute();
            _context->CopyResource((ID3D11Resource*)slot.Staging, (ID3D11Resource*)_frameBuffer);
            if (thumbnail)
            {
                _context->CopyResource((ID3D11Resource*)slot.ThumbnailStaging, (ID3D11Resource*)_thumbnailBuffer);
            }

            _context->Flush();
        }
        finally
        {
            Unlock();
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

        var views = SourceViews.Create(_device, (ID3D11Texture2D*)texture, desc, _plan.Level);
        _sources.Add(views);
        return views;
    }

    private void RunScale(in ScalePlan plan, ID3D11ShaderResourceView* first, ID3D11ShaderResourceView* second, float weight)
    {
        EnsureScaled();
        int levelWidth = SourceWidth >> plan.Level;
        int levelHeight = SourceHeight >> plan.Level;
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
        };
        Upload(_scaleConstants, constants);
        ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2] { first, second != null ? second : first };
        Dispatch(_scale, _scaleConstants, inputs, 2, _scaledUav, Output.Width, Output.Height);
    }

    private void RunConvert(ID3D11ShaderResourceView* picture, in ExportPlane plane)
    {
        ExportVideoFormat format = Output;
        ExportPixelFormat pixels = format.PixelFormat;
        int bits = pixels.BitDepth();
        double max = (1 << bits) - 1;
        double unit = Math.Pow(2, bits - 8);
        (double kr, double kb) = format.Color.Matrix == ExportMatrix.Bt601 ? (0.299, 0.114) : (0.2126, 0.0722);
        uint flags = 0;
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
        Upload(_convertConstants, constants);
        Dispatch(_convert, _convertConstants, &picture, 1, _frameUav, plane.Units, plane.Rows);
    }

    private void RunThumbnail(ID3D11ShaderResourceView* picture)
    {
        (int width, int height) = ThumbnailSize;
        var constants = new ThumbnailConstants { Width = (uint)width, Height = (uint)height, PictureWidth = (uint)Output.Width, PictureHeight = (uint)Output.Height };
        Upload(_thumbnailConstants, constants);
        Dispatch(_thumbnail, _thumbnailConstants, &picture, 1, _thumbnailUav, width, height);
    }

    private void Dispatch(ID3D11ComputeShader* shader, ID3D11Buffer* constants, ID3D11ShaderResourceView** inputs, uint inputCount,
        ID3D11UnorderedAccessView* output, int width, int height)
    {
        // Primero se suelta la salida anterior: la textura escalada pasa de UAV a SRV entre la escala y la conversión.
        ID3D11UnorderedAccessView* none = null;
        _context->CSSetUnorderedAccessViews(0, 1, &none, null);
        _context->CSSetShader(shader, null, 0);
        _context->CSSetConstantBuffers(0, 1, &constants);
        ID3D11SamplerState* sampler = _sampler;
        _context->CSSetSamplers(0, 1, &sampler);
        _context->CSSetShaderResources(0, inputCount, inputs);
        _context->CSSetUnorderedAccessViews(0, 1, &output, null);
        _context->Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
    }

    private void UnbindCompute()
    {
        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[2];
        _context->CSSetShaderResources(0, 2, views);
        ID3D11UnorderedAccessView* none = null;
        _context->CSSetUnorderedAccessViews(0, 1, &none, null);
        _context->CSSetShader(null, null, 0);
    }

    private void Upload<T>(ID3D11Buffer* buffer, in T constants)
        where T : unmanaged
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes de exportación)");
        *(T*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)buffer, 0);
    }

    private byte* MapWhenReady(ID3D11Buffer* staging)
    {
        long start = 0;
        var spin = default(SpinWait);
        while (true)
        {
            D3D11_MAPPED_SUBRESOURCE mapped;
            HRESULT hr;
            Lock();
            try
            {
                hr = _context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, DoNotWait, &mapped);
            }
            finally
            {
                Unlock();
            }

            if (hr != DXGI_ERROR_WAS_STILL_DRAWING)
            {
                Check(hr, "Map(cuadro de exportación)");
                if (start != 0)
                {
                    _readWaitTicks += Stopwatch.GetTimestamp() - start;
                }

                return (byte*)mapped.pData;
            }

            if (start == 0)
            {
                start = Stopwatch.GetTimestamp();
            }

            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    private void Unmap(ID3D11Buffer* staging)
    {
        Lock();
        try
        {
            _context->Unmap((ID3D11Resource*)staging, 0);
        }
        finally
        {
            Unlock();
        }
    }

    private void Lock() => _gpu?.LockContext();

    private void Unlock() => _gpu?.UnlockContext();

    private void CreateResources()
    {
        _scale = CreateShader(ExportShaders.ScaleVariant);
        _convert = CreateShader(ExportShaders.ConvertVariant);
        _thumbnail = CreateShader(ExportShaders.ThumbnailVariant);
        _scaleConstants = CreateConstants(sizeof(ScaleConstants));
        _convertConstants = CreateConstants(sizeof(ConvertConstants));
        _thumbnailConstants = CreateConstants(sizeof(ThumbnailConstants));
        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxLOD = D3D11_FLOAT32_MAX,
        };
        ID3D11SamplerState* sampler;
        Check(_device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState(exportación)");
        _sampler = sampler;
        CreateRawBuffer(_layout.TotalBytes, out _frameBuffer, out _frameUav);
        (int width, int height) = ThumbnailSize;
        CreateRawBuffer(width * height * 4, out _thumbnailBuffer, out _thumbnailUav);
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Staging = CreateStaging(_layout.TotalBytes);
            _slots[i].ThumbnailStaging = CreateStaging(width * height * 4);
        }

        if (!_plan.PassThrough)
        {
            EnsureScaled();
        }
    }

    private ID3D11ComputeShader* CreateShader(ShaderVariant variant)
    {
        byte[] code = _shaders.GetOrCompile(variant);
        ID3D11ComputeShader* shader;
        fixed (byte* bytes = code)
        {
            Check(_device->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), $"CreateComputeShader({variant.EntryPoint})");
        }

        return shader;
    }

    private ID3D11Buffer* CreateConstants(int bytes)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&desc, null, &buffer), "CreateBuffer(constantes de exportación)");
        return buffer;
    }

    private void CreateRawBuffer(int bytes, out ID3D11Buffer* buffer, out ID3D11UnorderedAccessView* view)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS,
        };
        ID3D11Buffer* created;
        Check(_device->CreateBuffer(&desc, null, &created), "CreateBuffer(cuadro de exportación)");
        buffer = created;
        view = null;
        var viewDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        viewDesc.Buffer.FirstElement = 0;
        viewDesc.Buffer.NumElements = (uint)(bytes / 4);
        viewDesc.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;
        ID3D11UnorderedAccessView* uav;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)created, &viewDesc, &uav), "CreateUnorderedAccessView(cuadro de exportación)");
        view = uav;
    }

    private ID3D11Buffer* CreateStaging(int bytes)
    {
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&desc, null, &buffer), "CreateBuffer(lectura de exportación)");
        return buffer;
    }

    private void EnsureScaled()
    {
        if (_scaled != null)
        {
            return;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Output.Width,
            Height = (uint)Output.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(escala de exportación)");
        _scaled = texture;
        ID3D11UnorderedAccessView* uav;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)texture, null, &uav), "CreateUnorderedAccessView(escala de exportación)");
        _scaledUav = uav;
        ID3D11ShaderResourceView* srv;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv), "CreateShaderResourceView(escala de exportación)");
        _scaledSrv = srv;
    }

    private struct Slot
    {
        public ID3D11Buffer* Staging;
        public ID3D11Buffer* ThumbnailStaging;
        public long Index;
        public bool HasThumbnail;
    }

    /// <summary>
    /// Vistas de una composición: el nivel que se lee y, si hace falta reducir, la cadena 0..nivel para generar los
    /// mips. Las vistas retienen la textura, así su puntero no se reutiliza mientras está en la caché.
    /// </summary>
    private sealed class SourceViews : IDisposable
    {
        private ID3D11ShaderResourceView* _chain;
        private ID3D11ShaderResourceView* _level;
        private ID3D11ShaderResourceView* _base;

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
                views._base = View(device, texture, desc.Format, 0, 1);
                if (level > 0 && generate && desc.MipLevels > level)
                {
                    views._chain = View(device, texture, desc.Format, 0, level + 1);
                    views._level = View(device, texture, desc.Format, level, 1);
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
        public ID3D11ShaderResourceView* Prepare(ID3D11DeviceContext* context, int level)
        {
            if (level == 0)
            {
                return _base;
            }

            context->GenerateMips(_chain);
            return _level;
        }

        public void Dispose()
        {
            Com.Release(ref _chain);
            Com.Release(ref _level);
            Com.Release(ref _base);
        }

        private static ID3D11ShaderResourceView* View(ID3D11Device* device, ID3D11Texture2D* texture, DXGI_FORMAT format, int mostDetailed, int levels)
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
