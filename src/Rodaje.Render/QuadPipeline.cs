using System.Runtime.InteropServices;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>
/// Los cuadriláteros del compositor y del presentador sobre <see cref="IGpu"/>: sombreador de vértices, constantes (b0), muestreador
/// (s0) y una canalización por sombreador de píxeles y mezcla. Guarda las entradas enlazadas entre dibujos, como el contexto de D3D11.
/// </summary>
internal sealed unsafe class QuadPipeline : IDisposable
{
    private readonly D3D11Gpu _engine;
    private readonly Dictionary<(GpuShader Pixel, GpuBlend Blend), GpuPipeline> _pipelines = [];
    private readonly Dictionary<nint, GpuTexture> _views = [];
    private GpuShader? _vertex;
    private GpuShader? _layer;
    private GpuShader? _present;
    private GpuBuffer? _constants;
    private GpuSampler? _sampler;
    private GpuBindings _bindings;
    private GpuPipeline? _current;
    private GpuTexture? _target;
    private int _width;
    private int _height;

    public QuadPipeline(D3D11Gpu engine)
    {
        _engine = engine;
        try
        {
            Create();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Texturas de entrada que usan los shaders: planos, composición, fondo, LUT e intermedios (t0–t6).</summary>
    public const int InputSlots = 7;

    public enum Shader
    {
        Layer,
        Present,
    }

    public D3D11Gpu Engine => _engine;

    /// <summary>El cuadro donde se graba; lo abre y lo cierra quien compone o presenta.</summary>
    public IGpuFrame? Frame { get; set; }

    internal IGpuFrame CurrentFrame => Frame ?? throw new InvalidOperationException("No hay un cuadro de GPU abierto.");

    /// <summary>Deja el pipeline listo para dibujar cuadriláteros en <paramref name="target"/>.</summary>
    public void Bind(GpuTexture target, int width, int height, Shader shader)
    {
        bool layer = shader == Shader.Layer;
        Bind(target, width, height, layer ? _layer! : _present!, layer ? GpuBlend.PremultipliedOver : GpuBlend.Opaque);
    }

    /// <summary>Como <see cref="Bind(GpuTexture, int, int, Shader)"/> con otro pixel shader y otra mezcla (<see cref="GpuBlend.Opaque"/>: sin mezcla).</summary>
    public void Bind(GpuTexture target, int width, int height, GpuShader? pixelShader, GpuBlend blend)
    {
        ArgumentNullException.ThrowIfNull(pixelShader);
        _target = target;
        _width = width;
        _height = height;
        if (!_pipelines.TryGetValue((pixelShader, blend), out GpuPipeline? pipeline))
        {
            pipeline = _engine.CreatePipeline(new GpuPipelineDesc { Vertex = _vertex, Pixel = pixelShader, Blend = blend });
            _pipelines.Add((pixelShader, blend), pipeline);
        }

        _current = pipeline;
    }

    /// <summary>Enlaza <paramref name="resource"/> en <c>t#</c> hasta que se cambie o se quite (<c>null</c>).</summary>
    public void SetInput(int register, GpuResource? resource) => _bindings.SetResource(register, resource);

    /// <summary>
    /// Para lo que aún no pasó a <see cref="IGpu"/>: enlaza una vista nativa (la envuelve una vez; las envolturas se sueltan en
    /// <see cref="Unbind"/>).
    /// </summary>
    public void SetInputView(int register, ID3D11ShaderResourceView* view)
    {
        if (view == null)
        {
            _bindings.SetResource(register, null);
            return;
        }

        if (!_views.TryGetValue((nint)view, out GpuTexture? wrapped))
        {
            wrapped = D3D11Texture.View(default, view);
            _views.Add((nint)view, wrapped);
        }

        _bindings.SetResource(register, wrapped);
    }

    /// <summary>Enlaza un búfer de constantes en <c>b#</c> (los efectos usan b1).</summary>
    public void SetConstants(int register, GpuBuffer buffer) => _bindings.SetConstants(register, buffer);

    /// <summary>
    /// 1 si la composición es una secuencia HDR: los shaders trabajan en luz lineal BT.2020 con 1,0 = 203 nits (<c>hdr.w</c>); 0, SDR
    /// en gamma como siempre. Se estampa en cada dibujo para que llegue a todas las constantes sin tocar a quien las arma.
    /// </summary>
    public float HdrMode { get; set; }

    /// <summary>Sube las constantes y dibuja la tira de cuatro vértices.</summary>
    public void Draw(in QuadConstants constants)
    {
        IGpuFrame frame = CurrentFrame;
        QuadConstants exact = constants;
        exact.Hdr.W = HdrMode;
        exact.SetPixelGradients(_width, _height);
        frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref exact, 1));
        frame.Draw(_current!, new GpuTargets(_target!), _bindings, GpuViewport.Of(_width, _height), new GpuDrawCall(4));
    }

    /// <summary>Quita las texturas de entrada (t0–t15); el destino lo suelta el cuadro al cerrarse.</summary>
    public void Unbind()
    {
        for (int i = 0; i < GpuBindings.MaxResources; i++)
        {
            _bindings.SetResource(i, null);
        }

        foreach (GpuTexture view in _views.Values)
        {
            view.Dispose();
        }

        _views.Clear();
    }

    public void Dispose()
    {
        Unbind();
        foreach (GpuPipeline pipeline in _pipelines.Values)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
        _vertex?.Dispose();
        _layer?.Dispose();
        _present?.Dispose();
        _constants?.Dispose();
        _sampler?.Dispose();
    }

    private void Create()
    {
        _vertex = _engine.CreateShader(RenderShaders.Vertex, "vs_5_0", "rodaje-render.hlsl:VsQuad");
        _layer = _engine.CreateShader(RenderShaders.Layer, "ps_5_0", "rodaje-render.hlsl:PsLayer");
        _present = _engine.CreateShader(RenderShaders.Present, "ps_5_0", "rodaje-render.hlsl:PsPresent");
        _constants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(QuadConstants), GpuBufferKind.Constant));
        _sampler = _engine.CreateSampler(new GpuSamplerDesc(GpuFilter.Linear, GpuAddressMode.Clamp));
        _bindings.SetConstants(0, _constants);
        _bindings.SetSampler(0, _sampler);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Float4(float x, float y, float z, float w)
{
    public float X = x;
    public float Y = y;
    public float Z = z;
    public float W = w;
}

/// <summary>Constantes de <c>RenderShaders</c>, en el orden del <c>cbuffer Quad</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct QuadConstants
{
    public Float4 TopLeft;
    public Float4 TopRight;
    public Float4 BottomLeft;
    public Float4 BottomRight;
    public Float4 LocalTopLeft;
    public Float4 LocalTopRight;
    public Float4 LocalBottomLeft;
    public Float4 LocalBottomRight;
    public Float4 Range;
    public Float4 Limits;
    public Float4 ToRgb;
    public Float4 Shape;
    public Float4 Present;
    public Float4 PixelOrigin;
    public Float4 PerPixelX;
    public Float4 PerPixelY;
    public Float4 Hdr;

    /// <summary>Un cuadrilátero que llena el destino, con uv y coordenadas locales de 0 a 1.</summary>
    public static QuadConstants FullTarget() => new()
    {
        TopLeft = new(-1, 1, 0, 0),
        TopRight = new(1, 1, 1, 0),
        BottomLeft = new(-1, -1, 0, 1),
        BottomRight = new(1, -1, 1, 1),
        LocalTopLeft = new(0, 0, 0, 0),
        LocalTopRight = new(1, 0, 0, 0),
        LocalBottomLeft = new(0, 1, 0, 0),
        LocalBottomRight = new(1, 1, 0, 0),
        Shape = new(1, 1e6f, 1, 1),
    };

    /// <summary>
    /// Cómo cambian uv y las coordenadas locales por píxel de destino desde la esquina superior izquierda, para que los
    /// shaders las calculen desde <c>SV_Position</c> en lugar de interpolarlas. WARP recorta en el viewport los triángulos
    /// que se salen (el margen de un píxel del borde suavizado) y los atributos de los vértices nuevos se desvían hasta
    /// ~1/150 de texel: en un borde fuerte, 1,5/255 por cada pasada. Solo si el cuadrilátero es un paralelogramo (afín).
    /// </summary>
    internal void SetPixelGradients(int width, int height)
    {
        (double X, double Y) Pixel(Float4 corner) => ((corner.X + 1) * 0.5 * width, (1 - corner.Y) * 0.5 * height);
        (double x0, double y0) = Pixel(TopLeft);
        (double x1, double y1) = Pixel(TopRight);
        (double x2, double y2) = Pixel(BottomLeft);
        (double x3, double y3) = Pixel(BottomRight);
        double ax = x1 - x0, ay = y1 - y0, bx = x2 - x0, by = y2 - y0;
        double det = (ax * by) - (ay * bx);
        PixelOrigin = default;

        // Con perspectiva (anclaje de esquinas) uv y local no son lineales en pantalla: se interpolan con la w de cada esquina.
        if (Present.Z > 0 || Math.Abs(det) < 1e-6 || Math.Abs(x3 - x1 - x2 + x0) > 1e-2 || Math.Abs(y3 - y1 - y2 + y0) > 1e-2)
        {
            return;
        }

        Span<double> a0 = [TopLeft.Z, TopLeft.W, LocalTopLeft.X, LocalTopLeft.Y];
        Span<double> a1 = [TopRight.Z, TopRight.W, LocalTopRight.X, LocalTopRight.Y];
        Span<double> a2 = [BottomLeft.Z, BottomLeft.W, LocalBottomLeft.X, LocalBottomLeft.Y];
        Span<double> a3 = [BottomRight.Z, BottomRight.W, LocalBottomRight.X, LocalBottomRight.Y];
        Span<float> dx = stackalloc float[4];
        Span<float> dy = stackalloc float[4];
        for (int k = 0; k < 4; k++)
        {
            double d1 = a1[k] - a0[k];
            double d2 = a2[k] - a0[k];
            if (Math.Abs(a3[k] - a1[k] - a2[k] + a0[k]) > 1e-4 * (Math.Abs(d1) + Math.Abs(d2) + 1))
            {
                return;
            }

            dx[k] = (float)(((d1 * by) - (d2 * ay)) / det);
            dy[k] = (float)(((d2 * ax) - (d1 * bx)) / det);
        }

        PixelOrigin = new((float)x0, (float)y0, 1, 0);
        PerPixelX = new(dx[0], dx[1], dx[2], dx[3]);
        PerPixelY = new(dy[0], dy[1], dy[2], dy[3]);
    }
}
