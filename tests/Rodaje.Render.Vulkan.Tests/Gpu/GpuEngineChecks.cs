using System.Runtime.InteropServices;
using Rodaje.Media.Video;
using Rodaje.Render.Gpu;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>
/// Lo que todo motor de <see cref="IGpu"/> cumple igual, con los sombreadores de <c>igpu-pruebas.hlsl</c>: el núcleo que usa Rodaje y el
/// nivel <i>Raster</i> que pide Unicomp. Lo comparten las pruebas del motor D3D11 (WARP) y del Vulkan.
/// </summary>
internal sealed class GpuEngineChecks : IDisposable
{
    public const int Size = 16;
    private readonly IGpu _gpu;
    private readonly Func<string, string, GpuShader> _shader;
    private readonly GpuBuffer _constants;
    private readonly List<GpuShader> _shaders = [];

    /// <param name="shader">Crea el sombreador de un punto de entrada de <c>igpu-pruebas.hlsl</c> con su perfil (<c>vs_5_0</c>…).</param>
    public GpuEngineChecks(IGpu gpu, Func<string, string, GpuShader> shader)
    {
        _gpu = gpu;
        _shader = shader;
        _constants = _gpu.CreateBuffer(new GpuBufferDesc(48, GpuBufferKind.Constant));
    }

    public void Dispose()
    {
        _constants.Dispose();
        _shaders.ForEach(s => s.Dispose());
    }

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

    public void Compute_writes_a_storage_texture_and_a_raw_buffer()
    {
        using GpuShader shader = _shader("CsFill", "cs_5_0");
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

    /// <summary>Contrato de <c>Acquire</c>: la superficie vive hasta que termina el cuadro aunque su dueño la suelte justo después.</summary>
    public void Acquired_surface_outlives_its_owner_until_the_frame_completes()
    {
        byte[] color = [10, 20, 30, 255];
        GpuTexture texture = _gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled),
            [.. Enumerable.Range(0, Size * Size).SelectMany(_ => color)]);
        var surface = new GpuTextureSurface(texture, null, FramePixelFormat.Bgra8, VideoColor.Bt709Limited);
        using GpuSurfaceCache cache = _gpu.CreateSurfaceCache();
        using GpuReadback read = _gpu.CreateReadback(Size, Size, GpuFormat.Rgba8Unorm);
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Acquire(cache, surface);
            surface.Release();
            Assert.False(surface.IsReleased);
            frame.Copy(texture, new GpuBox(0, 0, Size, Size), read);
        }

        byte[] pixels = new byte[Size * Size * 4];
        read.Read(pixels);
        Assert.Equal(color, pixels.AsSpan(0, 4).ToArray());
        Assert.True(surface.IsReleased);
        Assert.Throws<ArgumentException>(() =>
        {
            using IGpuFrame frame = _gpu.BeginFrame();
            frame.Acquire(cache, surface);
        });
    }

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

    public void Clockwise_triangles_are_the_front_faces(GpuCullMode cull)
    {
        using GpuPipeline pipeline = Pipeline("VsMesh", "PsColor", GpuTopology.TriangleList, raster: new GpuRasterState(Cull: cull), layout: MeshLayout);
        using GpuTexture target = Target(GpuFormat.Rgba8Unorm);
        // Izquierda en el sentido de las agujas del reloj en pantalla; derecha, al revés.
        using GpuBuffer vertices = Mesh((-1, 1, 0), (0, 1, 0), (-1, -1, 0), (1, 1, 0), (0, 1, 0), (1, -1, 0));
        using (IGpuFrame frame = _gpu.BeginFrame())
        {
            frame.Clear(target, GpuColor.OpaqueBlack);
            frame.Draw(pipeline, new GpuTargets(target), Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(6, vertices));
        }

        byte[] pixels = ReadBytes(target);
        Assert.Equal(cull == GpuCullMode.Front ? 0 : 255, Red(pixels, 2, 2));
        Assert.Equal(cull == GpuCullMode.Back ? 0 : 255, Red(pixels, Size - 2, 2));
    }

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


    private static GpuVertexLayout MeshLayout { get; } = new(28,
        new GpuVertexElement("POSITION", 0, GpuVertexFormat.Float3, 0),
        new GpuVertexElement("COLOR", 0, GpuVertexFormat.Float4, 12));

    private static int Red(byte[] pixels, int x, int y) => pixels[((y * Size) + x) * 4];

    public GpuPipeline Pipeline(string vertex, string pixel, GpuTopology topology = GpuTopology.TriangleStrip, GpuBlend blend = default,
        GpuDepthState depth = default, GpuRasterState? raster = null, GpuVertexLayout? layout = null, int samples = 1)
    {
        GpuShader vs = _shader(vertex, "vs_5_0");
        GpuShader ps = _shader(pixel, "ps_5_0");
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

    public GpuTexture Target(GpuFormat format) =>
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

    public GpuBindings Bindings()
    {
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _constants);
        return bindings;
    }

    public void SetConstants(IGpuFrame frame, GpuColor color, float depth = 0) =>
        frame.Write<float>(_constants, [color.R, color.G, color.B, color.A, depth, 0, 0, 0, 0, 0, 0, 0]);

    private void DrawFull(IGpuFrame frame, GpuPipeline pipeline, in GpuTargets targets, GpuColor color, float depth)
    {
        SetConstants(frame, color, depth);
        frame.Draw(pipeline, targets, Bindings(), GpuViewport.Of(Size, Size), new GpuDrawCall(4));
    }

    public byte[] ReadBytes(GpuTexture texture)
    {
        using IGpuFrame frame = _gpu.BeginFrame();
        return ReadBytesInFrame(frame, texture);
    }

    public byte[] ReadBytesInFrame(IGpuFrame frame, GpuTexture texture)
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
