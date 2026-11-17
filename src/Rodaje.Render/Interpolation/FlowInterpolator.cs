using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Interpolation;

internal sealed unsafe class FlowInterpolator(GpuDevice gpu, RenderTargetPool budget, ShaderCache shaders) : IDisposable
{
    private readonly D3D11Gpu _engine = D3D11Gpu.For(gpu);
    private readonly List<IDisposable> _resources = [];
    private readonly List<Level> _levels = [];
    private readonly Dictionary<string, GpuPipeline> _pipelines = [];
    private readonly Dictionary<string, GpuShader> _shaders = [];
    private GpuTexture? _patches;
    private GpuBuffer? _histogram;
    private GpuBuffer? _residual;
    private GpuBuffer? _decision;
    private GpuBuffer? _constants;
    private int _width;
    private int _height;
    private long _bytes;

    public int TextureCount => _resources.Count(r => r is GpuTexture);
    public long Frames { get; private set; }
    internal nint DecisionBuffer => _decision is D3D11Buffer d ? (nint)d.Native : 0;

    public bool TryDispatch(PlaneViews a, PlaneViews b, GpuTexture luma, GpuTexture chroma,
        int width, int height, bool eightBit, float fraction)
    {
        if (width < 32 || height < 32 || (width & 1) != 0 || (height & 1) != 0) return false;
        try
        {
            if (!Prepare(width, height)) return false;
            using IGpuFrame frame = _engine.BeginFrame();
            var c = new Constants
            {
                FullWidth = (uint)width, FullHeight = (uint)height, Fraction = fraction, MaxCode = eightBit ? 255 : 65535,
                DeltaThreshold = FlowDefaults.DeltaThreshold, Regularization = FlowDefaults.DiagonalRegularization,
                Alpha = FlowDefaults.Alpha, Beta = FlowDefaults.Beta, CutHistogram = FlowDefaults.CutHistogram,
                CutResidual = FlowDefaults.CutResidual, Iterations = FlowDefaults.Iterations, Scale = 1 << FlowDefaults.FlowLevel,
            };

            for (int i = 0; i < _levels.Count; i++)
            {
                Level level = _levels[i];
                c.Width = (uint)level.Width; c.Height = (uint)level.Height; c.Initial = i == 0 ? 1u : 0u;
                for (int direction = 0; direction < 2; direction++)
                {
                    var bindings = default(GpuBindings);
                    bindings.SetConstants(0, _constants);
                    bindings.SetResource(0, direction == 0 ? a.LumaTexture : b.LumaTexture);
                    bindings.SetResource(2, i == 0 ? null : _levels[i - 1].Images[direction]);
                    bindings.SetStorage(0, level.Images[direction]);
                    frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref c, 1));
                    frame.Dispatch(GetPipeline("Downsample"), bindings, Groups(level.Width), Groups(level.Height));
                }
            }

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

                    var bSearch = default(GpuBindings);
                    bSearch.SetConstants(0, _constants);
                    bSearch.SetResource(2, level.Images[direction]);
                    bSearch.SetResource(3, level.Images[1 - direction]);
                    bSearch.SetResource(4, c.Initial != 0 ? null : _levels[i + 1].Flows[direction]);
                    bSearch.SetStorage(1, _patches);
                    frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref c, 1));
                    frame.Dispatch(GetPipeline("Search"), bSearch, (int)((c.PatchesX + 3) / 4), (int)((c.PatchesY + 3) / 4));

                    var bDensify = default(GpuBindings);
                    bDensify.SetConstants(0, _constants);
                    bDensify.SetResource(2, level.Images[direction]);
                    bDensify.SetResource(3, level.Images[1 - direction]);
                    bDensify.SetResource(6, _patches);
                    bDensify.SetStorage(1, level.Flows[direction]);
                    frame.Dispatch(GetPipeline("Densify"), bDensify, Groups(level.Width), Groups(level.Height));
                }
            }

            frame.ClearUint(_histogram!, 0);

            var bHist = default(GpuBindings);
            bHist.SetConstants(0, _constants);
            bHist.SetResource(0, a.LumaTexture);
            bHist.SetResource(1, b.LumaTexture);
            bHist.SetStorage(4, _histogram);
            frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref c, 1));
            frame.Dispatch(GetPipeline("Histogram"), bHist, (width * height + 255) / 256, 1);

            Level finest = _levels[FlowDefaults.FlowLevel - 1];
            c.Width = (uint)finest.Width; c.Height = (uint)finest.Height;
            c.PartialCount = (uint)((finest.Width * finest.Height + 255) / 256);

            var bRes = default(GpuBindings);
            bRes.SetConstants(0, _constants);
            bRes.SetResource(2, finest.Images[0]);
            bRes.SetResource(3, finest.Images[1]);
            bRes.SetResource(4, finest.Flows[0]);
            bRes.SetStorage(5, _residual);
            frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref c, 1));
            frame.Dispatch(GetPipeline("Residual"), bRes, (int)c.PartialCount, 1);

            var bDecide = default(GpuBindings);
            bDecide.SetConstants(0, _constants);
            bDecide.SetResource(9, _histogram);
            bDecide.SetResource(10, _residual);
            bDecide.SetStorage(6, _decision);
            frame.Dispatch(GetPipeline("Decide"), bDecide, 1, 1);

            var bWarp = default(GpuBindings);
            bWarp.SetConstants(0, _constants);
            bWarp.SetResource(0, a.LumaTexture);
            bWarp.SetResource(1, b.LumaTexture);
            bWarp.SetResource(2, finest.Images[0]);
            bWarp.SetResource(3, finest.Images[1]);
            bWarp.SetResource(4, finest.Flows[0]);
            bWarp.SetResource(5, finest.Flows[1]);
            bWarp.SetResource(7, a.ChromaTexture);
            bWarp.SetResource(8, b.ChromaTexture);
            bWarp.SetResource(11, _decision);
            bWarp.SetStorage(2, luma);
            bWarp.SetStorage(3, chroma);
            frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref c, 1));
            frame.Dispatch(GetPipeline("Warp"), bWarp, Groups(width / 2), Groups(height / 2));

            Frames++;
            return true;
        }
        catch (Exception error) when (error is RenderException or OutOfMemoryException or NotSupportedException)
        {
            Trim();
            return false;
        }
    }

    public void Trim()
    {
        foreach (IDisposable resource in _resources) resource.Dispose();
        _resources.Clear(); _levels.Clear();
        _patches = null; _histogram = null; _residual = null; _decision = null;
        _constants = null;
        budget.ReleaseReservation(_bytes);
        _bytes = 0; _width = 0; _height = 0;
    }

    public void Dispose()
    {
        Trim();
        foreach (GpuPipeline pipeline in _pipelines.Values) pipeline.Dispose();
        _pipelines.Clear();
        foreach (GpuShader shader in _shaders.Values) shader.Dispose();
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
                level.Images[d] = CreateTexture(lw, lh, GpuFormat.R32Float);
                if (_levels.Count >= FlowDefaults.FlowLevel) level.Flows[d] = CreateTexture(lw, lh, GpuFormat.Rg32Float);
            }
        }
        _patches = CreateTexture(PatchCount(fw), PatchCount(fh), GpuFormat.Rg32Float);
        _histogram = CreateBuffer(128, 4);
        _residual = CreateBuffer((fw * fh + 255) / 256, 4);
        _decision = CreateBuffer(1, 16);
        _constants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(Constants), GpuBufferKind.Constant));
        _resources.Add(_constants);
        _width = width; _height = height;
        return true;
    }

    private GpuTexture CreateTexture(int width, int height, GpuFormat format)
    {
        GpuTexture texture = _engine.CreateTexture(new GpuTextureDesc(width, height, format, GpuTextureUsage.Sampled | GpuTextureUsage.Storage));
        _resources.Add(texture);
        return texture;
    }

    private GpuBuffer CreateBuffer(int count, int stride)
    {
        GpuBuffer buffer = _engine.CreateBuffer(new GpuBufferDesc(count * stride, GpuBufferKind.Structured, Storage: true, Stride: stride));
        _resources.Add(buffer);
        return buffer;
    }

    private GpuPipeline GetPipeline(string entry)
    {
        if (!_pipelines.TryGetValue(entry, out GpuPipeline? pipeline))
        {
            GpuShader shader = _engine.CreateShader(FlowShaders.Optical(entry), shaders);
            _shaders.Add(entry, shader);
            pipeline = _engine.CreatePipeline(new GpuPipelineDesc { Compute = shader });
            _pipelines.Add(entry, pipeline);
        }

        return pipeline;
    }

    private static int PatchCount(int length) => ((length - FlowDefaults.PatchSize + FlowDefaults.PatchStride - 1) / FlowDefaults.PatchStride) + 1;
    private static int Groups(int length) => (length + 7) / 8;

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
        public GpuTexture[] Images { get; } = new GpuTexture[2];
        public GpuTexture?[] Flows { get; } = new GpuTexture?[2];
    }
}
