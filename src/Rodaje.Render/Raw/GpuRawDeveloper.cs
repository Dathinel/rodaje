using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Core.Raw;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Raw;

/// <summary>
/// Revela mosaicos de Bayer en la GPU (docs/hdr.md, fase D): el mosaico de 16 bits sube a una textura <c>R16_UINT</c>, un
/// compute shader (<see cref="RawShaders"/>) hace el balance, el interpolado de Malvar-He-Cutler, la matriz de la cámara, la
/// exposición y la curva, igual que <see cref="MosaicDeveloper"/> en la CPU, y el cuadro rgba16f vuelve por una textura
/// <c>Staging</c>. Un revelador sirve para todos los cuadros del mismo tamaño; cada llamada espera a la GPU.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public sealed unsafe class GpuRawDeveloper : IDisposable
{
    private const int Group = 16;

    private readonly D3D11Gpu _engine;
    private GpuShader? _shader;
    private GpuPipeline? _pipeline;
    private GpuBuffer? _constants;
    private GpuTexture? _mosaic;
    private GpuTexture? _developed;
    private GpuReadback? _readback;
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
        _engine = D3D11Gpu.For(gpu);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, _engine.Caps.MaxTextureSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, _engine.Caps.MaxTextureSize);
        Width = width;
        Height = height;
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
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            frame.Write(_mosaic!, MemoryMarshal.AsBytes(mosaic), Width * 2);

            Constants constants = default;
            constants.Width = (uint)Width;
            constants.Height = (uint)Height;
            constants.Curve = (uint)parameters.Curve;
            constants.TopLeft = (uint)parameters.Pattern.TopLeft;
            constants.TopRight = (uint)parameters.Pattern.TopRight;
            constants.BottomLeft = (uint)parameters.Pattern.BottomLeft;
            constants.BottomRight = (uint)parameters.Pattern.BottomRight;
            constants.ScaleR = multipliers[0] / parameters.White;
            constants.ScaleG = multipliers[1] / parameters.White;
            constants.ScaleB = multipliers[2] / parameters.White;
            constants.Gain = (float)parameters.Gain;
            for (int row = 0; row < 3; row++)
            {
                constants.Rows[row * 4] = m[row * 3];
                constants.Rows[(row * 4) + 1] = m[(row * 3) + 1];
                constants.Rows[(row * 4) + 2] = m[(row * 3) + 2];
                constants.Rows[(row * 4) + 3] = 0;
            }

            frame.Write(_constants!, new ReadOnlySpan<byte>(&constants, sizeof(Constants)));

            var bindings = default(GpuBindings);
            bindings.SetConstants(0, _constants);
            bindings.SetResource(0, _mosaic);
            bindings.SetStorage(0, _developed);
            frame.Dispatch(_pipeline!, bindings, (Width + Group - 1) / Group, (Height + Group - 1) / Group);
            frame.Copy(_developed!, new GpuBox(0, 0, Width, Height), _readback!);
        }

        using (GpuMappedRows rows = _readback!.Map())
        {
            int rowBytes = Width * 8;
            for (int y = 0; y < Height; y++)
            {
                rows.Row(y).CopyTo(output.Slice(y * stride, rowBytes));
            }
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
        _pipeline?.Dispose();
        _shader?.Dispose();
        _constants?.Dispose();
        _mosaic?.Dispose();
        _developed?.Dispose();
        _readback?.Dispose();
    }

    private void Create(ShaderCache? shaders)
    {
        _shader = _engine.CreateShader(RawShaders.DevelopVariant, shaders);
        _pipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = _shader });
        _constants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(Constants), GpuBufferKind.Constant));
        _mosaic = _engine.CreateTexture(new GpuTextureDesc(Width, Height, GpuFormat.R16Uint, GpuTextureUsage.Sampled));
        _developed = _engine.CreateTexture(new GpuTextureDesc(Width, Height, GpuFormat.Rgba16Float, GpuTextureUsage.Storage));
        _readback = _engine.CreateReadback(Width, Height, GpuFormat.Rgba16Float);
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
