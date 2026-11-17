using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Gpu.D3D11;

/// <summary>Una textura con las vistas que pide su uso. Las vistas por defecto abarcan todos los mips (lectura) o el 0 (escritura).</summary>
internal sealed unsafe class D3D11Texture : GpuTexture
{
    private ID3D11Resource* _resource;
    private ID3D11ShaderResourceView* _srv;
    private ID3D11RenderTargetView* _rtv;
    private ID3D11UnorderedAccessView* _uav;
    private ID3D11DepthStencilView* _dsv;

    private D3D11Texture(GpuTextureDesc desc, ID3D11Resource* resource)
        : base(desc)
    {
        _resource = resource;
    }

    public ID3D11Resource* Resource => _resource;

    public ID3D11Texture2D* Texture2D => (ID3D11Texture2D*)_resource;

    public ID3D11ShaderResourceView* Srv => _srv;

    public ID3D11RenderTargetView* Rtv => _rtv;

    public ID3D11UnorderedAccessView* Uav => _uav;

    public ID3D11DepthStencilView* Dsv => _dsv;

    public static D3D11Texture Create(D3D11Gpu gpu, GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Depth);
        bool depth = (desc.Usage & GpuTextureUsage.DepthStencil) != 0;
        bool sampled = (desc.Usage & GpuTextureUsage.Sampled) != 0;
        if (!initialData.IsEmpty && desc.MipLevels != 1)
        {
            throw new ArgumentException("El contenido inicial solo va con un mip.", nameof(desc));
        }

        // Profundidad que además se muestrea: el recurso va sin tipo y cada vista pone el suyo.
        DXGI_FORMAT format = depth && sampled ? DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS : D3D11Gpu.Native(desc.Format);
        uint bind = 0;
        if (sampled) bind |= (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        if ((desc.Usage & GpuTextureUsage.RenderTarget) != 0) bind |= (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
        if ((desc.Usage & GpuTextureUsage.Storage) != 0) bind |= (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS;
        if (depth) bind |= (uint)D3D11_BIND_FLAG.D3D11_BIND_DEPTH_STENCIL;
        uint misc = (desc.Usage & GpuTextureUsage.GenerateMips) != 0 ? (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;
        int pitch = desc.Width * GpuFormats.BytesPerPixel(desc.Format);
        ID3D11Resource* resource;
        fixed (byte* data = initialData)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = (uint)pitch, SysMemSlicePitch = (uint)(pitch * desc.Height) };
            D3D11_SUBRESOURCE_DATA* first = initialData.IsEmpty ? null : &initial;
            if (desc.Depth > 1)
            {
                var native = new D3D11_TEXTURE3D_DESC
                {
                    Width = (uint)desc.Width,
                    Height = (uint)desc.Height,
                    Depth = (uint)desc.Depth,
                    MipLevels = (uint)desc.MipLevels,
                    Format = format,
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = bind,
                    MiscFlags = misc,
                };
                ID3D11Texture3D* texture;
                Check(gpu.NativeDevice->CreateTexture3D(&native, first, &texture), "CreateTexture3D");
                resource = (ID3D11Resource*)texture;
            }
            else
            {
                var native = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)desc.Width,
                    Height = (uint)desc.Height,
                    MipLevels = (uint)desc.MipLevels,
                    ArraySize = 1,
                    Format = format,
                    SampleDesc = new DXGI_SAMPLE_DESC((uint)desc.Samples, 0),
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = bind,
                    MiscFlags = misc,
                };
                ID3D11Texture2D* texture;
                Check(gpu.NativeDevice->CreateTexture2D(&native, first, &texture), "CreateTexture2D");
                resource = (ID3D11Resource*)texture;
            }
        }

        var created = new D3D11Texture(desc, resource);
        try
        {
            created.CreateViews(gpu.NativeDevice, depth && sampled);
        }
        catch
        {
            created.Dispose();
            throw;
        }

        return created;
    }

    public static D3D11Texture Wrap(D3D11Gpu gpu, ID3D11Texture2D* texture, in GpuTextureDesc desc, ID3D11ShaderResourceView* srv,
        ID3D11RenderTargetView* rtv)
    {
        texture->AddRef();
        var wrapped = new D3D11Texture(desc, (ID3D11Resource*)texture) { _srv = srv, _rtv = rtv };
        if (srv != null) srv->AddRef();
        if (rtv != null) rtv->AddRef();
        return wrapped;
    }

    protected override void Release()
    {
        Com.Release(ref _srv);
        Com.Release(ref _rtv);
        Com.Release(ref _uav);
        Com.Release(ref _dsv);
        Com.Release(ref _resource);
    }

    private void CreateViews(ID3D11Device* device, bool typelessDepth)
    {
        GpuTextureUsage usage = Desc.Usage;
        bool ms = Desc.Samples > 1;
        if ((usage & GpuTextureUsage.Sampled) != 0)
        {
            ID3D11ShaderResourceView* srv;
            if (typelessDepth)
            {
                var view = new D3D11_SHADER_RESOURCE_VIEW_DESC
                {
                    Format = DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
                    ViewDimension = ms ? D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DMS : D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2D,
                };
                view.Texture2D.MipLevels = 1;
                Check(device->CreateShaderResourceView(_resource, &view, &srv), "CreateShaderResourceView(profundidad)");
            }
            else
            {
                Check(device->CreateShaderResourceView(_resource, null, &srv), "CreateShaderResourceView");
            }

            _srv = srv;
        }

        if ((usage & GpuTextureUsage.RenderTarget) != 0)
        {
            ID3D11RenderTargetView* rtv;
            Check(device->CreateRenderTargetView(_resource, null, &rtv), "CreateRenderTargetView");
            _rtv = rtv;
        }

        if ((usage & GpuTextureUsage.Storage) != 0)
        {
            ID3D11UnorderedAccessView* uav;
            Check(device->CreateUnorderedAccessView(_resource, null, &uav), "CreateUnorderedAccessView");
            _uav = uav;
        }

        if ((usage & GpuTextureUsage.DepthStencil) != 0)
        {
            var view = new D3D11_DEPTH_STENCIL_VIEW_DESC
            {
                Format = DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
                ViewDimension = ms ? D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2DMS : D3D11_DSV_DIMENSION.D3D11_DSV_DIMENSION_TEXTURE2D,
            };
            ID3D11DepthStencilView* dsv;
            Check(device->CreateDepthStencilView(_resource, &view, &dsv), "CreateDepthStencilView");
            _dsv = dsv;
        }
    }
}

internal sealed unsafe class D3D11Buffer : GpuBuffer
{
    private ID3D11Buffer* _buffer;
    private ID3D11ShaderResourceView* _srv;
    private ID3D11UnorderedAccessView* _uav;

    private D3D11Buffer(GpuBufferDesc desc, ID3D11Buffer* buffer)
        : base(desc)
    {
        _buffer = buffer;
    }

    public ID3D11Buffer* Native => _buffer;

    public ID3D11ShaderResourceView* Srv => _srv;

    public ID3D11UnorderedAccessView* Uav => _uav;

    /// <summary>Se reescribe con <c>Map(WRITE_DISCARD)</c> (las constantes); el resto, con <c>UpdateSubresource</c>.</summary>
    public bool IsDynamic => Desc.Kind == GpuBufferKind.Constant;

    public static D3D11Buffer Create(D3D11Gpu gpu, GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Size);
        if (initialData.Length > desc.Size)
        {
            throw new ArgumentException("El contenido inicial no cabe en el búfer.", nameof(initialData));
        }

        var native = new D3D11_BUFFER_DESC { ByteWidth = (uint)desc.Size, Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT };
        switch (desc.Kind)
        {
            case GpuBufferKind.Constant:
                if (desc.Size % 16 != 0) throw new ArgumentException("Un búfer de constantes mide un múltiplo de 16 bytes.", nameof(desc));
                native.Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC;
                native.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER;
                native.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE;
                break;
            case GpuBufferKind.Vertex:
                native.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_VERTEX_BUFFER;
                break;
            case GpuBufferKind.Index:
                native.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_INDEX_BUFFER;
                break;
            case GpuBufferKind.Structured:
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Stride);
                native.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
                native.MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
                native.StructureByteStride = (uint)desc.Stride;
                break;
            case GpuBufferKind.Raw:
                if (desc.Size % 4 != 0) throw new ArgumentException("Un búfer sin tipo mide un múltiplo de 4 bytes.", nameof(desc));
                native.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
                native.MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
                break;
        }

        if (desc.Storage)
        {
            native.BindFlags |= (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS;
        }

        ID3D11Buffer* buffer;
        fixed (byte* data = initialData)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(gpu.NativeDevice->CreateBuffer(&native, initialData.IsEmpty ? null : &initial, &buffer), "CreateBuffer");
        }

        var created = new D3D11Buffer(desc, buffer);
        try
        {
            created.CreateViews(gpu.NativeDevice);
        }
        catch
        {
            created.Dispose();
            throw;
        }

        return created;
    }

    protected override void Release()
    {
        Com.Release(ref _srv);
        Com.Release(ref _uav);
        Com.Release(ref _buffer);
    }

    private void CreateViews(ID3D11Device* device)
    {
        if (Desc.Kind is not (GpuBufferKind.Structured or GpuBufferKind.Raw))
        {
            return;
        }

        bool raw = Desc.Kind == GpuBufferKind.Raw;
        uint elements = (uint)(raw ? Desc.Size / 4 : Desc.Size / Desc.Stride);
        var srvDesc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = raw ? DXGI_FORMAT.DXGI_FORMAT_R32_TYPELESS : DXGI_FORMAT.DXGI_FORMAT_UNKNOWN,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFEREX,
        };
        srvDesc.BufferEx.NumElements = elements;
        srvDesc.BufferEx.Flags = raw ? (uint)D3D11_BUFFEREX_SRV_FLAG.D3D11_BUFFEREX_SRV_FLAG_RAW : 0;
        ID3D11ShaderResourceView* srv;
        Check(device->CreateShaderResourceView((ID3D11Resource*)_buffer, &srvDesc, &srv), "CreateShaderResourceView(búfer)");
        _srv = srv;
        if (!Desc.Storage)
        {
            return;
        }

        var uavDesc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = srvDesc.Format,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER,
        };
        uavDesc.Buffer.NumElements = elements;
        uavDesc.Buffer.Flags = raw ? (uint)D3D11_BUFFER_UAV_FLAG.D3D11_BUFFER_UAV_FLAG_RAW : 0;
        ID3D11UnorderedAccessView* uav;
        Check(device->CreateUnorderedAccessView((ID3D11Resource*)_buffer, &uavDesc, &uav), "CreateUnorderedAccessView(búfer)");
        _uav = uav;
    }
}

internal sealed unsafe class D3D11Sampler(ID3D11SamplerState* state) : GpuSampler
{
    private ID3D11SamplerState* _state = state;

    public ID3D11SamplerState* Native => _state;

    protected override void Release() => Com.Release(ref _state);
}

internal sealed unsafe class D3D11Shader(ID3D11DeviceChild* shader, string stage, byte[]? vertexCode) : GpuShader
{
    private ID3D11DeviceChild* _shader = shader;

    /// <summary><c>vs</c>, <c>ps</c> o <c>cs</c>.</summary>
    public string Stage { get; } = stage;

    /// <summary>El bytecode de un sombreador de vértices, para la disposición de entrada.</summary>
    public byte[]? VertexCode { get; } = vertexCode;

    public ID3D11DeviceChild* Native => _shader;

    protected override void Release() => Com.Release(ref _shader);
}

internal sealed unsafe class D3D11Pipeline : GpuPipeline
{
    private ID3D11BlendState* _blend;
    private ID3D11RasterizerState* _rasterizer;
    private ID3D11DepthStencilState* _depth;
    private ID3D11InputLayout* _layout;

    private D3D11Pipeline(GpuPipelineDesc desc)
        : base(desc)
    {
    }

    public ID3D11BlendState* Blend => _blend;

    public ID3D11RasterizerState* Rasterizer => _rasterizer;

    public ID3D11DepthStencilState* DepthState => _depth;

    public ID3D11InputLayout* Layout => _layout;

    public D3D_PRIMITIVE_TOPOLOGY Topology { get; private init; }

    public D3D11Shader? VertexShader => (D3D11Shader?)Desc.Vertex;

    public D3D11Shader? PixelShader => (D3D11Shader?)Desc.Pixel;

    public D3D11Shader? ComputeShader => (D3D11Shader?)Desc.Compute;

    public static D3D11Pipeline Create(D3D11Gpu gpu, GpuPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        if (desc.Compute is not null)
        {
            if (desc.Vertex is not null || desc.Pixel is not null)
            {
                throw new ArgumentException("Una canalización de cómputo no lleva sombreadores de dibujo.", nameof(desc));
            }

            Expect(desc.Compute, "cs");
            return new D3D11Pipeline(desc);
        }

        if (desc.Vertex is null || desc.Pixel is null)
        {
            throw new ArgumentException("Para dibujar hacen falta un sombreador de vértices y uno de píxeles.", nameof(desc));
        }

        Expect(desc.Vertex, "vs");
        Expect(desc.Pixel, "ps");
        var pipeline = new D3D11Pipeline(desc) { Topology = NativeTopology(desc.Raster.Topology) };
        try
        {
            pipeline.CreateStates(gpu.NativeDevice);
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }

        return pipeline;
    }

    protected override void Release()
    {
        Com.Release(ref _blend);
        Com.Release(ref _rasterizer);
        Com.Release(ref _depth);
        Com.Release(ref _layout);
    }

    private static void Expect(GpuShader shader, string stage)
    {
        if (shader is not D3D11Shader native || native.Stage != stage)
        {
            throw new ArgumentException($"Se esperaba un sombreador {stage} de D3D11 y llegó {shader.Id}.", nameof(shader));
        }
    }

    private static D3D_PRIMITIVE_TOPOLOGY NativeTopology(GpuTopology topology) => topology switch
    {
        GpuTopology.TriangleList => D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
        GpuTopology.LineList => D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINELIST,
        GpuTopology.LineStrip => D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_LINESTRIP,
        _ => D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP,
    };

    private static D3D11_BLEND Factor(GpuBlendFactor factor) => factor switch
    {
        GpuBlendFactor.Zero => D3D11_BLEND.D3D11_BLEND_ZERO,
        GpuBlendFactor.One => D3D11_BLEND.D3D11_BLEND_ONE,
        GpuBlendFactor.SourceColor => D3D11_BLEND.D3D11_BLEND_SRC_COLOR,
        GpuBlendFactor.InverseSourceColor => D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR,
        GpuBlendFactor.SourceAlpha => D3D11_BLEND.D3D11_BLEND_SRC_ALPHA,
        GpuBlendFactor.InverseSourceAlpha => D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
        GpuBlendFactor.DestinationColor => D3D11_BLEND.D3D11_BLEND_DEST_COLOR,
        GpuBlendFactor.InverseDestinationColor => D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR,
        GpuBlendFactor.DestinationAlpha => D3D11_BLEND.D3D11_BLEND_DEST_ALPHA,
        _ => D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA,
    };

    private static D3D11_COMPARISON_FUNC Compare(GpuCompare compare) => compare switch
    {
        GpuCompare.Never => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
        GpuCompare.Less => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS,
        GpuCompare.Equal => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_EQUAL,
        GpuCompare.LessEqual => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_LESS_EQUAL,
        GpuCompare.Greater => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_GREATER,
        GpuCompare.NotEqual => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NOT_EQUAL,
        GpuCompare.GreaterEqual => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_GREATER_EQUAL,
        _ => D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
    };

    private static DXGI_FORMAT VertexFormat(GpuVertexFormat format) => format switch
    {
        GpuVertexFormat.Float1 => DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT,
        GpuVertexFormat.Float2 => DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
        GpuVertexFormat.Float3 => DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
        GpuVertexFormat.Float4 => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
        GpuVertexFormat.Unorm8x4 => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        _ => DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
    };

    private void CreateStates(ID3D11Device* device)
    {
        GpuPipelineDesc desc = Desc;
        if (!desc.Blend.IsOpaque)
        {
            var blendDesc = new D3D11_BLEND_DESC();
            blendDesc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC
            {
                BlendEnable = true,
                SrcBlend = Factor(desc.Blend.Source),
                DestBlend = Factor(desc.Blend.Destination),
                BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                SrcBlendAlpha = Factor(desc.Blend.SourceAlpha),
                DestBlendAlpha = Factor(desc.Blend.DestinationAlpha),
                BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
                RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL,
            };
            ID3D11BlendState* blend;
            Check(device->CreateBlendState(&blendDesc, &blend), "CreateBlendState");
            _blend = blend;
        }

        var rasterizerDesc = new D3D11_RASTERIZER_DESC
        {
            FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
            CullMode = desc.Raster.Cull switch
            {
                GpuCullMode.Back => D3D11_CULL_MODE.D3D11_CULL_BACK,
                GpuCullMode.Front => D3D11_CULL_MODE.D3D11_CULL_FRONT,
                _ => D3D11_CULL_MODE.D3D11_CULL_NONE,
            },
            DepthBias = desc.Raster.DepthBias,
            DepthBiasClamp = desc.Raster.DepthBiasClamp,
            SlopeScaledDepthBias = desc.Raster.SlopeScaledDepthBias,
            DepthClipEnable = true,
        };
        ID3D11RasterizerState* rasterizer;
        Check(device->CreateRasterizerState(&rasterizerDesc, &rasterizer), "CreateRasterizerState");
        _rasterizer = rasterizer;

        // Sin estado propio vale el de D3D11 por defecto, que sin destino de profundidad no prueba nada.
        if (desc.Depth.Test || desc.Depth.Write)
        {
            var depthDesc = new D3D11_DEPTH_STENCIL_DESC
            {
                DepthEnable = true,
                DepthWriteMask = desc.Depth.Write ? D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ALL : D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
                DepthFunc = desc.Depth.Test ? Compare(desc.Depth.Compare) : D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
            };
            ID3D11DepthStencilState* depth;
            Check(device->CreateDepthStencilState(&depthDesc, &depth), "CreateDepthStencilState");
            _depth = depth;
        }

        if (desc.VertexLayout is { } layout)
        {
            byte[] code = VertexShader!.VertexCode!;
            int count = layout.Elements.Length;
            var names = new byte[count][];
            var elements = stackalloc D3D11_INPUT_ELEMENT_DESC[count];
            var handles = new System.Runtime.InteropServices.GCHandle[count];
            try
            {
                for (int i = 0; i < count; i++)
                {
                    GpuVertexElement element = layout.Elements[i];
                    names[i] = Encoding.ASCII.GetBytes(element.Semantic + "\0");
                    handles[i] = System.Runtime.InteropServices.GCHandle.Alloc(names[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                    elements[i] = new D3D11_INPUT_ELEMENT_DESC
                    {
                        SemanticName = (sbyte*)handles[i].AddrOfPinnedObject(),
                        SemanticIndex = (uint)element.SemanticIndex,
                        Format = VertexFormat(element.Format),
                        AlignedByteOffset = (uint)element.Offset,
                        InputSlotClass = D3D11_INPUT_CLASSIFICATION.D3D11_INPUT_PER_VERTEX_DATA,
                    };
                }

                ID3D11InputLayout* inputLayout;
                fixed (byte* bytes = code)
                {
                    Check(device->CreateInputLayout(elements, (uint)count, bytes, (nuint)code.Length, &inputLayout), "CreateInputLayout");
                }

                _layout = inputLayout;
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated) handle.Free();
                }
            }
        }
    }
}

/// <summary>Consultas <c>TIMESTAMP</c> y <c>TIMESTAMP_DISJOINT</c>, como el <c>GpuTimer</c> de siempre.</summary>
internal sealed unsafe class D3D11TimerQuery : GpuTimerQuery
{
    private readonly D3D11Gpu _gpu;
    private ID3D11Query* _disjoint;
    private ID3D11Query* _start;
    private ID3D11Query* _end;
    private bool _flushed;

    public D3D11TimerQuery(D3D11Gpu gpu)
    {
        _gpu = gpu;
        try
        {
            _disjoint = CreateQuery(gpu.NativeDevice, D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT);
            _start = CreateQuery(gpu.NativeDevice, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
            _end = CreateQuery(gpu.NativeDevice, D3D11_QUERY.D3D11_QUERY_TIMESTAMP);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Begin(ID3D11DeviceContext* context)
    {
        _flushed = false;
        context->Begin((ID3D11Asynchronous*)_disjoint);
        context->End((ID3D11Asynchronous*)_start);
    }

    public void End(ID3D11DeviceContext* context)
    {
        context->End((ID3D11Asynchronous*)_end);
        context->End((ID3D11Asynchronous*)_disjoint);
    }

    public override bool TryGetMilliseconds(out double? milliseconds)
    {
        milliseconds = null;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint;
        ulong start;
        ulong end;
        _gpu.Device.LockContext();
        try
        {
            ID3D11DeviceContext* context = _gpu.Context;
            uint flags = _flushed ? (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH : 0;
            _flushed = true;
            if (!Ready(context->GetData((ID3D11Asynchronous*)_disjoint, &disjoint, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), flags))
                || !Ready(context->GetData((ID3D11Asynchronous*)_start, &start, sizeof(ulong), flags))
                || !Ready(context->GetData((ID3D11Asynchronous*)_end, &end, sizeof(ulong), flags)))
            {
                return false;
            }
        }
        finally
        {
            _gpu.Device.UnlockContext();
        }

        milliseconds = disjoint.Disjoint || disjoint.Frequency == 0 ? null : (end - start) * 1000.0 / disjoint.Frequency;
        return true;
    }

    protected override void Release()
    {
        Com.Release(ref _disjoint);
        Com.Release(ref _start);
        Com.Release(ref _end);
    }

    internal static bool Ready(HRESULT hr)
    {
        Check(hr, "GetData");
        return hr == S.S_OK;
    }

    internal static ID3D11Query* CreateQuery(ID3D11Device* device, D3D11_QUERY kind)
    {
        var desc = new D3D11_QUERY_DESC { Query = kind };
        ID3D11Query* query;
        Check(device->CreateQuery(&desc, &query), "CreateQuery");
        return query;
    }
}

/// <summary>Una consulta de evento detrás de lo enviado.</summary>
internal sealed unsafe class D3D11Fence : GpuFence
{
    private readonly D3D11Gpu _gpu;
    private ID3D11Query* _query;
    private bool _complete;
    private bool _flushed;

    public D3D11Fence(D3D11Gpu gpu, ID3D11Query* query)
    {
        _gpu = gpu;
        _query = query;
        _complete = query == null;
    }

    ~D3D11Fence() => Com.Release(ref _query);

    public override bool IsComplete => Poll();

    public override bool Wait(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!Poll())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Yield();
        }

        return true;
    }

    private bool Poll()
    {
        if (_complete)
        {
            return true;
        }

        _gpu.Device.LockContext();
        try
        {
            if (_complete)
            {
                return true;
            }

            int done;
            uint flags = _flushed ? (uint)D3D11_ASYNC_GETDATA_FLAG.D3D11_ASYNC_GETDATA_DONOTFLUSH : 0;
            _flushed = true;
            if (!D3D11TimerQuery.Ready(_gpu.Context->GetData((ID3D11Asynchronous*)_query, &done, sizeof(int), flags)) || done == 0)
            {
                return false;
            }

            _complete = true;
            Com.Release(ref _query);
            return true;
        }
        finally
        {
            _gpu.Device.UnlockContext();
        }
    }
}

/// <summary>Textura o búfer de <c>STAGING</c> con lectura desde la CPU.</summary>
internal sealed unsafe class D3D11Readback : GpuReadback
{
    private readonly D3D11Gpu _gpu;
    private ID3D11Resource* _staging;

    private D3D11Readback(D3D11Gpu gpu, ID3D11Resource* staging, int width, int height, GpuFormat format, int bytes)
        : base(width, height, format, bytes)
    {
        _gpu = gpu;
        _staging = staging;
    }

    public ID3D11Resource* Native => _staging;

    public static D3D11Readback ForTexture(D3D11Gpu gpu, int width, int height, GpuFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format == GpuFormat.D32Float)
        {
            throw new NotSupportedException("D3D11 no lee la profundidad por regiones (GpuCaps.DepthReadback).");
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Gpu.Native(format),
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Check(gpu.NativeDevice->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(lectura)");
        return new D3D11Readback(gpu, (ID3D11Resource*)staging, width, height, format, width * GpuFormats.BytesPerPixel(format));
    }

    public static D3D11Readback ForBuffer(D3D11Gpu gpu, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)bytes,
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Buffer* staging;
        Check(gpu.NativeDevice->CreateBuffer(&desc, null, &staging), "CreateBuffer(lectura)");
        return new D3D11Readback(gpu, (ID3D11Resource*)staging, bytes, 1, GpuFormat.Unknown, bytes);
    }

    public override bool TryMap(out GpuMappedRows rows) => MapCore(wait: false, out rows);

    public override GpuMappedRows Map()
    {
        MapCore(wait: true, out GpuMappedRows rows);
        return rows;
    }

    protected internal override void Unmap()
    {
        _gpu.Context->Unmap(_staging, 0);
        _gpu.Device.UnlockContext();
    }

    protected override void Release() => Com.Release(ref _staging);

    // El candado del contexto queda tomado mientras las filas estén abiertas; lo suelta Unmap.
    private bool MapCore(bool wait, out GpuMappedRows rows)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _gpu.Device.LockContext();
        D3D11_MAPPED_SUBRESOURCE mapped;
        HRESULT hr = _gpu.Context->Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ, wait ? 0 : (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (!wait && hr == DXGI_ERROR_WAS_STILL_DRAWING)
        {
            _gpu.Device.UnlockContext();
            rows = default;
            return false;
        }

        if (hr.FAILED)
        {
            _gpu.Device.UnlockContext();
            Check(hr, "Map(lectura)");
        }

        rows = new GpuMappedRows(this, (byte*)mapped.pData, Height == 1 && Format == GpuFormat.Unknown ? Bytes : (int)mapped.RowPitch);
        return true;
    }
}
