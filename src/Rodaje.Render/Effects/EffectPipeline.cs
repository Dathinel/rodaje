using System.Runtime.InteropServices;
using Rodaje.Playback.Composition;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>Constantes de los efectos, en el orden del <c>cbuffer Effects</c> de <see cref="EffectShaders"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct EffectConstants
{
    public Float4 Grade;
    public Float4 Balance;
    public Float4 Tones;
    public Float4 Vignette;
    public Float4 BlendInfo;
    public Float4 MapA;
    public Float4 MapB;
    public Float4 Kernel;
    public Float4 Mix0;
    public Float4 Mix1;
    public Float4 Canvas;
}

/// <summary>
/// Shaders, constantes y estados de mezcla de los efectos. Los shaders se crean en segundo plano (de la caché en disco o
/// compilándolos); mientras uno no está, <see cref="TryGetShader"/> devuelve <c>null</c> y el cuadro sigue sin él.
/// </summary>
internal sealed unsafe class EffectPipeline : IDisposable
{
    private readonly ID3D11DeviceContext* _context;
    private readonly nint[] _shaders = new nint[EffectShaders.All.Length];
    private readonly Lock _gate = new();
    private ID3D11Buffer* _constants;
    private ID3D11BlendState* _over;
    private ID3D11BlendState* _add;
    private ID3D11BlendState* _multiply;
    private ID3D11BlendState* _screen;
    private bool _disposed;

    public EffectPipeline(ID3D11Device* device, ID3D11DeviceContext* context, ShaderCache cache)
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

        // El hilo de fondo retiene el dispositivo hasta terminar, aunque el compositor se cierre antes.
        device->AddRef();
        nint retained = (nint)device;
        Ready = Task.Run(() =>
        {
            try
            {
                CreateShaders((ID3D11Device*)retained, cache);
            }
            finally
            {
                ((ID3D11Device*)retained)->Release();
            }
        });
    }

    /// <summary>Termina cuando todas las variantes están creadas (o falló alguna: la excepción queda aquí).</summary>
    public Task Ready { get; }

    /// <summary>Normal, suma, multiplicar y trama se resuelven con el estado de mezcla; el resto, en el shader.</summary>
    public static bool IsShaderBlend(BlendMode mode) =>
        mode is not (BlendMode.Normal or BlendMode.Add or BlendMode.Multiply or BlendMode.Screen);

    public ID3D11PixelShader* TryGetShader(EffectShader shader, int flags) =>
        (ID3D11PixelShader*)Volatile.Read(ref _shaders[EffectShaders.IndexOf(shader, flags)]);

    /// <summary>Estado de mezcla para un valor premultiplicado; los modos por shader usan "over".</summary>
    public ID3D11BlendState* BlendState(BlendMode mode) => mode switch
    {
        BlendMode.Add => _add,
        BlendMode.Multiply => _multiply,
        BlendMode.Screen => _screen,
        _ => _over,
    };

    public void BindConstants()
    {
        ID3D11Buffer* constants = _constants;
        _context->PSSetConstantBuffers(1, 1, &constants);
    }

    public void Set(in EffectConstants constants)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(_context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(efectos)");
        *(EffectConstants*)mapped.pData = constants;
        _context->Unmap((ID3D11Resource*)_constants, 0);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            for (int i = 0; i < _shaders.Length; i++)
            {
                var shader = (ID3D11PixelShader*)_shaders[i];
                Com.Release(ref shader);
                _shaders[i] = 0;
            }
        }

        Com.Release(ref _constants);
        Com.Release(ref _over);
        Com.Release(ref _add);
        Com.Release(ref _multiply);
        Com.Release(ref _screen);
    }

    private void CreateShaders(ID3D11Device* device, ShaderCache cache)
    {
        for (int i = 0; i < EffectShaders.All.Length; i++)
        {
            if (Volatile.Read(ref _disposed))
            {
                return;
            }

            byte[] code = cache.GetOrCompile(EffectShaders.All[i]);
            ID3D11PixelShader* shader;
            fixed (byte* bytes = code)
            {
                Check(device->CreatePixelShader(bytes, (nuint)code.Length, null, &shader), "CreatePixelShader(efecto)");
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    shader->Release();
                    return;
                }

                Volatile.Write(ref _shaders[i], (nint)shader);
            }
        }
    }

    private void Create(ID3D11Device* device)
    {
        var bufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(EffectConstants),
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* constants;
        Check(device->CreateBuffer(&bufferDesc, null, &constants), "CreateBuffer(efectos)");
        _constants = constants;

        // Con fuente premultiplicada S = Cs·a y fondo opaco Cb (fórmulas W3C con alfa):
        // normal  Cb·(1 − a) + S;  suma  Cb + S;  multiplicar  Cb·(1 − a) + S·Cb;  trama  Cb + S·(1 − Cb).
        _over = CreateBlend(device, D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA);
        _add = CreateBlend(device, D3D11_BLEND.D3D11_BLEND_ONE, D3D11_BLEND.D3D11_BLEND_ONE);
        _multiply = CreateBlend(device, D3D11_BLEND.D3D11_BLEND_DEST_COLOR, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA);
        _screen = CreateBlend(device, D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR, D3D11_BLEND.D3D11_BLEND_ONE);
    }

    private static ID3D11BlendState* CreateBlend(ID3D11Device* device, D3D11_BLEND source, D3D11_BLEND destination)
    {
        var desc = new D3D11_BLEND_DESC();
        desc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
        {
            BlendEnable = true,
            SrcBlend = source,
            DestBlend = destination,
            BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE,
            DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
        };
        ID3D11BlendState* state;
        Check(device->CreateBlendState(&desc, &state), "CreateBlendState(efecto)");
        return state;
    }
}
