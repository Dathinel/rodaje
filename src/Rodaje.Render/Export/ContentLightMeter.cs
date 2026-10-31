using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Export;

/// <summary>MaxCLL y MaxFALL medidos, en nits (CTA-861.3), redondeados al entero.</summary>
public readonly record struct ContentLight(int MaxCll, int MaxFall);

/// <summary>
/// Mide MaxCLL y MaxFALL sobre la composición lineal de una secuencia HDR (1,0 = 203 nits): por cuadro, el máximo y la media
/// de max(R, G, B); MaxCLL es el mayor máximo y MaxFALL la mayor media de todos los cuadros medidos. Cada tesela de 16×16 se
/// reduce en la GPU (<c>ExportShaders.LightVariant</c>) y la CPU junta las teselas: en 1080p son 8160 pares de floats por cuadro.
/// </summary>
public sealed unsafe class ContentLightMeter : IDisposable
{
    private const int Tile = 16;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private readonly int _width;
    private readonly int _height;
    private readonly int _tilesPerRow;
    private readonly int _tileCount;
    private readonly float[] _tiles;
    private ID3D11ComputeShader* _shader;
    private ID3D11Buffer* _constants;
    private ID3D11Buffer* _buffer;
    private ID3D11UnorderedAccessView* _view;
    private ID3D11Buffer* _staging;
    private double _peak;
    private double _average;
    private bool _disposed;

    /// <param name="gpu">El dispositivo del compositor que se mide.</param>
    /// <param name="width">Ancho de la composición.</param>
    /// <param name="height">Alto de la composición.</param>
    public ContentLightMeter(GpuDevice gpu, int width, int height, ShaderCache? shaders = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _gpu = gpu;
        _device = (ID3D11Device*)gpu.NativeDevice;
        _context = (ID3D11DeviceContext*)gpu.NativeContext;
        _width = width;
        _height = height;
        _tilesPerRow = (width + Tile - 1) / Tile;
        _tileCount = _tilesPerRow * ((height + Tile - 1) / Tile);
        _tiles = new float[_tileCount * 2];
        try
        {
            Create((shaders ?? ShaderCache.Default).GetOrCompile(ExportShaders.LightVariant));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Cuadros medidos hasta ahora.</summary>
    public long Frames { get; private set; }

    /// <summary>Lo medido hasta ahora; 0 y 0 sin cuadros.</summary>
    public ContentLight Result => new(Nits(_peak), Nits(_average));

    /// <summary>Luz lineal (1,0 = 203 nits) a nits enteros.</summary>
    internal static int Nits(double linear) => (int)Math.Clamp(Math.Round(linear * 203), 0, 100_000);

    /// <summary>Mide la composición de <paramref name="shaderResourceView"/> (<c>Compositor.ShaderResourceView</c>) y espera a la GPU.</summary>
    public void Measure(nint shaderResourceView)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(shaderResourceView);
        _gpu.LockContext();
        try
        {
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes de luz)");
            *(uint*)mapped.pData = (uint)_width;
            *((uint*)mapped.pData + 1) = (uint)_height;
            *((uint*)mapped.pData + 2) = (uint)_tilesPerRow;
            *((uint*)mapped.pData + 3) = 0;
            _context->Unmap((ID3D11Resource*)_constants, 0);

            var source = (ID3D11ShaderResourceView*)shaderResourceView;
            ID3D11Buffer* constants = _constants;
            ID3D11UnorderedAccessView* view = _view;
            _context->CSSetShader(_shader, null, 0);
            _context->CSSetConstantBuffers(0, 1, &constants);
            _context->CSSetShaderResources(0, 1, &source);
            _context->CSSetUnorderedAccessViews(0, 1, &view, null);
            _context->Dispatch((uint)_tilesPerRow, (uint)(_tileCount / _tilesPerRow), 1);
            ID3D11ShaderResourceView* noSource = null;
            ID3D11UnorderedAccessView* noView = null;
            _context->CSSetShaderResources(0, 1, &noSource);
            _context->CSSetUnorderedAccessViews(0, 1, &noView, null);
            _context->CSSetShader(null, null, 0);
            _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_buffer);
            Check(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(luz)");
            try
            {
                new ReadOnlySpan<float>(mapped.pData, _tiles.Length).CopyTo(_tiles);
            }
            finally
            {
                _context->Unmap((ID3D11Resource*)_staging, 0);
            }
        }
        finally
        {
            _gpu.UnlockContext();
        }

        double peak = 0;
        double sum = 0;
        for (int i = 0; i < _tileCount; i++)
        {
            peak = Math.Max(peak, _tiles[i * 2]);
            sum += _tiles[(i * 2) + 1];
        }

        _peak = Math.Max(_peak, peak);
        _average = Math.Max(_average, sum / ((double)_width * _height));
        Frames++;
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
            Com.Release(ref _shader);
            Com.Release(ref _constants);
            Com.Release(ref _view);
            Com.Release(ref _buffer);
            Com.Release(ref _staging);
        }
        finally
        {
            _gpu.UnlockContext();
        }
    }

    private void Create(byte[] code)
    {
        ID3D11ComputeShader* shader;
        fixed (byte* bytes = code)
        {
            Check(_device->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), "CreateComputeShader(luz)");
        }

        _shader = shader;
        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = 16,
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&constants, null, &buffer), "CreateBuffer(constantes de luz)");
        _constants = buffer;

        uint bytes = (uint)(_tileCount * 8);
        var tiles = new D3D11_BUFFER_DESC
        {
            ByteWidth = bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS,
        };
        Check(_device->CreateBuffer(&tiles, null, &buffer), "CreateBuffer(teselas de luz)");
        _buffer = buffer;
        var viewDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        viewDesc.Buffer.FirstElement = 0;
        viewDesc.Buffer.NumElements = bytes / 4;
        viewDesc.Buffer.Flags = (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW;
        ID3D11UnorderedAccessView* view;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)_buffer, &viewDesc, &view), "CreateUnorderedAccessView(teselas de luz)");
        _view = view;

        var staging = new D3D11_BUFFER_DESC
        {
            ByteWidth = bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        Check(_device->CreateBuffer(&staging, null, &buffer), "CreateBuffer(lectura de luz)");
        _staging = buffer;
    }
}
