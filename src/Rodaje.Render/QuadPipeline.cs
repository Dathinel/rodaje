using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Estado de Direct3D común al compositor y al presentador: shaders, constantes, muestreador y mezclas.</summary>
internal sealed unsafe class QuadPipeline : IDisposable
{
    private readonly ID3D11DeviceContext* _context;
    private ID3D11VertexShader* _vertex;
    private ID3D11PixelShader* _layer;
    private ID3D11PixelShader* _present;
    private ID3D11Buffer* _constants;
    private ID3D11SamplerState* _sampler;
    private ID3D11BlendState* _premultipliedOver;
    private ID3D11RasterizerState* _noCulling;
    private int _width;
    private int _height;

    public QuadPipeline(ID3D11Device* device, ID3D11DeviceContext* context)
    {
        _context = context;
        try
        {
            Create(device);
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

    /// <summary>Deja el pipeline listo para dibujar cuadriláteros en <paramref name="target"/>; con el candado del contexto tomado.</summary>
    public void Bind(ID3D11RenderTargetView* target, int width, int height, Shader shader)
    {
        bool layer = shader == Shader.Layer;
        Bind(target, width, height, layer ? _layer : _present, layer ? _premultipliedOver : null);
    }

    /// <summary>Como <see cref="Bind(ID3D11RenderTargetView*, int, int, Shader)"/> con otro pixel shader y otra mezcla (<c>null</c>: sin mezcla).</summary>
    public void Bind(ID3D11RenderTargetView* target, int width, int height, ID3D11PixelShader* pixelShader, ID3D11BlendState* blend)
    {
        _context->OMSetRenderTargets(1, &target, null);
        _width = width;
        _height = height;
        var viewport = new D3D11_VIEWPORT { Width = width, Height = height, MaxDepth = 1 };
        _context->RSSetViewports(1, &viewport);
        _context->RSSetState(_noCulling);
        _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        _context->IASetInputLayout(null);
        _context->VSSetShader(_vertex, null, 0);
        ID3D11Buffer* constants = _constants;
        _context->VSSetConstantBuffers(0, 1, &constants);
        _context->PSSetConstantBuffers(0, 1, &constants);
        ID3D11SamplerState* sampler = _sampler;
        _context->PSSetSamplers(0, 1, &sampler);
        _context->PSSetShader(pixelShader, null, 0);
        _context->OMSetBlendState(blend, null, uint.MaxValue);
    }

    /// <summary>Sube las constantes y dibuja la tira de cuatro vértices.</summary>
    public void Draw(in QuadConstants constants)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes)");
        QuadConstants exact = constants;
        exact.SetPixelGradients(_width, _height);
        *(QuadConstants*)mapped.pData = exact;
        _context->Unmap((ID3D11Resource*)_constants, 0);
        _context->Draw(4, 0);
    }

    /// <summary>Desenlaza las texturas y el destino, para que el decodificador pueda reutilizar sus superficies.</summary>
    public void Unbind()
    {
        ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[InputSlots];
        _context->PSSetShaderResources(0, InputSlots, none);
        _context->OMSetRenderTargets(0, null, null);
    }

    public void Dispose()
    {
        Com.Release(ref _vertex);
        Com.Release(ref _layer);
        Com.Release(ref _present);
        Com.Release(ref _constants);
        Com.Release(ref _sampler);
        Com.Release(ref _premultipliedOver);
        Com.Release(ref _noCulling);
    }

    private void Create(ID3D11Device* device)
    {
        byte[] vs = RenderShaders.Vertex;
        byte[] layer = RenderShaders.Layer;
        byte[] present = RenderShaders.Present;
        fixed (byte* v = vs, l = layer, p = present)
        {
            ID3D11VertexShader* vertex;
            Check(device->CreateVertexShader(v, (nuint)vs.Length, null, &vertex), "CreateVertexShader");
            _vertex = vertex;
            ID3D11PixelShader* pixel;
            Check(device->CreatePixelShader(l, (nuint)layer.Length, null, &pixel), "CreatePixelShader(capa)");
            _layer = pixel;
            Check(device->CreatePixelShader(p, (nuint)present.Length, null, &pixel), "CreatePixelShader(presentación)");
            _present = pixel;
        }

        var bufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(QuadConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* constants;
        Check(device->CreateBuffer(&bufferDesc, null, &constants), "CreateBuffer(constantes)");
        _constants = constants;

        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxLOD = D3D11_FLOAT32_MAX,
        };
        ID3D11SamplerState* sampler;
        Check(device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");
        _sampler = sampler;

        // "Over" con alfa premultiplicado: destino = fuente + destino · (1 − alfa de la fuente), también en alfa.
        var blendDesc = new D3D11_BLEND_DESC();
        blendDesc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = true,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        ID3D11BlendState* blend;
        Check(device->CreateBlendState(&blendDesc, &blend), "CreateBlendState");
        _premultipliedOver = blend;

        // Una capa reflejada (escala negativa en el futuro) invierte el sentido de los triángulos: no se descarta ninguno.
        var rasterizerDesc = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
            DepthClipEnable = true,
        };
        ID3D11RasterizerState* rasterizer;
        Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _noCulling = rasterizer;
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
