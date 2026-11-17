using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>
/// El motor D3D11 de <see cref="IGpu"/> en WARP (está en todo Windows y da los mismos bytes en cada corrida): las comprobaciones comunes
/// a todos los motores (<see cref="GpuEngineChecks"/>) y lo propio de D3D11 (superficies de D3D11VA, cadena de composición).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public sealed class D3D11GpuTests : IDisposable
{
    private static readonly string s_hlsl = ReadHlsl();
    private readonly D3D11Gpu _gpu = D3D11Gpu.Create(preferSoftware: true);
    private readonly ShaderCache _cache = new(null);
    private readonly GpuEngineChecks _checks;

    public D3D11GpuTests()
    {
        _checks = new GpuEngineChecks(_gpu, (entry, profile) => _gpu.CreateShader(new ShaderVariant(s_hlsl, entry, profile, "igpu-pruebas.hlsl"), _cache));
    }

    public void Dispose()
    {
        _checks.Dispose();
        _gpu.Dispose();
    }

    [D3D11Fact("motor D3D11 en WARP")]
    public void Software_engine_declares_raster_and_no_depth_readback()
    {
        Assert.True(_gpu.Caps.IsSoftware);
        Assert.True(_gpu.Caps.Raster);
        Assert.False(_gpu.Caps.DepthReadback);
        Assert.False(_gpu.IsLost);
        Assert.True(_gpu.Caps.MaxSamples(GpuFormat.Rgba8Unorm) >= 4);
        Assert.True(_gpu.Caps.CanBlend(GpuFormat.Rgba16Float));
    }

    [D3D11Fact("motor D3D11 en WARP")]
    public void Clear_copy_and_readback_keep_bytes_and_rows_from_the_top() => _checks.Clear_copy_and_readback_keep_bytes_and_rows_from_the_top();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Draw_without_vertex_buffer_fills_the_target_with_the_constant_color() => _checks.Draw_without_vertex_buffer_fills_the_target_with_the_constant_color();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Premultiplied_over_blends_like_the_compositor() => _checks.Premultiplied_over_blends_like_the_compositor();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Compute_writes_a_storage_texture_and_a_raw_buffer() => _checks.Compute_writes_a_storage_texture_and_a_raw_buffer();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Acquired_surface_outlives_its_owner_until_the_frame_completes() => _checks.Acquired_surface_outlives_its_owner_until_the_frame_completes();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Id_target_is_cleared_and_read_back_one_pixel_at_a_time() => _checks.Id_target_is_cleared_and_read_back_one_pixel_at_a_time();

    [D3D11Theory("motor D3D11 en WARP")]
    [InlineData(GpuTopology.LineList)]
    [InlineData(GpuTopology.LineStrip)]
    public void Lines_draw_one_pixel_wide_edges(GpuTopology topology) => _checks.Lines_draw_one_pixel_wide_edges(topology);

    [D3D11Theory("motor D3D11 en WARP")]
    [InlineData(GpuCullMode.Back)]
    [InlineData(GpuCullMode.Front)]
    [InlineData(GpuCullMode.None)]
    public void Clockwise_triangles_are_the_front_faces(GpuCullMode cull) => _checks.Clockwise_triangles_are_the_front_faces(cull);

    [D3D11Fact("motor D3D11 en WARP")]
    public void Depth_test_without_write_keeps_the_stored_depth() => _checks.Depth_test_without_write_keeps_the_stored_depth();

    [D3D11Theory("motor D3D11 en WARP")]
    [InlineData(0, 0f, false)]
    [InlineData(-100, 0f, true)]
    [InlineData(0, -1f, true)]
    public void Depth_bias_lets_a_coplanar_draw_pass_a_less_test(int bias, float slope, bool passes) => _checks.Depth_bias_lets_a_coplanar_draw_pass_a_less_test(bias, slope, passes);

    [D3D11Theory("motor D3D11 en WARP")]
    [InlineData(GpuIndexFormat.Bits16)]
    [InlineData(GpuIndexFormat.Bits32)]
    public void Indexed_draw_takes_16_and_32_bit_indices(GpuIndexFormat format) => _checks.Indexed_draw_takes_16_and_32_bit_indices(format);

    [D3D11Fact("motor D3D11 en WARP")]
    public void Multisampled_target_resolves_to_partial_coverage_on_edges() => _checks.Multisampled_target_resolves_to_partial_coverage_on_edges();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Timer_and_fence_complete() => _checks.Timer_and_fence_complete();

    [D3D11Fact("motor D3D11 en WARP")]
    public void Generated_mips_average_the_level_above() => _checks.Generated_mips_average_the_level_above();
    [D3D11Fact("motor D3D11 en WARP")]
    public void Acquired_decoder_surface_samples_its_planes_without_a_copy()
    {
        GpuDevice device = _gpu.Device;
        nint texture = YuvImage.CreateTexture(device, [YuvImage.Solid(GpuEngineChecks.Size, GpuEngineChecks.Size, tenBit: false, new YuvCode(200, 64, 160))]);
        using GpuSurfaceCache cache = _gpu.CreateSurfaceCache();
        using GpuPipeline pipeline = _checks.Pipeline("VsFull", "PsPlanes");
        using GpuTexture target = _checks.Target(GpuFormat.Rgba8Unorm);
        try
        {
            using D3D11TexturePayload surface = D3D11TexturePayload.FromTexture(device, texture, 0, GpuEngineChecks.Size, GpuEngineChecks.Size, VideoColor.Bt709Limited);
            using (IGpuFrame frame = _gpu.BeginFrame())
            {
                GpuDecoderPlanes planes = frame.Acquire(cache, surface);
                GpuBindings bindings = _checks.Bindings();
                bindings.SetResource(0, planes.Luma);
                bindings.SetResource(1, planes.Chroma);
                _checks.SetConstants(frame, default);
                frame.Draw(pipeline, new GpuTargets(target), bindings, GpuViewport.Of(GpuEngineChecks.Size, GpuEngineChecks.Size), new GpuDrawCall(4));
            }

            Assert.Equal(new byte[] { 200, 64, 160, 255 }, _checks.ReadBytes(target)[..4]);
        }
        finally
        {
            Marshal.Release(texture);
        }
    }

    [D3D11Fact("motor D3D11 en WARP")]
    public void Swapchain_back_buffer_is_a_target_that_presents_and_resizes()
    {
        using IGpuSwapchain swapchain = _gpu.CreateSwapchain(GpuEngineChecks.Size, GpuEngineChecks.Size, GpuFormat.Bgra8Unorm);
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
        swapchain.Resize(2 * GpuEngineChecks.Size, GpuEngineChecks.Size);
        Assert.Equal((2 * GpuEngineChecks.Size, GpuEngineChecks.Size), (swapchain.Width, swapchain.Height));
        using IGpuFrame again = _gpu.BeginFrame();
        Assert.Equal(2 * GpuEngineChecks.Size, swapchain.AcquireBackBuffer(again).Width);
    }


    private static string ReadHlsl()
    {
        using Stream stream = typeof(D3D11GpuTests).Assembly.GetManifestResourceStream("igpu-pruebas.hlsl")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
