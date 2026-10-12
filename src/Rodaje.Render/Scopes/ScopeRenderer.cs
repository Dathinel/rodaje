using System.Diagnostics;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.DXGI;

namespace Rodaje.Render.Scopes;

/// <summary>Qué monitor de imagen se dibuja.</summary>
public enum ScopeKind
{
    /// <summary>Forma de onda de la luma (BT.709), por columna de la imagen.</summary>
    Waveform,

    /// <summary>Forma de onda de R, G y B superpuestas.</summary>
    WaveformRgb,

    /// <summary>Desfile: R, G y B uno al lado del otro.</summary>
    Parade,

    /// <summary>Vectorscopio: Cb a la derecha, Cr hacia arriba (256 × 256 a la izquierda de la imagen).</summary>
    Vectorscope,

    /// <summary>Histograma de R, G, B y luma.</summary>
    Histogram,
}

/// <summary>Una imagen de monitor lista para mostrar: BGRA de 8 bits, fila tras fila.</summary>
public sealed record ScopeImage(byte[] Bgra, int Width, int Height, ScopeKind Kind, long Sequence);

/// <summary>Las cuentas crudas de un cuadro, para las pruebas (y para quien quiera dibujar sus propios monitores).</summary>
public sealed class ScopeCounts(uint[] raw, int width, int height, int step)
{
    /// <summary>Ancho y alto de la imagen medida y cada cuántos píxeles se tomó uno.</summary>
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Step { get; } = step;

    /// <summary>Píxeles en la columna <paramref name="column"/> (de <see cref="ScopeRenderer.Columns"/>) con ese nivel (0–255) en el canal 0 R, 1 G, 2 B o 3 luma.</summary>
    public uint Wave(int channel, int level, int column) => raw[(((channel * 256) + level) * ScopeRenderer.Columns) + column];

    /// <summary>Píxeles en la celda (x, y) del vectorscopio de 256 × 256.</summary>
    public uint Vector(int x, int y) => raw[ScopeRenderer.VectorBase + (y * 256) + x];

    /// <summary>Píxeles con ese nivel en el canal 0 R, 1 G, 2 B o 3 luma.</summary>
    public uint Histogram(int channel, int level) => raw[ScopeRenderer.HistogramBase + (channel * 256) + level];

    /// <summary>El mayor nivel del histograma sin los extremos (0 y 255), con el que se escala su dibujo.</summary>
    public uint HistogramPeak => raw[ScopeRenderer.MaxSlot];
}

/// <summary>
/// Los monitores de imagen en la GPU (compute shaders): cuenta los píxeles de la composición por columna y nivel, por
/// celda de croma y por nivel, y dibuja el monitor elegido en una imagen de 512 × 256 que baja por un anillo de
/// <c>Staging</c> sin esperar a la GPU. Se usa desde el hilo que compone, después de <see cref="Compositor.Render"/>.
/// </summary>
public sealed unsafe class ScopeRenderer : IDisposable
{
    /// <summary>Columnas de la forma de onda y del desfile.</summary>
    public const int Columns = 512;

    public const int ImageWidth = 512;

    public const int ImageHeight = 256;

    /// <summary>Celdas del vectorscopio por unidad de croma (Cb o Cr): el 100 % de saturación queda dentro del círculo de 128.</summary>
    public const double VectorScale = 236;

    /// <summary>Ancho máximo que se mide sin submuestrear; por encima se toma un píxel de cada 2 (o 3…) en cada eje.</summary>
    public const int MaxMeasuredWidth = 1920;

    internal const int VectorBase = 4 * 256 * Columns;
    internal const int HistogramBase = VectorBase + (256 * 256);
    internal const int MaxSlot = HistogramBase + 1024;
    private const int CountSlots = MaxSlot + 1;
    private const int Ring = 3;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly Task _ready;
    private readonly ID3D11Texture2D*[] _staging = new ID3D11Texture2D*[Ring];
    private readonly (long Sequence, ScopeKind Kind)[] _pending = new (long, ScopeKind)[Ring];
    private ID3D11ComputeShader* _accumulate;
    private ID3D11ComputeShader* _maximum;
    private ID3D11ComputeShader* _draw;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _counts;
    private ID3D11UnorderedAccessView* _countsView;
    private ID3D11Texture2D* _image;
    private ID3D11UnorderedAccessView* _imageView;
    private long _submitted;
    private long _read;
    private bool _disposed;

    public ScopeRenderer(GpuDevice gpu, ShaderCache? shaders = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        ShaderCache cache = shaders ?? ShaderCache.Default;
        _ready = Task.Run(() =>
        {
            byte[][] code = [cache.GetOrCompile(ScopeShaders.Accumulate), cache.GetOrCompile(ScopeShaders.Maximum), cache.GetOrCompile(ScopeShaders.Draw)];
            return code;
        }).ContinueWith(t => _compiled = t.Result, TaskScheduler.Default);
    }

    private byte[][]? _compiled;

    /// <summary>Termina cuando los shaders están compilados (o falló la compilación: la excepción queda aquí).</summary>
    public Task Ready => _ready;

    /// <summary>Ganancia del trazo: con 1, un nivel con el promedio de píxeles de su columna se ve al 63 %.</summary>
    public float Gain { get; set; } = 1.5f;

    /// <summary>Tiempo de CPU de la última medición encolada, en ms (sin esperar a la GPU).</summary>
    public double LastSubmitMilliseconds { get; private set; }

    /// <summary>
    /// Encola la medición de la última composición de <paramref name="compositor"/> y el dibujo de <paramref name="kind"/>.
    /// No espera: si los shaders no están, no hace nada y devuelve <c>false</c>.
    /// </summary>
    public bool Submit(Compositor compositor, ScopeKind kind)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (_compiled is null || compositor.ShaderResourceView == 0)
        {
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        _gpu.LockContext();
        try
        {
            EnsureResources();
            int slot = (int)(_submitted % Ring);
            if (_submitted - _read >= Ring)
            {
                // El anillo está lleno: la más vieja se pierde (la interfaz solo quiere la última).
                _read = _submitted - Ring + 1;
            }

            Measure(compositor, kind);
            _context->CopyResource((ID3D11Resource*)_staging[slot], (ID3D11Resource*)_image);
            _context->Flush();
            _pending[slot] = (++_submitted, kind);
        }
        finally
        {
            _gpu.UnlockContext();
        }

        LastSubmitMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return true;
    }

    /// <summary>La imagen más nueva que la GPU ya terminó, o <c>null</c> si no hay ninguna nueva. Nunca espera.</summary>
    public ScopeImage? TryRead()
    {
        if (_read >= _submitted || _staging[0] == null)
        {
            return null;
        }

        ScopeImage? latest = null;
        _gpu.LockContext();
        try
        {
            while (_read < _submitted)
            {
                int slot = (int)(_read % Ring);
                D3D11_MAPPED_SUBRESOURCE mapped;
                int hr = _context->Map((ID3D11Resource*)_staging[slot], 0, D3D11_MAP.D3D11_MAP_READ,
                    (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
                if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
                {
                    break;
                }

                Check(hr, "Map(monitor)");
                var pixels = new byte[ImageWidth * ImageHeight * 4];
                for (int y = 0; y < ImageHeight; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + ((nuint)y * mapped.RowPitch), ImageWidth * 4).CopyTo(pixels.AsSpan(y * ImageWidth * 4));
                }

                _context->Unmap((ID3D11Resource*)_staging[slot], 0);
                (long sequence, ScopeKind kind) = _pending[slot];
                latest = new ScopeImage(pixels, ImageWidth, ImageHeight, kind, sequence);
                _read++;
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        return latest;
    }

    /// <summary>Mide y espera: las cuentas crudas de la última composición (para las pruebas).</summary>
    public ScopeCounts MeasureNow(Compositor compositor, ScopeKind kind = ScopeKind.Waveform)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        Ready.GetAwaiter().GetResult();
        _gpu.LockContext();
        try
        {
            EnsureResources();
            (int width, int height, int step) = Measure(compositor, kind);
            var desc = new D3D11_BUFFER_DESC
            {
                ByteWidth = CountSlots * 4,
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Buffer* staging;
            Check(_device->CreateBuffer(&desc, null, &staging), "CreateBuffer(lectura de monitores)");
            try
            {
                _context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)_counts);
                D3D11_MAPPED_SUBRESOURCE mapped;
                Check(_context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(monitores)");
                var raw = new uint[CountSlots];
                new ReadOnlySpan<uint>(mapped.pData, CountSlots).CopyTo(raw);
                _context->Unmap((ID3D11Resource*)staging, 0);
                return new ScopeCounts(raw, width, height, step);
            }
            finally
            {
                staging->Release();
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    /// <summary>Tres pasadas: limpiar y contar, el máximo del histograma y el dibujo. Con el candado del contexto tomado.</summary>
    private (int Width, int Height, int Step) Measure(Compositor compositor, ScopeKind kind)
    {
        int width = compositor.Width;
        int height = compositor.Height;
        int step = Math.Max(1, (width + MaxMeasuredWidth - 1) / MaxMeasuredWidth);
        int measuredWidth = (width + step - 1) / step;
        int measuredHeight = (height + step - 1) / step;
        var constants = new ScopeConstants
        {
            SourceWidth = (uint)width,
            SourceHeight = (uint)height,
            Step = (uint)step,
            Columns = Columns,
            Kind = (float)kind,
            Gain = Gain,
            PixelsPerColumn = (float)measuredWidth * measuredHeight / Math.Min(Columns, measuredWidth),
            TotalPixels = (float)measuredWidth * measuredHeight,
        };
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes de monitores)");
        *(ScopeConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);

        uint* zero = stackalloc uint[4];
        _context->ClearUnorderedAccessViewUint(_countsView, zero);
        ID3D11Buffer* buffer = _constants;
        var source = (ID3D11ShaderResourceView*)compositor.ShaderResourceView;
        ID3D11UnorderedAccessView** views = stackalloc ID3D11UnorderedAccessView*[2] { _countsView, _imageView };
        _context->CSSetConstantBuffers(0, 1, &buffer);
        _context->CSSetShaderResources(0, 1, &source);
        _context->CSSetUnorderedAccessViews(0, 2, views, null);
        _context->CSSetShader(_accumulate, null, 0);
        _context->Dispatch((uint)((measuredWidth + 15) / 16), (uint)((measuredHeight + 15) / 16), 1);
        _context->CSSetShader(_maximum, null, 0);
        _context->Dispatch(1, 1, 1);
        _context->CSSetShader(_draw, null, 0);
        _context->Dispatch(ImageWidth / 8, ImageHeight / 8, 1);

        ID3D11ShaderResourceView* noSource = null;
        views[0] = null;
        views[1] = null;
        _context->CSSetShaderResources(0, 1, &noSource);
        _context->CSSetUnorderedAccessViews(0, 2, views, null);
        _context->CSSetShader(null, null, 0);
        return (measuredWidth, measuredHeight, step);
    }

    private void EnsureResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_accumulate != null)
        {
            return;
        }

        byte[][] code = _compiled!;
        _accumulate = CreateShader(code[0]);
        _maximum = CreateShader(code[1]);
        _draw = CreateShader(code[2]);

        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(ScopeConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&constants, null, &buffer), "CreateBuffer(constantes de monitores)");
        _constants = buffer;

        var counts = new D3D11_BUFFER_DESC
        {
            ByteWidth = CountSlots * 4,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS,
        };
        Check(_device->CreateBuffer(&counts, null, &buffer), "CreateBuffer(cuentas de monitores)");
        _counts = buffer;
        var countsView = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        countsView.Buffer.NumElements = CountSlots;
        countsView.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;
        ID3D11UnorderedAccessView* uav;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_counts, &countsView, &uav), "CreateUnorderedAccessView(cuentas de monitores)");
        _countsView = uav;

        var image = new D3D11_TEXTURE2D_DESC
        {
            Width = ImageWidth,
            Height = ImageHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&image, null, &texture), "CreateTexture2D(monitor)");
        _image = texture;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)texture, null, &uav), "CreateUnorderedAccessView(monitor)");
        _imageView = uav;

        image.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        image.BindFlags = 0;
        image.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        for (int i = 0; i < Ring; i++)
        {
            Check(_device->CreateTexture2D(&image, null, &texture), "CreateTexture2D(lectura de monitor)");
            _staging[i] = texture;
        }
    }

    private ID3D11ComputeShader* CreateShader(byte[] code)
    {
        ID3D11ComputeShader* shader;
        fixed (byte* bytes = code)
        {
            Check(_device->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), "CreateComputeShader(monitor)");
        }

        return shader;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gpu.LockContext();
        try
        {
            Com.Release(ref _accumulate);
            Com.Release(ref _maximum);
            Com.Release(ref _draw);
            Com.Release(ref _constants);
            Com.Release(ref _countsView);
            Com.Release(ref _counts);
            Com.Release(ref _imageView);
            Com.Release(ref _image);
            for (int i = 0; i < Ring; i++)
            {
                ID3D11Texture2D* staging = _staging[i];
                Com.Release(ref staging);
                _staging[i] = null;
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ScopeConstants
    {
        public uint SourceWidth;
        public uint SourceHeight;
        public uint Step;
        public uint Columns;
        public float Kind;
        public float Gain;
        public float PixelsPerColumn;
        public float TotalPixels;
    }
}
