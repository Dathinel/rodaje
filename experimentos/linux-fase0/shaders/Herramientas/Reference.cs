using System.Runtime.InteropServices;
using Rodaje.Render;
using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static Rodaje.Render.RenderException;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace Fase0.Shaders;

/// <summary>
/// referencia: dibuja cada caso con D3D11 WARP como hoy (D3DCompile ps_5_0/vs_5_0, mismas constantes y muestreador que
/// QuadPipeline) en un objetivo RGBA16F de 64×64 y vuelca los flotantes a salida/ref/&lt;caso&gt;.bin.
/// </summary>
internal static unsafe class Reference
{
    private const int Slots = 12;

    /// <param name="hardware">Control: el mismo dibujo con el adaptador D3D11 por defecto (la GPU) en vez de WARP, en salida/ref-gpu.</param>
    public static int Run(string outDir, bool hardware = false)
    {
        string refDir = Path.Combine(outDir, hardware ? "ref-gpu" : "ref");
        Directory.CreateDirectory(refDir);
        foreach (string old in Directory.EnumerateFiles(refDir, "*.bin"))
        {
            File.Delete(old);
        }

        TextureSpec[] textures = Cases.TextureSet();
        CaseSpec[] cases = Cases.All();
        Json.Write(Path.Combine(refDir, "casos.json"), new CaseFile
        {
            Width = Cases.Size,
            Height = Cases.Size,
            Target = "rgba16f",
            TextureSet = textures,
            Cases = cases,
        }, Json.Default.CaseFile);

        using var gpu = new Warp(hardware);
        var views = textures.ToDictionary(t => t.Name, gpu.CreateTexture);
        var shaders = new Dictionary<int, nint>();
        int done = 0;
        foreach (CaseSpec c in cases)
        {
            if (!shaders.TryGetValue(c.Variant, out nint ps))
            {
                ShaderVariant v = EffectShaders.All[c.Variant];
                ps = gpu.CreatePixelShader(ShaderCompiler.Compile(v.FullSource(), v.EntryPoint, v.Profile, v.SourceName));
                shaders[c.Variant] = ps;
            }

            var bound = new nint[Slots];
            foreach (var (register, name) in c.Textures)
            {
                bound[int.Parse(register.AsSpan(1))] = views[name];
            }

            float[] pixels = gpu.Draw((ID3D11PixelShader*)ps, c.Quad, c.Effects, bound);
            File.WriteAllBytes(Path.Combine(refDir, c.Name + ".bin"), MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
            done++;
        }

        foreach (nint ps in shaders.Values)
        {
            ((ID3D11PixelShader*)ps)->Release();
        }

        foreach (nint view in views.Values)
        {
            ((ID3D11ShaderResourceView*)view)->Release();
        }

        int covered = cases.Select(c => c.Variant).Distinct().Count();
        Console.WriteLine($"Referencias con D3D11 {(hardware ? "en la GPU (" + gpu.AdapterName + ")" : "WARP")}: {done} casos, {covered} de {EffectShaders.All.Length} variantes, en {refDir}");
        return covered == EffectShaders.All.Length ? 0 : 1;
    }

    private sealed class Warp : IDisposable
    {
        private readonly ID3D11Device* _device;
        private readonly ID3D11DeviceContext* _context;
        private readonly ID3D11VertexShader* _vertex;
        private readonly ID3D11SamplerState* _sampler;
        private readonly ID3D11RasterizerState* _rasterizer;
        private readonly ID3D11Buffer* _quad;
        private readonly ID3D11Buffer* _effects;
        private readonly ID3D11Texture2D* _target;
        private readonly ID3D11RenderTargetView* _rtv;
        private readonly ID3D11Texture2D* _staging;

        public string AdapterName { get; } = "WARP";

        public Warp(bool hardware)
        {
            ID3D11Device* device;
            ID3D11DeviceContext* context;
            D3D_FEATURE_LEVEL level;
            D3D_FEATURE_LEVEL wanted = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
            IDXGIAdapter1* adapter = null;
            if (hardware)
            {
                adapter = PickAdapter(Environment.GetEnvironmentVariable("RODAJE_D3D_GPU"), out string name);
                AdapterName = name;
            }

            D3D_DRIVER_TYPE driver = hardware ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP;
            HRESULT created = D3D11CreateDevice((IDXGIAdapter*)adapter, driver, HMODULE.NULL, 0, &wanted, 1, D3D11_SDK_VERSION,
                &device, &level, &context);
            if (adapter != null)
            {
                adapter->Release();
            }

            Check(created, $"D3D11CreateDevice({AdapterName})");
            _device = device;
            _context = context;

            byte[] vs = RenderShaders.Vertex;
            ID3D11VertexShader* vertex;
            fixed (byte* p = vs)
            {
                Check(device->CreateVertexShader(p, (nuint)vs.Length, null, &vertex), "CreateVertexShader");
            }

            _vertex = vertex;
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
            var rasterizerDesc = new D3D11_RASTERIZER_DESC
            {
                FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
                CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
                DepthClipEnable = true,
            };
            ID3D11RasterizerState* rasterizer;
            Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
            _rasterizer = rasterizer;
            _quad = ConstantBuffer(17 * 16);
            _effects = ConstantBuffer(26 * 16);

            var targetDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = Cases.Size,
                Height = Cases.Size,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            };
            ID3D11Texture2D* target;
            Check(device->CreateTexture2D(&targetDesc, null, &target), "CreateTexture2D(objetivo)");
            _target = target;
            ID3D11RenderTargetView* rtv;
            Check(device->CreateRenderTargetView((ID3D11Resource*)target, null, &rtv), "CreateRenderTargetView");
            _rtv = rtv;
            targetDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            targetDesc.BindFlags = 0;
            targetDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            ID3D11Texture2D* staging;
            Check(device->CreateTexture2D(&targetDesc, null, &staging), "CreateTexture2D(lectura)");
            _staging = staging;
        }

        /// <summary>El adaptador de hardware cuyo nombre contiene <paramref name="filter"/> o, sin filtro, el de más VRAM propia.</summary>
        private static IDXGIAdapter1* PickAdapter(string? filter, out string bestName)
        {
            IDXGIFactory1* factory;
            Check(CreateDXGIFactory1(__uuidof<IDXGIFactory1>(), (void**)&factory), "CreateDXGIFactory1");
            IDXGIAdapter1* best = null;
            bestName = "";
            ulong bestMemory = 0;
            IDXGIAdapter1* adapter;
            for (uint i = 0; factory->EnumAdapters1(i, &adapter).SUCCEEDED; i++)
            {
                DXGI_ADAPTER_DESC1 desc;
                string name = adapter->GetDesc1(&desc).SUCCEEDED ? new string((char*)&desc.Description).TrimEnd('\0') : "";
                bool software = (desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                bool wanted = !software && (filter is { Length: > 0 }
                    ? name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    : best == null || desc.DedicatedVideoMemory > bestMemory);
                if (wanted && (best == null || filter is not { Length: > 0 }))
                {
                    if (best != null)
                    {
                        best->Release();
                    }

                    best = adapter;
                    (bestName, bestMemory) = (name, desc.DedicatedVideoMemory);
                }
                else
                {
                    adapter->Release();
                }
            }

            factory->Release();
            return best != null ? best : throw new InvalidOperationException($"No hay ningún adaptador D3D11 de hardware{(filter is { Length: > 0 } ? $" que contenga «{filter}»" : "")}.");
        }

        private ID3D11Buffer* ConstantBuffer(int bytes)
        {
            var desc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)bytes,
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            };
            ID3D11Buffer* buffer;
            Check(_device->CreateBuffer(&desc, null, &buffer), "CreateBuffer(constantes)");
            return buffer;
        }

        public nint CreatePixelShader(byte[] code)
        {
            ID3D11PixelShader* ps;
            fixed (byte* p = code)
            {
                Check(_device->CreatePixelShader(p, (nuint)code.Length, null, &ps), "CreatePixelShader");
            }

            return (nint)ps;
        }

        public nint CreateTexture(TextureSpec t)
        {
            byte[] bytes = Patterns.Bytes(t);
            if (Patterns.Hash(bytes) != t.Hash)
            {
                throw new InvalidDataException($"{t.Name}: el hash no coincide.");
            }

            DXGI_FORMAT format = t.Format switch
            {
                "rgba16f" => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                "rg16f" => DXGI_FORMAT.DXGI_FORMAT_R16G16_FLOAT,
                "r16f" => DXGI_FORMAT.DXGI_FORMAT_R16_FLOAT,
                "r32f" => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
                "rgba32f" => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                _ => throw new InvalidDataException(t.Format),
            };
            uint pitch = (uint)(t.Width * Patterns.BytesPerTexel(t.Format));
            ID3D11Resource* resource;
            var srv = new D3D11_SHADER_RESOURCE_VIEW_DESC { Format = format };
            fixed (byte* data = bytes)
            {
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = pitch, SysMemSlicePitch = pitch * (uint)t.Height };
                if (t.Dimension == "3d")
                {
                    var desc = new D3D11_TEXTURE3D_DESC
                    {
                        Width = (uint)t.Width, Height = (uint)t.Height, Depth = (uint)t.Depth, MipLevels = 1, Format = format,
                        Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                    };
                    ID3D11Texture3D* texture;
                    Check(_device->CreateTexture3D(&desc, &init, &texture), "CreateTexture3D");
                    resource = (ID3D11Resource*)texture;
                    srv.ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE3D;
                    srv.Texture3D.MipLevels = 1;
                }
                else
                {
                    var desc = new D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)t.Width, Height = (uint)t.Height, MipLevels = 1, ArraySize = 1, Format = format,
                        SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
                        BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                    };
                    ID3D11Texture2D* texture;
                    Check(_device->CreateTexture2D(&desc, &init, &texture), "CreateTexture2D");
                    resource = (ID3D11Resource*)texture;
                    if (t.Dimension == "2d-array")
                    {
                        srv.ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
                        srv.Texture2DArray.MipLevels = 1;
                        srv.Texture2DArray.ArraySize = 1;
                    }
                    else
                    {
                        srv.ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D;
                        srv.Texture2D.MipLevels = 1;
                    }
                }
            }

            ID3D11ShaderResourceView* view;
            Check(_device->CreateShaderResourceView(resource, &srv, &view), "CreateShaderResourceView");
            resource->Release();
            return (nint)view;
        }

        public float[] Draw(ID3D11PixelShader* ps, float[] quad, float[] effects, nint[] textures)
        {
            fixed (float* q = quad, e = effects)
            {
                _context->UpdateSubresource((ID3D11Resource*)_quad, 0, null, q, 0, 0);
                _context->UpdateSubresource((ID3D11Resource*)_effects, 0, null, e, 0, 0);
            }

            float* clear = stackalloc float[4];
            ID3D11RenderTargetView* rtv = _rtv;
            _context->ClearRenderTargetView(rtv, clear);
            _context->OMSetRenderTargets(1, &rtv, null);
            var viewport = new D3D11_VIEWPORT { Width = Cases.Size, Height = Cases.Size, MaxDepth = 1 };
            _context->RSSetViewports(1, &viewport);
            _context->RSSetState(_rasterizer);
            _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            _context->IASetInputLayout(null);
            _context->VSSetShader(_vertex, null, 0);
            ID3D11Buffer** buffers = stackalloc ID3D11Buffer*[2] { _quad, _effects };
            _context->VSSetConstantBuffers(0, 1, buffers);
            _context->PSSetConstantBuffers(0, 2, buffers);
            ID3D11SamplerState* sampler = _sampler;
            _context->PSSetSamplers(0, 1, &sampler);
            _context->PSSetShader(ps, null, 0);
            _context->OMSetBlendState(null, null, uint.MaxValue);
            ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[Slots];
            for (int i = 0; i < Slots; i++)
            {
                srvs[i] = (ID3D11ShaderResourceView*)textures[i];
            }

            _context->PSSetShaderResources(0, Slots, srvs);
            _context->Draw(4, 0);
            _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_target);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura)");
            var result = new float[Cases.Size * Cases.Size * 4];
            for (int y = 0; y < Cases.Size; y++)
            {
                var row = new ReadOnlySpan<Half>((byte*)mapped.pData + (y * mapped.RowPitch), Cases.Size * 4);
                for (int i = 0; i < row.Length; i++)
                {
                    result[(y * Cases.Size * 4) + i] = (float)row[i];
                }
            }

            _context->Unmap((ID3D11Resource*)_staging, 0);
            ID3D11ShaderResourceView** none = stackalloc ID3D11ShaderResourceView*[Slots];
            _context->PSSetShaderResources(0, Slots, none);
            return result;
        }

        public void Dispose()
        {
            _staging->Release();
            _rtv->Release();
            _target->Release();
            _effects->Release();
            _quad->Release();
            _rasterizer->Release();
            _sampler->Release();
            _vertex->Release();
            _context->Release();
            _device->Release();
        }
    }
}
