using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;

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

    private readonly D3D11Gpu _engine;
    private readonly int _width;
    private readonly int _height;
    private readonly int _tilesPerRow;
    private readonly int _tileCount;
    private readonly float[] _tiles;
    private GpuShader? _shader;
    private GpuPipeline? _pipeline;
    private GpuBuffer? _constants;
    private GpuBuffer? _buffer;
    private GpuReadback? _readback;
    private GpuTexture? _source;
    private nint _sourceView;
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
        _engine = D3D11Gpu.For(gpu);
        _width = width;
        _height = height;
        _tilesPerRow = (width + Tile - 1) / Tile;
        _tileCount = _tilesPerRow * ((height + Tile - 1) / Tile);
        _tiles = new float[_tileCount * 2];
        try
        {
            Create(shaders);
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
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            Span<uint> constants = stackalloc uint[] { (uint)_width, (uint)_height, (uint)_tilesPerRow, 0 };
            frame.Write(_constants!, constants);
            var bindings = default(GpuBindings);
            bindings.SetConstants(0, _constants);
            bindings.SetResource(0, Source((ID3D11ShaderResourceView*)shaderResourceView));
            bindings.SetStorage(0, _buffer);
            frame.Dispatch(_pipeline!, bindings, _tilesPerRow, _tileCount / _tilesPerRow);
            frame.Copy(_buffer!, 0, _tiles.Length * 4, _readback!);
        }

        using (GpuMappedRows rows = _readback!.Map())
        {
            MemoryMarshal.Cast<byte, float>(rows.Row(0)).CopyTo(_tiles);
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
        _pipeline?.Dispose();
        _shader?.Dispose();
        _constants?.Dispose();
        _buffer?.Dispose();
        _readback?.Dispose();
        _source?.Dispose();
        _source = null;
        _sourceView = 0;
    }

    /// <summary>La composición tras la vista de <paramref name="view"/>, envuelta una vez por puntero.</summary>
    private GpuTexture Source(ID3D11ShaderResourceView* view)
    {
        if (_source is not null && _sourceView == (nint)view)
        {
            return _source;
        }

        _source?.Dispose();
        _source = null;
        _sourceView = 0;
        ID3D11Resource* resource;
        view->GetResource(&resource);
        try
        {
            var texture = (ID3D11Texture2D*)resource;
            D3D11_TEXTURE2D_DESC desc;
            texture->GetDesc(&desc);
            _source = _engine.Wrap(texture, new GpuTextureDesc((int)desc.Width, (int)desc.Height,
                D3D11Gpu.FromNative(desc.Format), GpuTextureUsage.Sampled), view);
            _sourceView = (nint)view;
            return _source;
        }
        finally
        {
            resource->Release();
        }
    }

    private void Create(ShaderCache? shaders)
    {
        _shader = _engine.CreateShader(ExportShaders.LightVariant, shaders);
        _pipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = _shader });
        _constants = _engine.CreateBuffer(new GpuBufferDesc(16, GpuBufferKind.Constant));
        int size = _tileCount * 8;
        _buffer = _engine.CreateBuffer(new GpuBufferDesc(size, GpuBufferKind.Raw, Storage: true));
        _readback = _engine.CreateReadback(size);
    }
}
