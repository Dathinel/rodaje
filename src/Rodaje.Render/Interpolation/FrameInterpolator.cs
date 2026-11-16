using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Interpolation;

/// <summary>Mezcla los códigos de los planos del origen; las salidas se devuelven al terminar el cuadro.</summary>
internal sealed unsafe class FrameInterpolator(GpuDevice gpu, PlaneViewCache views, RenderTargetPool budget, ShaderCache shaders) : IDisposable
{
    private readonly List<Slot> _slots = [];
    private readonly List<D3D11TexturePayload> _payloads = [];
    private ID3D11ComputeShader* _luma;
    private ID3D11ComputeShader* _chroma;
    private ID3D11Buffer* _constants;
    private long _frame;
    private FlowInterpolator? _flow;

    public int TextureCount => _slots.Count + (_flow?.TextureCount ?? 0);
    internal long FlowFrames => _flow?.Frames ?? 0;
    internal nint FlowDecisionBuffer => _flow?.DecisionBuffer ?? 0;

    public D3D11TexturePayload? Blend(D3D11TexturePayload a, D3D11TexturePayload b, float fraction) => Interpolate(a, b, fraction, false);

    public D3D11TexturePayload? Flow(D3D11TexturePayload a, D3D11TexturePayload b, float fraction)
    {
        if (a.Device != gpu || b.Device != gpu) return null;
        try
        {
            return Interpolate(a, b, fraction, true);
        }
        catch (Exception error) when (error is RenderException or OutOfMemoryException or NotSupportedException)
        {
            _flow?.Trim();
            try { return Blend(a, b, fraction); }
            catch (Exception fallback) when (fallback is RenderException or OutOfMemoryException or NotSupportedException) { return null; }
        }
    }

    private D3D11TexturePayload? Interpolate(D3D11TexturePayload a, D3D11TexturePayload b, float fraction, bool opticalFlow)
    {
        if (a.IsReleased || b.IsReleased || a.DxgiFormat != b.DxgiFormat || a.PixelFormat != b.PixelFormat
            || a.Width != b.Width || a.Height != b.Height
            || a.DxgiFormat is not ((int)DXGI_FORMAT.DXGI_FORMAT_NV12 or (int)DXGI_FORMAT.DXGI_FORMAT_P010 or (int)DXGI_FORMAT.DXGI_FORMAT_P016)
            || !float.IsFinite(fraction))
        {
            return null;
        }

        if (a.Device != gpu || b.Device != gpu)
        {
            throw new ArgumentException("Los cuadros de interpolación son de otro dispositivo.");
        }

        int width = a.CodedWidth;
        int height = a.CodedHeight;
        DXGI_FORMAT format = (DXGI_FORMAT)a.DxgiFormat;
        Slot? slot = _slots.Find(s => !s.InUse && s.Width == width && s.Height == height && s.Format == format);
        if (slot is null)
        {
            long bytes = (long)width * height * 3 / 2 * (format == DXGI_FORMAT.DXGI_FORMAT_NV12 ? 1 : 2);
            while (!budget.Reserve(bytes))
            {
                if (opticalFlow && _flow is { TextureCount: > 0 })
                {
                    _flow.Trim();
                    continue;
                }

                int free = _slots.FindIndex(s => !s.InUse);
                if (free < 0)
                {
                    return null;
                }

                Remove(free);
            }

            try
            {
                slot = CreateSlot(width, height, format, bytes);
                _slots.Add(slot);
            }
            catch (RenderException)
            {
                // Un controlador sin vistas de escritura sobre planos de video: la capa sale con el cuadro A, no se cae el cuadro.
                budget.ReleaseReservation(bytes);
                return null;
            }
            catch
            {
                budget.ReleaseReservation(bytes);
                throw;
            }
        }

        EnsureShaders();
        slot.InUse = true;
        PlaneViews first = views.Get(a, out bool ownFirst);
        try
        {
            PlaneViews second = views.Get(b, out bool ownSecond);
            try
            {
                bool flowed = opticalFlow && (_flow ??= new FlowInterpolator(gpu, budget, shaders))
                    .TryDispatch(first, second, slot.Luma, slot.Chroma, a.Width, a.Height,
                        format == DXGI_FORMAT.DXGI_FORMAT_NV12, Math.Clamp(fraction, 0, 1));
                if (!flowed)
                {
                    Dispatch(_luma, first.Luma, second.Luma, slot.Luma, width, height, fraction);
                    Dispatch(_chroma, first.Chroma, second.Chroma, slot.Chroma, width / 2, height / 2, fraction);
                }
            }
            finally
            {
                Unbind();
                if (ownSecond) second.Release();
            }
        }
        finally
        {
            if (ownFirst) first.Release();
        }

        D3D11TexturePayload payload = D3D11TexturePayload.FromTexture(gpu, (nint)slot.Texture, 0, a.Width, a.Height, a.Color, a.PixelFormat);
        _payloads.Add(payload);
        return payload;
    }

    public void EndFrame()
    {
        foreach (D3D11TexturePayload payload in _payloads) payload.Dispose();
        _payloads.Clear();
        foreach (Slot slot in _slots)
        {
            if (slot.InUse) slot.LastUsed = _frame;
            slot.InUse = false;
        }

        _frame++;
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (_frame - _slots[i].LastUsed > 240) Remove(i);
        }
    }

    public void Trim()
    {
        _flow?.Trim();
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (!_slots[i].InUse) Remove(i);
        }
    }

    public void Dispose()
    {
        EndFrame();
        Trim();
        _flow?.Dispose();
        Com.Release(ref _luma);
        Com.Release(ref _chroma);
        Com.Release(ref _constants);
    }

    private void Remove(int index)
    {
        Slot slot = _slots[index];
        _slots.RemoveAt(index);
        budget.ReleaseReservation(slot.Bytes);
        slot.Dispose();
    }

    private Slot CreateSlot(int width, int height, DXGI_FORMAT format, long bytes)
    {
        var slot = new Slot(width, height, format, bytes);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS),
        };
        var device = (ID3D11Device*)gpu.NativeDevice;
        try
        {
            ID3D11Texture2D* texture;
            Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(interpolación)");
            slot.Texture = texture;
            bool eight = format == DXGI_FORMAT.DXGI_FORMAT_NV12;
            slot.Luma = CreateView(texture, eight ? DXGI_FORMAT.DXGI_FORMAT_R8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R16_UNORM);
            slot.Chroma = CreateView(texture, eight ? DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM : DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM);
            return slot;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    private ID3D11UnorderedAccessView* CreateView(ID3D11Texture2D* texture, DXGI_FORMAT format)
    {
        var desc = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = format, ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D };
        ID3D11UnorderedAccessView* view;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateUnorderedAccessView((ID3D11Resource*)texture, &desc, &view), "CreateUnorderedAccessView(interpolación)");
        return view;
    }

    private void EnsureShaders()
    {
        if (_luma == null) _luma = CreateShader(FlowShaders.Luma);
        if (_chroma == null) _chroma = CreateShader(FlowShaders.Chroma);
        if (_constants != null) return;
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(Constants), Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* buffer;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateBuffer(&desc, null, &buffer), "CreateBuffer(interpolación)");
        _constants = buffer;
    }

    private ID3D11ComputeShader* CreateShader(ShaderVariant variant)
    {
        byte[] code = shaders.GetOrCompile(variant);
        ID3D11ComputeShader* shader;
        fixed (byte* bytes = code)
        {
            Check(((ID3D11Device*)gpu.NativeDevice)->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), "CreateComputeShader(interpolación)");
        }

        return shader;
    }

    private void Dispatch(ID3D11ComputeShader* shader, ID3D11ShaderResourceView* a, ID3D11ShaderResourceView* b,
        ID3D11UnorderedAccessView* output, int width, int height, float fraction)
    {
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        Unbind();
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(interpolación)");
        *(Constants*)mapped.pData = new Constants { Width = (uint)width, Height = (uint)height, Fraction = Math.Clamp(fraction, 0, 1) };
        context->Unmap((ID3D11Resource*)_constants, 0);
        ID3D11Buffer* constants = _constants;
        ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2] { a, b };
        context->CSSetShader(shader, null, 0);
        context->CSSetConstantBuffers(0, 1, &constants);
        context->CSSetShaderResources(0, 2, inputs);
        context->CSSetUnorderedAccessViews(0, 1, &output, null);
        context->Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
    }

    private void Unbind()
    {
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        ID3D11ShaderResourceView** inputs = stackalloc ID3D11ShaderResourceView*[2] { null, null };
        ID3D11UnorderedAccessView* output = null;
        context->CSSetShaderResources(0, 2, inputs);
        context->CSSetUnorderedAccessViews(0, 1, &output, null);
        context->CSSetShader(null, null, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public uint Width;
        public uint Height;
        public float Fraction;
        public uint Padding;
    }

    private sealed class Slot(int width, int height, DXGI_FORMAT format, long bytes) : IDisposable
    {
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly DXGI_FORMAT Format = format;
        public readonly long Bytes = bytes;
        public ID3D11Texture2D* Texture;
        public ID3D11UnorderedAccessView* Luma;
        public ID3D11UnorderedAccessView* Chroma;
        public bool InUse;
        public long LastUsed;

        public void Dispose()
        {
            Com.Release(ref Chroma);
            Com.Release(ref Luma);
            Com.Release(ref Texture);
        }
    }
}
