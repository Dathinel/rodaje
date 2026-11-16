using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Gpu.D3D11;

/// <summary>
/// Un cuadro de D3D11: el candado del contexto inmediato, tomado desde <see cref="D3D11Gpu.BeginFrame"/> hasta <see cref="Dispose"/>.
/// Cada orden va directa al contexto; no hay nada que «enviar» aparte, así que <see cref="Submit"/> solo deja una valla.
/// </summary>
internal sealed unsafe class D3D11Frame(D3D11Gpu gpu) : IGpuFrame
{
    private readonly ID3D11DeviceContext* _context = gpu.Context;
    private bool _closed;

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        gpu.Device.UnlockContext();
    }

    public void Write<T>(GpuBuffer buffer, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        var native = (D3D11Buffer)buffer;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        if (bytes.Length > native.Size)
        {
            throw new ArgumentException("Los datos no caben en el búfer.", nameof(data));
        }

        if (native.IsDynamic)
        {
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(_context->Map((ID3D11Resource*)native.Native, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(constantes)");
            bytes.CopyTo(new Span<byte>(mapped.pData, native.Size));
            _context->Unmap((ID3D11Resource*)native.Native, 0);
            return;
        }

        var box = new D3D11_BOX { left = 0, right = (uint)bytes.Length, top = 0, bottom = 1, front = 0, back = 1 };
        fixed (byte* source = bytes)
        {
            _context->UpdateSubresource((ID3D11Resource*)native.Native, 0, &box, source, 0, 0);
        }
    }

    public void Write(GpuTexture texture, ReadOnlySpan<byte> pixels, int rowPitch, int depthPitch = 0, int mip = 0)
    {
        var native = (D3D11Texture)texture;
        uint mips = (uint)(native.Desc.MipLevels == 0 ? 1 + (int)Math.Log2(Math.Max(native.Width, native.Height)) : native.Desc.MipLevels);
        fixed (byte* source = pixels)
        {
            _context->UpdateSubresource(native.Resource, D3D11CalcSubresource((uint)mip, 0, mips), null, source, (uint)rowPitch, (uint)depthPitch);
        }
    }

    public void Copy(GpuTexture source, GpuTexture destination) =>
        _context->CopyResource(((D3D11Texture)destination).Resource, ((D3D11Texture)source).Resource);

    public void Copy(GpuTexture source, in GpuBox region, GpuTexture destination, int x, int y)
    {
        D3D11_BOX box = Box(region);
        _context->CopySubresourceRegion(((D3D11Texture)destination).Resource, 0, (uint)x, (uint)y, 0, ((D3D11Texture)source).Resource, 0, &box);
    }

    public void GenerateMips(GpuTexture texture) => _context->GenerateMips(((D3D11Texture)texture).Srv);

    public void Clear(GpuTexture target, in GpuColor color)
    {
        var native = (D3D11Texture)target;
        float* rgba = stackalloc float[] { color.R, color.G, color.B, color.A };
        if (native.Rtv != null)
        {
            _context->ClearRenderTargetView(native.Rtv, rgba);
        }
        else
        {
            _context->ClearUnorderedAccessViewFloat(native.Uav, rgba);
        }
    }

    public void ClearUint(GpuResource target, uint value)
    {
        uint* values = stackalloc uint[] { value, value, value, value };
        switch (target)
        {
            case D3D11Texture { Uav: not null } texture:
                _context->ClearUnorderedAccessViewUint(texture.Uav, values);
                break;
            case D3D11Texture texture:
                float* asFloat = stackalloc float[] { value, value, value, value };
                _context->ClearRenderTargetView(texture.Rtv, asFloat);
                break;
            case D3D11Buffer { Uav: not null } buffer:
                _context->ClearUnorderedAccessViewUint(buffer.Uav, values);
                break;
            default:
                throw new ArgumentException("Solo se pone a un valor un destino de color o algo con escritura desde un shader.", nameof(target));
        }
    }

    public void ClearDepth(GpuTexture depth, float value = 1) =>
        _context->ClearDepthStencilView(((D3D11Texture)depth).Dsv, (uint)D3D11_CLEAR_FLAG.D3D11_CLEAR_DEPTH, value, 0);

    public void Draw(GpuPipeline pipeline, in GpuTargets targets, in GpuBindings bindings, in GpuViewport viewport, in GpuDrawCall draw)
    {
        var native = (D3D11Pipeline)pipeline;
        if (native.IsCompute)
        {
            throw new ArgumentException("Una canalización de cómputo se usa con Dispatch.", nameof(pipeline));
        }

        // Primero los destinos: un recurso que pase de destino a entrada se desenlaza como salida antes de leerse.
        UnbindComputeStorage();
        var rtvs = stackalloc ID3D11RenderTargetView*[GpuTargets.MaxColors];
        for (int i = 0; i < targets.Count; i++)
        {
            rtvs[i] = ((D3D11Texture)targets[i]!).Rtv;
        }

        ID3D11DepthStencilView* dsv = targets.Depth is D3D11Texture depth ? depth.Dsv : null;
        _context->OMSetRenderTargets((uint)targets.Count, rtvs, dsv);
        gpu.BoundTargets = targets.Count;
        var vp = new D3D11_VIEWPORT
        {
            TopLeftX = viewport.X,
            TopLeftY = viewport.Y,
            Width = viewport.Width,
            Height = viewport.Height,
            MinDepth = viewport.MinDepth,
            MaxDepth = viewport.MaxDepth,
        };
        _context->RSSetViewports(1, &vp);
        _context->RSSetState(native.Rasterizer);
        _context->IASetPrimitiveTopology(native.Topology);
        _context->IASetInputLayout(native.Layout);
        if (draw.VertexBuffer is D3D11Buffer vertices)
        {
            ID3D11Buffer* buffer = vertices.Native;
            uint stride = (uint)native.Desc.VertexLayout!.Stride;
            uint offset = 0;
            _context->IASetVertexBuffers(0, 1, &buffer, &stride, &offset);
        }

        if (draw.IndexBuffer is D3D11Buffer indices)
        {
            _context->IASetIndexBuffer(indices.Native,
                draw.IndexFormat == GpuIndexFormat.Bits32 ? DXGI_FORMAT.DXGI_FORMAT_R32_UINT : DXGI_FORMAT.DXGI_FORMAT_R16_UINT, 0);
        }

        _context->VSSetShader((ID3D11VertexShader*)native.VertexShader!.Native, null, 0);
        _context->PSSetShader((ID3D11PixelShader*)native.PixelShader!.Native, null, 0);
        SetConstants(bindings, compute: false);
        SetResources(bindings, compute: false);
        SetSamplers(bindings, compute: false);
        _context->OMSetBlendState(native.Blend, null, uint.MaxValue);
        _context->OMSetDepthStencilState(native.DepthState, 0);
        if (draw.IndexBuffer is not null)
        {
            if (draw.Instances == 1)
                _context->DrawIndexed((uint)draw.Count, (uint)draw.FirstIndex, draw.BaseVertex);
            else
                _context->DrawIndexedInstanced((uint)draw.Count, (uint)draw.Instances, (uint)draw.FirstIndex, draw.BaseVertex, 0);
        }
        else if (draw.Instances == 1)
        {
            _context->Draw((uint)draw.Count, (uint)draw.BaseVertex);
        }
        else
        {
            _context->DrawInstanced((uint)draw.Count, (uint)draw.Instances, (uint)draw.BaseVertex, 0);
        }
    }

    public void Dispatch(GpuPipeline pipeline, in GpuBindings bindings, int groupsX, int groupsY, int groupsZ = 1)
    {
        var native = (D3D11Pipeline)pipeline;
        if (!native.IsCompute)
        {
            throw new ArgumentException("Dispatch necesita una canalización de cómputo.", nameof(pipeline));
        }

        // Un destino de dibujo enlazado anularía su lectura aquí; las salidas van antes que las entradas.
        if (gpu.BoundTargets > 0)
        {
            _context->OMSetRenderTargets(0, null, null);
            gpu.BoundTargets = 0;
        }

        var uavs = stackalloc ID3D11UnorderedAccessView*[GpuBindings.MaxStorage];
        int count = Math.Max(bindings.StorageCount, gpu.BoundStorage);
        for (int i = 0; i < count; i++)
        {
            uavs[i] = bindings.StorageResource(i) switch
            {
                D3D11Texture texture => texture.Uav,
                D3D11Buffer buffer => buffer.Uav,
                _ => null,
            };
        }

        _context->CSSetUnorderedAccessViews(0, (uint)count, uavs, null);
        gpu.BoundStorage = bindings.StorageCount;
        _context->CSSetShader((ID3D11ComputeShader*)native.ComputeShader!.Native, null, 0);
        SetConstants(bindings, compute: true);
        SetResources(bindings, compute: true);
        SetSamplers(bindings, compute: true);
        _context->Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
    }

    public void Resolve(GpuTexture multisampled, GpuTexture destination) =>
        _context->ResolveSubresource(((D3D11Texture)destination).Resource, 0, ((D3D11Texture)multisampled).Resource, 0,
            D3D11Gpu.Native(destination.Format));

    public void Copy(GpuTexture source, in GpuBox region, GpuReadback destination)
    {
        if (region.Width > destination.Width || region.Height > destination.Height)
        {
            throw new ArgumentException("La región no cabe en el destino de lectura.", nameof(region));
        }

        D3D11_BOX box = Box(region);
        _context->CopySubresourceRegion(((D3D11Readback)destination).Native, 0, 0, 0, 0, ((D3D11Texture)source).Resource, 0, &box);
    }

    public void Copy(GpuBuffer source, int offset, int bytes, GpuReadback destination)
    {
        if (bytes > destination.Bytes)
        {
            throw new ArgumentException("Los bytes no caben en el destino de lectura.", nameof(bytes));
        }

        var box = new D3D11_BOX { left = (uint)offset, right = (uint)(offset + bytes), top = 0, bottom = 1, front = 0, back = 1 };
        _context->CopySubresourceRegion(((D3D11Readback)destination).Native, 0, 0, 0, 0, (ID3D11Resource*)((D3D11Buffer)source).Native, 0, &box);
    }

    public void BeginTimer(GpuTimerQuery timer) => ((D3D11TimerQuery)timer).Begin(_context);

    public void EndTimer(GpuTimerQuery timer) => ((D3D11TimerQuery)timer).End(_context);

    public GpuFence Submit()
    {
        ID3D11Query* query = D3D11TimerQuery.CreateQuery(gpu.NativeDevice, D3D11_QUERY.D3D11_QUERY_EVENT);
        _context->End((ID3D11Asynchronous*)query);
        return new D3D11Fence(gpu, query);
    }

    private static D3D11_BOX Box(in GpuBox region) => new()
    {
        left = (uint)region.X,
        top = (uint)region.Y,
        right = (uint)(region.X + region.Width),
        bottom = (uint)(region.Y + region.Height),
        front = 0,
        back = 1,
    };

    private void UnbindComputeStorage()
    {
        if (gpu.BoundStorage == 0)
        {
            return;
        }

        var none = stackalloc ID3D11UnorderedAccessView*[GpuBindings.MaxStorage];
        _context->CSSetUnorderedAccessViews(0, (uint)gpu.BoundStorage, none, null);
        gpu.BoundStorage = 0;
    }

    private void SetConstants(in GpuBindings bindings, bool compute)
    {
        int count = bindings.ConstantCount;
        if (count == 0)
        {
            return;
        }

        var buffers = stackalloc ID3D11Buffer*[GpuBindings.MaxConstants];
        for (int i = 0; i < count; i++)
        {
            buffers[i] = bindings.ConstantBuffer(i) is D3D11Buffer buffer ? buffer.Native : null;
        }

        if (compute)
        {
            _context->CSSetConstantBuffers(0, (uint)count, buffers);
            return;
        }

        _context->VSSetConstantBuffers(0, (uint)count, buffers);
        _context->PSSetConstantBuffers(0, (uint)count, buffers);
    }

    private void SetResources(in GpuBindings bindings, bool compute)
    {
        ref int bound = ref compute ? ref gpu.BoundComputeResources : ref gpu.BoundPixelResources;
        int count = Math.Max(bindings.ResourceCount, bound);
        if (count == 0)
        {
            return;
        }

        var views = stackalloc ID3D11ShaderResourceView*[GpuBindings.MaxResources];
        for (int i = 0; i < count; i++)
        {
            views[i] = i >= bindings.ResourceCount ? null : bindings.Resource(i) switch
            {
                D3D11Texture texture => texture.Srv,
                D3D11Buffer buffer => buffer.Srv,
                _ => null,
            };
        }

        if (compute)
        {
            _context->CSSetShaderResources(0, (uint)count, views);
        }
        else
        {
            _context->PSSetShaderResources(0, (uint)count, views);
        }

        bound = bindings.ResourceCount;
    }

    private void SetSamplers(in GpuBindings bindings, bool compute)
    {
        int count = bindings.SamplerCount;
        if (count == 0)
        {
            return;
        }

        var samplers = stackalloc ID3D11SamplerState*[GpuBindings.MaxSamplers];
        for (int i = 0; i < count; i++)
        {
            samplers[i] = bindings.Sampler(i) is D3D11Sampler sampler ? sampler.Native : null;
        }

        if (compute)
        {
            _context->CSSetSamplers(0, (uint)count, samplers);
        }
        else
        {
            _context->PSSetSamplers(0, (uint)count, samplers);
        }
    }
}
