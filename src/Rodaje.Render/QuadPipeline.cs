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

    public enum Shader
    {
        Layer,
        Present,
    }

    /// <summary>Deja el pipeline listo para dibujar cuadriláteros en <paramref name="target"/>; con el candado del contexto tomado.</summary>
    public void Bind(ID3D11RenderTargetView* target, int width, int height, Shader shader)
    {
        _context->OMSetRenderTargets(1, &target, null);
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
        bool layer = shader == Shader.Layer;
        _context->PSSetShader(layer ? _layer : _present, null, 0);
        _context->OMSetBlendState(layer ? _premultipliedOver : null, null, uint.MaxValue);
    }

    /// <summary>Sube las constantes y dibuja la tira de cuatro vértices.</summary>
    public void Draw(in QuadConstants constants)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes)");
        *(QuadConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);
        _context->Draw(4, 0);
    }

    /// <summary>Desenlaza las texturas y el destino, para que el decodificador pueda reutilizar sus superficies.</summary>
    public void Unbind()
    {
        ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[3];
        _context->PSSetShaderResources(0, 3, none);
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
}
