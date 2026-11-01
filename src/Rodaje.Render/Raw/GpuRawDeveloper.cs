using System.Diagnostics;
using Rodaje.Core.Raw;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.D3D11;

namespace Rodaje.Render.Raw;

/// <summary>
/// Revela mosaicos de Bayer en la GPU (docs/hdr.md, fase D): el mosaico de 16 bits sube a una textura <c>R16_UINT</c>, un
/// compute shader (<see cref="RawShaders"/>) hace el balance, el interpolado de Malvar-He-Cutler, la matriz de la cámara, la
/// exposición y la curva, igual que <see cref="MosaicDeveloper"/> en la CPU, y el cuadro rgba16f vuelve por una textura
/// <c>Staging</c>. Un revelador sirve para todos los cuadros del mismo tamaño; cada llamada espera a la GPU.
/// </summary>
public sealed unsafe class GpuRawDeveloper : IDisposable
{
    private const int Group = 16;

    private readonly GpuDevice _gpu;
    private readonly ID3D11Device* _device;
    private readonly ID3D11DeviceContext* _context;
    private ID3D11ComputeShader* _shader;
    private ID3D11Buffer* _constants;
    private ID3D11Texture2D* _mosaic;
    private ID3D11ShaderResourceView* _mosaicView;
    private ID3D11Texture2D* _developed;
    private ID3D11UnorderedAccessView* _developedView;
    private ID3D11Texture2D* _staging;
    private long _gpuTicks;
    private bool _disposed;

    /// <param name="gpu">El dispositivo (el de la importación; WARP en las pruebas).</param>
    /// <param name="width">Ancho del mosaico (y del cuadro revelado).</param>
    /// <param name="height">Alto del mosaico.</param>
    /// <exception cref="RenderException">Direct3D no pudo crear los recursos o compilar el shader.</exception>
    public GpuRawDeveloper(GpuDevice gpu, int width, int height, ShaderCache? shaders = null)
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
        try
        {
            Create((shaders ?? ShaderCache.Default).GetOrCompile(RawShaders.DevelopVariant));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Cuadros revelados hasta ahora.</summary>
    public long Frames { get; private set; }

    /// <summary>Tiempo total de <see cref="Develop"/> (subida, shader, espera a la GPU y bajada).</summary>
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(0, _gpuTicks);

    /// <summary>
    /// Revela <paramref name="mosaic"/> (ancho × alto códigos con el negro restado, por filas) con <paramref name="parameters"/>
    /// en <paramref name="output"/> (rgba16f, alfa 1, <paramref name="stride"/> bytes por fila) y espera a que la GPU termine.
    /// </summary>
    /// <exception cref="ArgumentException">Tamaños que no cuadran o parámetros no válidos.</exception>
    /// <exception cref="RenderException">Direct3D falló (por ejemplo, se perdió el dispositivo).</exception>
    public void Develop(ReadOnlySpan<ushort> mosaic, RawDevelopParameters parameters, Span<byte> output, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Check();
        if (mosaic.Length < Width * Height)
        {
            throw new ArgumentException("El mosaico es más chico que el cuadro.", nameof(mosaic));
        }

        if (stride < Width * 8 || output.Length < ((long)stride * (Height - 1)) + (Width * 8))
        {
            throw new ArgumentException("La salida es más chica que el cuadro.", nameof(output));
        }

        long start = Stopwatch.GetTimestamp();
        float[] multipliers = parameters.NormalizedMultipliers();
        IReadOnlyList<float> m = parameters.Matrix;
        _gpu.LockContext();
        try
        {
            fixed (ushort* data = mosaic)
            {
                _context->UpdateSubresource((ID3D11Resource*)_mosaic, 0, null, data, (uint)(Width * 2), 0);
            }

            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes de revelado)");
            var constants = (Constants*)mapped.pData;
            constants->Width = (uint)Width;
            constants->Height = (uint)Height;
            constants->Curve = (uint)parameters.Curve;
            constants->Reserved = 0;
            constants->TopLeft = (uint)parameters.Pattern.TopLeft;
            constants->TopRight = (uint)parameters.Pattern.TopRight;
            constants->BottomLeft = (uint)parameters.Pattern.BottomLeft;
            constants->BottomRight = (uint)parameters.Pattern.BottomRight;
            constants->ScaleR = multipliers[0] / parameters.White;
            constants->ScaleG = multipliers[1] / parameters.White;
            constants->ScaleB = multipliers[2] / parameters.White;
            constants->Gain = (float)parameters.Gain;
            float* rows = constants->Rows;
            for (int row = 0; row < 3; row++)
            {
                rows[row * 4] = m[row * 3];
                rows[(row * 4) + 1] = m[(row * 3) + 1];
                rows[(row * 4) + 2] = m[(row * 3) + 2];
                rows[(row * 4) + 3] = 0;
            }

            _context->Unmap((ID3D11Resource*)_constants, 0);

            ID3D11Buffer* buffer = _constants;
            ID3D11ShaderResourceView* source = _mosaicView;
            ID3D11UnorderedAccessView* target = _developedView;
            _context->CSSetShader(_shader, null, 0);
            _context->CSSetConstantBuffers(0, 1, &buffer);
            _context->CSSetShaderResources(0, 1, &source);
            _context->CSSetUnorderedAccessViews(0, 1, &target, null);
            _context->Dispatch((uint)((Width + Group - 1) / Group), (uint)((Height + Group - 1) / Group), 1);
            ID3D11ShaderResourceView* noSource = null;
            ID3D11UnorderedAccessView* noTarget = null;
            _context->CSSetShaderResources(0, 1, &noSource);
            _context->CSSetUnorderedAccessViews(0, 1, &noTarget, null);
            _context->CSSetShader(null, null, 0);
            _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_developed);
            Check(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(cuadro revelado)");
            try
            {
                int rowBytes = Width * 8;
                for (int y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + ((nuint)y * mapped.RowPitch), rowBytes).CopyTo(output.Slice(y * stride, rowBytes));
                }
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

        _gpuTicks += Stopwatch.GetTimestamp() - start;
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
            Com.Release(ref _mosaicView);
            Com.Release(ref _mosaic);
            Com.Release(ref _developedView);
            Com.Release(ref _developed);
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
            Check(_device->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), "CreateComputeShader(revelado)");
        }

        _shader = shader;
        var constants = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(Constants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(_device->CreateBuffer(&constants, null, &buffer), "CreateBuffer(constantes de revelado)");
        _constants = buffer;

        var mosaic = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R16_UINT,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        Check(_device->CreateTexture2D(&mosaic, null, &texture), "CreateTexture2D(mosaico)");
        _mosaic = texture;
        ID3D11ShaderResourceView* view;
        Check(_device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(mosaico)");
        _mosaicView = view;

        var developed = mosaic with
        {
            Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
        };
        Check(_device->CreateTexture2D(&developed, null, &texture), "CreateTexture2D(cuadro revelado)");
        _developed = texture;
        ID3D11UnorderedAccessView* uav;
        Check(_device->CreateUnorderedAccessView((ID3D11Resource*)texture, null, &uav), "CreateUnorderedAccessView(cuadro revelado)");
        _developedView = uav;

        var staging = developed with
        {
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        Check(_device->CreateTexture2D(&staging, null, &texture), "CreateTexture2D(lectura del revelado)");
        _staging = texture;
    }

    private struct Constants
    {
        public uint Width, Height, Curve, Reserved;
        public uint TopLeft, TopRight, BottomLeft, BottomRight;
        public float ScaleR, ScaleG, ScaleB, Gain;

        /// <summary>Tres filas de la matriz, cada una en un float4.</summary>
        public fixed float Rows[12];
    }
}
