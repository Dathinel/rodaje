using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Interpolation;

internal sealed unsafe class FlowInterpolator(GpuDevice gpu, RenderTargetPool budget, ShaderCache shaders) : IDisposable
{
    private readonly List<Resource> _resources = [];
    private readonly List<Level> _levels = [];
    private readonly Dictionary<string, nint> _shaders = [];
    private Resource? _patches;
    private Resource? _histogram;
    private Resource? _residual;
    private Resource? _decision;
    private ID3D11Buffer* _constants;
    private int _width;
    private int _height;
    private long _bytes;

    public int TextureCount => _resources.Count(r => r.IsTexture);
    public long Frames { get; private set; }
    internal nint DecisionBuffer => _decision?.Native ?? 0;

    public bool TryDispatch(PlaneViews a, PlaneViews b, ID3D11UnorderedAccessView* luma, ID3D11UnorderedAccessView* chroma,
        int width, int height, bool eightBit, float fraction)
    {
        if (width < 32 || height < 32 || (width & 1) != 0 || (height & 1) != 0) return false;
        try
        {
            if (!Prepare(width, height)) return false;
            var c = new Constants
            {
                FullWidth = (uint)width, FullHeight = (uint)height, Fraction = fraction, MaxCode = eightBit ? 255 : 65535,
                DeltaThreshold = FlowDefaults.DeltaThreshold, Regularization = FlowDefaults.DiagonalRegularization,
                Alpha = FlowDefaults.Alpha, Beta = FlowDefaults.Beta, CutHistogram = FlowDefaults.CutHistogram,
                CutResidual = FlowDefaults.CutResidual, Iterations = FlowDefaults.Iterations, Scale = 1 << FlowDefaults.FlowLevel,
            };
            var inputs = new nint[12];
            var outputs = new nint[7];
            for (int i = 0; i < _levels.Count; i++)
            {
                Level level = _levels[i];
                c.Width = (uint)level.Width; c.Height = (uint)level.Height; c.Initial = i == 0 ? 1u : 0u;
                for (int direction = 0; direction < 2; direction++)
                {
                    inputs[0] = (nint)(direction == 0 ? a.Luma : b.Luma);
                    inputs[2] = i == 0 ? 0 : _levels[i - 1].Images[direction].Srv;
                    outputs[0] = level.Images[direction].Uav;
                    Dispatch("Downsample", c, inputs, outputs, Groups(level.Width), Groups(level.Height));
                }
            }

            Array.Clear(inputs); Array.Clear(outputs);
            for (int direction = 0; direction < 2; direction++)
            {
                for (int i = _levels.Count - 1; i >= FlowDefaults.FlowLevel - 1; i--)
                {
                    Level level = _levels[i];
                    c.Width = (uint)level.Width; c.Height = (uint)level.Height;
                    c.PatchesX = (uint)PatchCount(level.Width); c.PatchesY = (uint)PatchCount(level.Height);
                    c.Initial = i == _levels.Count - 1 ? 1u : 0u;
                    c.PreviousWidth = c.Initial != 0 ? 1u : (uint)_levels[i + 1].Width;
                    c.PreviousHeight = c.Initial != 0 ? 1u : (uint)_levels[i + 1].Height;
                    inputs[2] = level.Images[direction].Srv; inputs[3] = level.Images[1 - direction].Srv;
                    inputs[4] = c.Initial != 0 ? 0 : _levels[i + 1].Flows[direction].Srv;
                    inputs[6] = 0; outputs[1] = _patches!.Uav;
                    Dispatch("Search", c, inputs, outputs, (c.PatchesX + 3) / 4, (c.PatchesY + 3) / 4);
                    inputs[4] = 0; inputs[6] = _patches.Srv; outputs[1] = level.Flows[direction].Uav;
                    Dispatch("Densify", c, inputs, outputs, Groups(level.Width), Groups(level.Height));
                }
            }

            Array.Clear(inputs); Array.Clear(outputs);
            uint* zero = stackalloc uint[4];
            new Span<uint>(zero, 4).Clear();
            ((ID3D11DeviceContext*)gpu.NativeContext)->ClearUnorderedAccessViewUint((ID3D11UnorderedAccessView*)_histogram!.Uav, zero);
            inputs[0] = (nint)a.Luma; inputs[1] = (nint)b.Luma; outputs[4] = _histogram.Uav;
            Dispatch("Histogram", c, inputs, outputs, (uint)((width * height + 255) / 256), 1);

            Level finest = _levels[FlowDefaults.FlowLevel - 1];
            c.Width = (uint)finest.Width; c.Height = (uint)finest.Height;
            c.PartialCount = (uint)((finest.Width * finest.Height + 255) / 256);
            Array.Clear(outputs);
            inputs[2] = finest.Images[0].Srv; inputs[3] = finest.Images[1].Srv; inputs[4] = finest.Flows[0].Srv;
            outputs[5] = _residual!.Uav;
            Dispatch("Residual", c, inputs, outputs, c.PartialCount, 1);
            Array.Clear(outputs);
            inputs[9] = _histogram.Srv; inputs[10] = _residual.Srv; outputs[6] = _decision!.Uav;
            Dispatch("Decide", c, inputs, outputs, 1, 1);

            Array.Clear(outputs);
            inputs[5] = finest.Flows[1].Srv; inputs[7] = (nint)a.Chroma; inputs[8] = (nint)b.Chroma;
            inputs[11] = _decision.Srv; outputs[2] = (nint)luma; outputs[3] = (nint)chroma;
            Dispatch("Warp", c, inputs, outputs, Groups(width / 2), Groups(height / 2));
            Frames++;
            return true;
        }
        catch (Exception error) when (error is RenderException or OutOfMemoryException or NotSupportedException)
        {
            Trim();
            return false;
        }
        finally
        {
            Unbind();
        }
    }

    public void Trim()
    {
        Unbind();
        foreach (Resource resource in _resources) resource.Dispose();
        _resources.Clear(); _levels.Clear();
        _patches = null; _histogram = null; _residual = null; _decision = null;
        Com.Release(ref _constants);
        budget.ReleaseReservation(_bytes);
        _bytes = 0; _width = 0; _height = 0;
    }

    public void Dispose()
    {
        Trim();
        foreach (nint shader in _shaders.Values) ((ID3D11ComputeShader*)shader)->Release();
        _shaders.Clear();
    }

    private bool Prepare(int width, int height)
    {
        if (_width == width && _height == height) return true;
        Trim();
        var dimensions = new List<(int Width, int Height)>();
        int w = width, h = height;
        while (true)
        {
            int nw = w / 2, nh = h / 2;
            if (dimensions.Count >= FlowDefaults.FlowLevel && Math.Min(nw, nh) < 16) break;
            dimensions.Add((nw, nh)); w = nw; h = nh;
        }

        long bytes = sizeof(Constants) + 128 * sizeof(uint) + 16;
        for (int i = 0; i < dimensions.Count; i++)
        {
            (w, h) = dimensions[i];
            bytes += (long)w * h * (i >= FlowDefaults.FlowLevel - 1 ? 24 : 8);
        }
        (int fw, int fh) = dimensions[FlowDefaults.FlowLevel - 1];
        bytes += (long)PatchCount(fw) * PatchCount(fh) * 8 + ((fw * fh + 255) / 256 * 4);
        if (!budget.Reserve(bytes)) return false;
        _bytes = bytes;
        foreach ((int lw, int lh) in dimensions)
        {
            var level = new Level(lw, lh);
            _levels.Add(level);
            for (int d = 0; d < 2; d++)
            {
                level.Images[d] = Texture(lw, lh, DXGI_FORMAT.DXGI_FORMAT_R32_FLOAT);
                if (_levels.Count >= FlowDefaults.FlowLevel) level.Flows[d] = Texture(lw, lh, DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT);
            }
        }
        _patches = Texture(PatchCount(fw), PatchCount(fh), DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT);
        _histogram = Buffer(128, 4); _residual = Buffer((fw * fh + 255) / 256, 4); _decision = Buffer(1, 16);
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)sizeof(Constants), Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };
        ID3D11Buffer* constants;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateBuffer(&desc, null, &constants), "CreateBuffer(flujo)");
        _constants = constants; _width = width; _height = height;
        return true;
    }

    private Resource Texture(int width, int height, DXGI_FORMAT format)
    {
        var resource = new Resource(true);
        _resources.Add(resource);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS),
        };
        var device = (ID3D11Device*)gpu.NativeDevice;
        ID3D11Texture2D* texture;
        Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(flujo)");
        resource.Native = (nint)texture;
        CreateViews(resource, null, null);
        return resource;
    }

    private Resource Buffer(int count, int stride)
    {
        var resource = new Resource(false);
        _resources.Add(resource);
        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = (uint)(count * stride), StructureByteStride = (uint)stride,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS),
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_BUFFER_STRUCTURED,
        };
        ID3D11Buffer* buffer;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateBuffer(&desc, null, &buffer), "CreateBuffer(reducción de flujo)");
        resource.Native = (nint)buffer;
        var srv = new D3D11_SHADER_RESOURCE_VIEW_DESC { ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_BUFFER };
        srv.Buffer.NumElements = (uint)count;
        var uav = new D3D11_UNORDERED_ACCESS_VIEW_DESC { ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_BUFFER };
        uav.Buffer.NumElements = (uint)count;
        CreateViews(resource, &srv, &uav);
        return resource;
    }

    private void CreateViews(Resource resource, D3D11_SHADER_RESOURCE_VIEW_DESC* srvDesc, D3D11_UNORDERED_ACCESS_VIEW_DESC* uavDesc)
    {
        var device = (ID3D11Device*)gpu.NativeDevice;
        ID3D11ShaderResourceView* srv;
        Check(device->CreateShaderResourceView((ID3D11Resource*)resource.Native, srvDesc, &srv), "CreateShaderResourceView(flujo)");
        resource.Srv = (nint)srv;
        ID3D11UnorderedAccessView* uav;
        Check(device->CreateUnorderedAccessView((ID3D11Resource*)resource.Native, uavDesc, &uav), "CreateUnorderedAccessView(flujo)");
        resource.Uav = (nint)uav;
    }

    private void Dispatch(string entry, Constants values, nint[] inputs, nint[] outputs, uint x, uint y)
    {
        Unbind();
        if (!_shaders.TryGetValue(entry, out nint native))
        {
            byte[] code = shaders.GetOrCompile(FlowShaders.Optical(entry));
            ID3D11ComputeShader* shader;
            fixed (byte* bytes = code)
                Check(((ID3D11Device*)gpu.NativeDevice)->CreateComputeShader(bytes, (nuint)code.Length, null, &shader), "CreateComputeShader(flujo)");
            native = (nint)shader; _shaders.Add(entry, native);
        }
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(flujo)");
        *(Constants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)_constants, 0);
        ID3D11Buffer* constants = _constants;
        context->CSSetConstantBuffers(0, 1, &constants);
        fixed (nint* srvs = inputs)
        fixed (nint* uavs = outputs)
        {
            context->CSSetShaderResources(0, 12, (ID3D11ShaderResourceView**)srvs);
            context->CSSetUnorderedAccessViews(0, 7, (ID3D11UnorderedAccessView**)uavs, null);
        }
        context->CSSetShader((ID3D11ComputeShader*)native, null, 0);
        context->Dispatch(x, y, 1);
    }

    private void Unbind()
    {
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        nint* empty = stackalloc nint[12];
        new Span<nint>(empty, 12).Clear();
        context->CSSetShaderResources(0, 12, (ID3D11ShaderResourceView**)empty);
        context->CSSetUnorderedAccessViews(0, 7, (ID3D11UnorderedAccessView**)empty, null);
        context->CSSetConstantBuffers(0, 1, (ID3D11Buffer**)empty);
        context->CSSetShader(null, null, 0);
    }

    private static int PatchCount(int length) => ((length - FlowDefaults.PatchSize + FlowDefaults.PatchStride - 1) / FlowDefaults.PatchStride) + 1;
    private static uint Groups(int length) => (uint)((length + 7) / 8);

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public uint Width, Height, FullWidth, FullHeight;
        public uint PreviousWidth, PreviousHeight, PatchesX, PatchesY;
        public float Fraction, MaxCode;
        public uint Initial, PartialCount;
        public float DeltaThreshold, Regularization, Alpha, Beta;
        public float CutHistogram, CutResidual;
        public uint Iterations, Scale;
    }

    private sealed class Level(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public Resource[] Images { get; } = new Resource[2];
        public Resource[] Flows { get; } = new Resource[2];
    }

    private sealed class Resource(bool isTexture) : IDisposable
    {
        public bool IsTexture { get; } = isTexture;
        public nint Native, Srv, Uav;
        public void Dispose()
        {
            if (Uav != 0) ((ID3D11UnorderedAccessView*)Uav)->Release();
            if (Srv != 0) ((ID3D11ShaderResourceView*)Srv)->Release();
            if (Native != 0) ((ID3D11Resource*)Native)->Release();
            Uav = 0; Srv = 0; Native = 0;
        }
    }
}
