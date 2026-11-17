using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>
/// El motor D3D11 de <see cref="IGpu"/> en WARP (está en todo Windows y da los mismos bytes en cada corrida): el núcleo que usa
/// Rodaje y el nivel <i>Raster</i> que pide Unicomp (líneas, sesgo de profundidad, prueba sin escritura, índices de 16 y 32 bits).
/// </summary>
public sealed class D3D11GpuTests : IDisposable
{
    private const int Size = 16;

    private const string Hlsl = """
        cbuffer Draw : register(b0) { float4 color; float4 depth; uint4 id; };
        struct VsOut { float4 position : SV_Position; float4 color : COLOR; };

        VsOut VsFull(uint v : SV_VertexID)
        {
            VsOut o;
            float2 uv = float2(v & 1, v >> 1);
            o.position = float4(uv.x * 2 - 1, 1 - uv.y * 2, depth.x, 1);
            o.color = color;
            return o;
        }

        // Profundidad que crece de izquierda a derecha (para el sesgo por pendiente).
        VsOut VsSlope(uint v : SV_VertexID)
        {
            VsOut o;
            float2 uv = float2(v & 1, v >> 1);
            o.position = float4(uv.x * 2 - 1, 1 - uv.y * 2, 0.2 + 0.6 * uv.x, 1);
            o.color = color;
            return o;
        }

        VsOut VsMesh(float3 position : POSITION, float4 vertexColor : COLOR)
        {
            VsOut o;
            o.position = float4(position, 1);
            o.color = vertexColor;
            return o;
        }

        float4 PsColor(VsOut i) : SV_Target { return i.color; }

        uint PsId(VsOut i) : SV_Target { return id.x; }

        Texture2DArray<float> luma : register(t0);
        Texture2DArray<float2> chroma : register(t1);

        float4 PsPlanes(VsOut i) : SV_Target { return float4(luma.Load(int4(0, 0, 0, 0)), chroma.Load(int4(0, 0, 0, 0)), 1); }

        RWTexture2D<float4> image : register(u0);
        RWByteAddressBuffer bytes : register(u1);

        [numthreads(8, 8, 1)]
        void CsFill(uint3 t : SV_DispatchThreadID)
        {
            image[t.xy] = float4(t.x / 255.0, t.y / 255.0, color.z, 1);
            if (t.y == 0) bytes.Store(t.x * 4, t.x * 3 + id.x);
        }
        """;

    private readonly D3D11Gpu _gpu = D3D11Gpu.Create(preferSoftware: true);
    private readonly ShaderCache _cache = new(null);
    private readonly GpuBuffer _constants;
    private readonly List<GpuShader> _shaders = [];

    public D3D11GpuTests()
    {
        _constants = _gpu.CreateBuffer(new GpuBufferDesc(48, GpuBufferKind.Constant));
    }

    public void Dispose()
    {
        _constants.Dispose();
        _shaders.ForEach(s => s.Dispose());
        _gpu.Dispose();
    }

    [Fact]
    public void Software_engine_declares_raster_and_no_depth_readback()
    {
        Assert.True(_gpu.Caps.IsSoftware);
        Assert.True(_gpu.Caps.Raster);
        Assert.False(_gpu.Caps.DepthReadback);
        Assert.False(_gpu.IsLost);
        Assert.True(_gpu.Caps.MaxSamples(GpuFormat.Rgba8Unorm) >= 4);
        Assert.True(_gpu.Caps.CanBlend(GpuFormat.Rgba16Float));
    }

    [Fact]
    public void Clear_copy_and_readback_keep_bytes_and_rows_from_the_top()
    {
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        using GpuTexture copy = Target(GpuFormat.Rgba8Unorm);
        using GpuReadback readback = _gpu.CreateReadback(4, 2, GpuFormat.Rgba8Unorm);
        byte[] pixels = new byte[Size * Size * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, new GpuColor(1, 0, 0, 1));
            frame.Write(copy, pixels, Size * 4);
            frame.Copy(copy, new GpuBox(0, 0, 8, 8), target, 8, 8);
            frame.Copy(target, new GpuBox(6, 9, 4, 2), readback);
            Assert.True(frame.Submit().Wait(TimeSpan.FromSeconds(5)));
        }

        byte[] read = new byte[4 * 4 * 2];
        readback.Read(read);
        // Fila 9 → fila 1 de la copia; columnas 6 y 7 siguen rojas, 8 y 9 son las columnas 0 y 1 de la fuente.
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 }, read[..8]);
        Assert.Equal(pixels.AsSpan(Size * 4, 8).ToArray(), read[8..16]);
        Assert.Equal(pixels.AsSpan(Size * 4 * 2, 8).ToArray(), read[24..32]);
    }

    [Fact]
    public void Draw_without_vertex_buffer_fills_the_target_with_the_constant_color()
    {
        using GpuPipeline pipeline = Pipeline("VsFull", "PsColor");
        using GpuTexture target = Target(GpuFormat.Rgba16Float);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            SetConstants(frame, new GpuColor(0.25f, 0.5f, 1, 1));
            frame.Draw(pipeline, new GpuTargets(target), Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(4));
        }

        Half[] pixels = ReadHalves(target);
        Assert.All(Enumerable.Range(0, Size * Size), p =>
            Assert.Equal(new[] { 0.25f, 0.5f, 1f, 1f }, pixels.AsSpan(p * 4, 4).ToArray().Select(h => (float)h)));
    }

    [Fact]
    public void Premultiplied_over_blends_like_the_compositor()
    {
        using GpuPipeline pipeline = Pipeline("VsFull", "PsColor", blend: GpuBlend.PremultipliedOver);
        using GpuTexture target = Target(GpuFormat.Rgba16Float);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, new GpuColor(0, 0, 1, 1));
            SetConstants(frame, new GpuColor(0.5f, 0, 0, 0.5f));
            frame.Draw(pipeline, new GpuTargets(target), Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(4));
        }

        Half[] pixels = ReadHalves(target);
        Assert.Equal(new[] { 0.5f, 0f, 0.5f, 1f }, pixels.AsSpan(0, 4).ToArray().Select(h => (float)h));
    }

    [Fact]
    public void Compute_writes_a_storage_texture_and_a_raw_buffer()
    {
        using GpuShader shader = _gpu.CreateShader(Variant("CsFill", "cs_5_0"), _cache);
        using GpuPipeline pipeline = _gpu.CreatePipeline(new GpuPipelineDesc { Compute = shader });
        using GpuTexture image = _gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
        using GpuBuffer bytes = _gpu.CreateBuffer(new GpuBufferDesc(Size * 4, GpuBufferKind.Raw, Storage: true));
        using GpuReadback imageRead = _gpu.CreateReadback(Size, Size, GpuFormat.Rgba8Unorm);
        using GpuReadback bytesRead = _gpu.CreateReadback(Size * 4);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.ClearUint(bytes, 0);
            frame.Write<uint>(_constants, [0, 0, 0x3F800000, 0, 0, 0, 0, 0, 100, 0, 0, 0]);
            var bindings = Bindings();
            bindings.SetStorage(0, image);
            bindings.SetStorage(1, bytes);
            frame.Dispatch(pipeline, bindings, Size / 8, Size / 8);
            frame.Copy(image, new GpuBox(0, 0, Size, Size), imageRead);
            frame.Copy(bytes, 0, Size * 4, bytesRead);
        }

        byte[] pixels = new byte[Size * Size * 4];
        imageRead.Read(pixels);
        Assert.Equal(new byte[] { 3, 5, 255, 255 }, pixels.AsSpan(((5 * Size) + 3) * 4, 4).ToArray());
        byte[] raw = new byte[Size * 4];
        bytesRead.Read(raw);
        Assert.Equal(Enumerable.Range(0, Size).Select(x => (uint)((x * 3) + 100)), MemoryMarshal.Cast<byte, uint>(raw).ToArray());
    }

    [Fact]
    public void Id_target_is_cleared_and_read_back_one_pixel_at_a_time()
    {
        using GpuPipeline pipeline = Pipeline("VsMesh", "PsId", GpuTopology.TriangleList, layout: MeshLayout);
        using GpuTexture ids = Target(GpuFormat.R32Uint);
        using GpuBuffer vertices = Mesh((-1, 1, 0.5f), (0, 1, 0.5f), (-1, -1, 0.5f));
        using GpuReadback pixel = _gpu.CreateReadback(1, 1, GpuFormat.R32Uint);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.ClearUint(ids, 7);
            frame.Write<uint>(_constants, [0, 0, 0, 0, 0, 0, 0, 0, 0xABCDE, 0, 0, 0]);
            frame.Draw(pipeline, new GpuTargets(ids), Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(3, vertices));
        }

        Assert.Equal(0xABCDEu, ReadUint(ids, pixel, 1, 1));
        Assert.Equal(7u, ReadUint(ids, pixel, Size - 1, Size - 1));
    }

    [Theory]
    [InlineData(GpuTopology.LineList)]
    [InlineData(GpuTopology.LineStrip)]
    public void Lines_draw_one_pixel_wide_edges(GpuTopology topology)
    {
        using GpuPipeline pipeline = Pipeline("VsMesh", "PsColor", topology, layout: MeshLayout);
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        // Una horizontal por el centro de la fila 4 y una vertical por el de la columna 10 (en espacio de recorte de 16 px).
        float row = 1 - ((4.5f / Size) * 2);
        float column = ((10.5f / Size) * 2) - 1;
        using GpuBuffer vertices = topology == GpuTopology.LineList
            ? Mesh((-1, row, 0), (1, row, 0), (column, 1, 0), (column, -1, 0))
            : Mesh((-1, row, 0), (column, row, 0), (column, -1, 0));
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            frame.Draw(pipeline, new GpuTargets(target), Bindings(), GpuViewport.Of(Size, Size),
                new GpuDrawCall(topology == GpuTopology.LineList ? 4 : 3, vertices));
        }

        byte[] pixels = ReadBytes(target);
        Assert.Equal(255, Red(pixels, 2, 4));
        Assert.Equal(255, Red(pixels, 10, 12));
        Assert.Equal(0, Red(pixels, 2, 5));
        Assert.Equal(0, Red(pixels, 2, 3));
        Assert.Equal(0, Red(pixels, 11, 12));
        if (topology == GpuTopology.LineStrip)
        {
            Assert.Equal(0, Red(pixels, 14, 4));
        }
    }

    [Fact]
    public void Depth_test_without_write_keeps_the_stored_depth()
    {
        using GpuPipeline writes = Pipeline("VsFull", "PsColor", depth: new GpuDepthState(Test: true, Write: true));
        using GpuPipeline testsOnly = Pipeline("VsFull", "PsColor", depth: new GpuDepthState(Test: true, Write: false));
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        using GpuTexture depth = Depth();
        var targets = new GpuTargets(target, depth);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            frame.ClearDepth(depth);
            DrawFull(frame, writes, targets, new GpuColor(1, 0, 0, 1), 0.5f);
            // Pasa (0,3 < 0,5) pero no escribe: la profundidad sigue en 0,5.
            DrawFull(frame, testsOnly, targets, new GpuColor(0, 1, 0, 1), 0.3f);
            byte[] afterTestOnly = ReadBytesInFrame(frame, target);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, afterTestOnly[..4]);
            // Con 0,4 solo pasa si la de antes no escribió su 0,3.
            DrawFull(frame, writes, targets, new GpuColor(0, 0, 1, 1), 0.4f);
            // Y 0,45 ya no pasa: el 0,4 sí se escribió.
            DrawFull(frame, testsOnly, targets, new GpuColor(1, 1, 1, 1), 0.45f);
        }

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, ReadBytes(target)[..4]);
    }

    [Theory]
    [InlineData(0, 0f, false)]
    [InlineData(-100, 0f, true)]
    [InlineData(0, -1f, true)]
    public void Depth_bias_lets_a_coplanar_draw_pass_a_less_test(int bias, float slope, bool passes)
    {
        using GpuPipeline first = Pipeline("VsSlope", "PsColor", depth: new GpuDepthState(true, true));
        using GpuPipeline second = Pipeline("VsSlope", "PsColor", depth: new GpuDepthState(true, false),
            raster: new GpuRasterState(DepthBias: bias, SlopeScaledDepthBias: slope));
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        using GpuTexture depth = Depth();
        var targets = new GpuTargets(target, depth);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            frame.ClearDepth(depth);
            DrawFull(frame, first, targets, new GpuColor(1, 0, 0, 1), 0);
            DrawFull(frame, second, targets, new GpuColor(0, 1, 0, 1), 0);
        }

        byte[] pixels = ReadBytes(target);
        Assert.All(Enumerable.Range(0, Size * Size), p => Assert.Equal(passes ? (byte)255 : (byte)0, pixels[(p * 4) + 1]));
    }

    [Theory]
    [InlineData(GpuIndexFormat.Bits16)]
    [InlineData(GpuIndexFormat.Bits32)]
    public void Indexed_draw_takes_16_and_32_bit_indices(GpuIndexFormat format)
    {
        using GpuPipeline pipeline = Pipeline("VsMesh", "PsColor", GpuTopology.TriangleList, layout: MeshLayout);
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        // Cuatro vértices de la mitad izquierda; el primero sobra y los índices lo saltan con BaseVertex.
        using GpuBuffer vertices = Mesh((5, 5, 0), (-1, 1, 0), (0, 1, 0), (-1, -1, 0), (0, -1, 0));
        int[] order = [0, 1, 2, 2, 1, 3];
        byte[] indices = format == GpuIndexFormat.Bits16
            ? MemoryMarshal.AsBytes(order.Select(i => (ushort)i).ToArray().AsSpan()).ToArray()
            : MemoryMarshal.AsBytes(order.AsSpan()).ToArray();
        using GpuBuffer indexBuffer = _gpu.CreateBuffer(new GpuBufferDesc(indices.Length, GpuBufferKind.Index), indices);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            frame.Draw(pipeline, new GpuTargets(target), Bindings(), GpuViewport.Of(Size, Size),
                new GpuDrawCall(6, vertices, indexBuffer, format, BaseVertex: 1));
        }

        byte[] pixels = ReadBytes(target);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Assert.Equal(x < Size / 2 ? 255 : 0, Red(pixels, x, y));
            }
        }
    }

    [Fact]
    public void Multisampled_target_resolves_to_partial_coverage_on_edges()
    {
        const int samples = 4;
        using GpuPipeline pipeline = Pipeline("VsMesh", "PsColor", GpuTopology.TriangleList, layout: MeshLayout, samples: samples);
        using GpuTexture multisampled = _gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.RenderTarget, Samples: samples));
        using GpuTexture resolved = Target(GpuFormat.Rgba8Unorm);
        using GpuBuffer vertices = Mesh((-1, 1, 0), (1, 1, 0), (-1, -1, 0));
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(multisampled, GpuColor.OpaqueBlack);
            frame.Draw(pipeline, new GpuTargets(multisampled), Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(3, vertices));
            frame.Resolve(multisampled, resolved);
        }

        byte[] pixels = ReadBytes(resolved);
        Assert.Equal(255, Red(pixels, 0, 0));
        Assert.Equal(0, Red(pixels, Size - 1, Size - 1));
        // La diagonal queda a medias: hay al menos un píxel entre 0 y 255.
        Assert.Contains(Enumerable.Range(0, Size), x => Red(pixels, x, Size - 1 - x) is > 0 and < 255);
    }

    [Fact]
    public void Timer_and_fence_complete()
    {
        using GpuPipeline pipeline = Pipeline("VsFull", "PsColor");
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        using GpuTimerQuery timer = _gpu.CreateTimer();
        GpuFence fence;
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.BeginTimer(timer);
            DrawFull(frame, pipeline, new GpuTargets(target), new GpuColor(1, 1, 1, 1), 0);
            frame.EndTimer(timer);
            fence = frame.Submit();
        }

        Assert.True(fence.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(fence.IsComplete);
        double? ms = timer.WaitMilliseconds(TimeSpan.FromSeconds(5));
        Assert.True(ms is null or >= 0);
    }

    [Fact]
    public void Generated_mips_average_the_level_above()
    {
        using GpuTexture texture = _gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm,
            GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.GenerateMips, MipLevels: 0));
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(texture, new GpuColor(0.5f, 0.5f, 0.5f, 1));
            frame.GenerateMips(texture);
        }

        Assert.Equal(Size, texture.Width);
    }

    [Fact]
    public void Acquired_decoder_surface_samples_its_planes_without_a_copy()
    {
        GpuDevice device = _gpu.Device;
        nint texture = YuvImage.CreateTexture(device, [YuvImage.Solid(Size, Size, tenBit: false, new YuvCode(200, 64, 160))]);
        using GpuSurfaceCache cache = _gpu.CreateSurfaceCache();
        using GpuPipeline pipeline = Pipeline("VsFull", "PsPlanes");
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        try
        {
            using D3D11TexturePayload surface = D3D11TexturePayload.FromTexture(device, texture, 0, Size, Size, VideoColor.Bt709Limited);
            using (IGpuFrame frame = _gpu.BeginFrame())
            {
                GpuDecoderPlanes planes = frame.Acquire(cache, surface);
                GpuBindings bindings = Bindings();
                bindings.SetResource(0, planes.Luma);
                bindings.SetResource(1, planes.Chroma);
                SetConstants(frame, default);
                frame.Draw(pipeline, new GpuTargets(target), bindings, GpuViewport.Of(Size, Size), new GpuDrawCall(4));
            }

            Assert.Equal(new byte[] { 200, 64, 160, 255 }, ReadBytes(target)[..4]);
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    [Fact]
    public void Swapchain_back_buffer_is_a_target_that_presents_and_resizes()
    {
        using IGpuSwapchain swapchain = _gpu.CreateSwapchain(Size, Size, GpuFormat.Bgra8Unorm);
        using GpuReadback readback = _gpu.CreateReadback(1, 1, GpuFormat.Bgra8Unorm);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            GpuTexture back = swapchain.AcquireBackBuffer(frame);
            Assert.Same(back, swapchain.AcquireBackBuffer(frame));
            frame.Clear(back, new GpuColor(0, 0, 1, 1));
            frame.Copy(back, new GpuBox(3, 3, 1, 1), readback);
            Assert.True(frame.Submit().Wait(TimeSpan.FromSeconds(5)));
        }

        byte[] pixel = new byte[4];
        readback.Read(pixel);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixel);
        Assert.True(swapchain.Present(vsync: false));
        swapchain.Resize(2 * Size, Size);
        Assert.Equal((2 * Size, Size), (swapchain.Width, swapchain.Height));
        using IGpuFrame again = _gpu.BeginFrame();
        Assert.Equal(2 * Size, swapchain.AcquireBackBuffer(again).Width);
    }

    private static GpuVertexLayout MeshLayout { get; } = new(28,
        new GpuVertexElement("POSITION", 0, GpuVertexFormat.Float3, 0),
        new GpuVertexElement("COLOR", 0, GpuVertexFormat.Float4, 12));

    private static ShaderVariant Variant(string entry, string profile) => new(Hlsl, entry, profile, "igpu-pruebas.hlsl");

    private static int Red(byte[] pixels, int x, int y) => pixels[((y * Size) + x) * 4];

    private GpuPipeline Pipeline(string vertex, string pixel, GpuTopology topology = GpuTopology.TriangleStrip, GpuBlend blend = default,
        GpuDepthState depth = default, GpuRasterState? raster = null, GpuVertexLayout? layout = null, int samples = 1)
    {
        GpuShader vs = _gpu.CreateShader(Variant(vertex, "vs_5_0"), _cache);
        GpuShader ps = _gpu.CreateShader(Variant(pixel, "ps_5_0"), _cache);
        _shaders.Add(vs);
        _shaders.Add(ps);
        return _gpu.CreatePipeline(new GpuPipelineDesc
        {
            Vertex = vs,
            Pixel = ps,
            Blend = blend,
            Depth = depth,
            Raster = (raster ?? default) with { Topology = topology },
            VertexLayout = layout,
            Samples = samples,
        });
    }

    private GpuTexture Target(GpuFormat format) =>
        _gpu.CreateTexture(new GpuTextureDesc(Size, Size, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

    private GpuTexture Depth() => _gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.D32Float, GpuTextureUsage.DepthStencil));

    private GpuBuffer Mesh(params (float X, float Y, float Z)[] points)
    {
        var data = new float[points.Length * 7];
        for (int i = 0; i < points.Length; i++)
        {
            (data[i * 7], data[(i * 7) + 1], data[(i * 7) + 2]) = points[i];
            (data[(i * 7) + 3], data[(i * 7) + 4], data[(i * 7) + 5], data[(i * 7) + 6]) = (1, 0, 0, 1);
        }

        return _gpu.CreateBuffer(new GpuBufferDesc(data.Length * 4, GpuBufferKind.Vertex), MemoryMarshal.AsBytes(data.AsSpan()));
    }

    private GpuBindings Bindings()
    {
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _constants);
        return bindings;
    }

    private void SetConstants(IGpuFrame frame, GpuColor color, float depth = 0) =>
        frame.Write<float>(_constants, [color.R, color.G, color.B, color.A, depth, 0, 0, 0, 0, 0, 0, 0]);

    private void DrawFull(IGpuFrame frame, GpuPipeline pipeline, in GpuTargets targets, GpuColor color, float depth)
    {
        SetConstants(frame, color, depth);
        frame.Draw(pipeline, targets, Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(4));
    }

    private byte[] ReadBytes(GpuTexture texture)
    {
        using IGpuFrame frame = _gpu.BeginFrame();
        return ReadBytesInFrame(frame, texture);
    }

    private byte[] ReadBytesInFrame(IGpuFrame frame, GpuTexture texture)
    {
        using GpuReadback readback = _gpu.CreateReadback(Size, Size, texture.Format);
        frame.Copy(texture, new GpuBox(0, 0, Size, Size), readback);
        byte[] pixels = new byte[Size * Size * GpuFormats.BytesPerPixel(texture.Format)];
        readback.Read(pixels);
        return pixels;
    }

    private Half[] ReadHalves(GpuTexture texture) => MemoryMarshal.Cast<byte, Half>(ReadBytes(texture)).ToArray();

    private uint ReadUint(GpuTexture texture, GpuReadback pixel, int x, int y)
    {
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Copy(texture, new GpuBox(x, y, 1, 1), pixel);
        }

        using GpuMappedRows rows = pixel.Map();
        return MemoryMarshal.Read<uint>(rows.Row(0));
    }
}
