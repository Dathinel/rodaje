using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.Vulkan;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>
/// El motor Vulkan de <see cref="IGpu"/>: las mismas comprobaciones que el de D3D11 (<see cref="GpuEngineChecks"/>) con el SPIR-V de
/// <c>igpu-pruebas.hlsl</c>, y lo propio de Vulkan.
/// </summary>
public sealed class VulkanGpuTests : IDisposable
{
    private readonly GpuEngineChecks? _checks;

    public VulkanGpuTests()
    {
        if (VulkanTestGpu.SkipReason is null)
        {
            _checks = new GpuEngineChecks(Gpu, static (entry, _) => Gpu.CreateShader(Spirv(entry), entry));
        }
    }

    private static VulkanGpu Gpu => VulkanTestGpu.Gpu;

    private GpuEngineChecks Checks => _checks!;

    public void Dispose() => _checks?.Dispose();

    [VulkanFact]
    public void Engine_declares_raster_and_depth_readback()
    {
        Assert.True(Gpu.Caps.Raster);
        Assert.True(Gpu.Caps.DepthReadback);
        Assert.False(Gpu.IsLost);
        Assert.True(Gpu.Caps.MaxSamples(GpuFormat.Rgba8Unorm) >= 4);
        Assert.True(Gpu.Caps.CanBlend(GpuFormat.Rgba16Float));
        Assert.False(string.IsNullOrEmpty(Gpu.Caps.Adapter));
    }

    [VulkanFact]
    public void Clear_copy_and_readback_keep_bytes_and_rows_from_the_top() => Checks.Clear_copy_and_readback_keep_bytes_and_rows_from_the_top();

    [VulkanFact]
    public void Draw_without_vertex_buffer_fills_the_target_with_the_constant_color() => Checks.Draw_without_vertex_buffer_fills_the_target_with_the_constant_color();

    [VulkanFact]
    public void Premultiplied_over_blends_like_the_compositor() => Checks.Premultiplied_over_blends_like_the_compositor();

    [VulkanFact]
    public void Compute_writes_a_storage_texture_and_a_raw_buffer() => Checks.Compute_writes_a_storage_texture_and_a_raw_buffer();

    [VulkanFact]
    public void Id_target_is_cleared_and_read_back_one_pixel_at_a_time() => Checks.Id_target_is_cleared_and_read_back_one_pixel_at_a_time();

    [VulkanTheory]
    [InlineData(GpuTopology.LineList)]
    [InlineData(GpuTopology.LineStrip)]
    public void Lines_draw_one_pixel_wide_edges(GpuTopology topology) => Checks.Lines_draw_one_pixel_wide_edges(topology);

    [VulkanTheory]
    [InlineData(GpuCullMode.Back)]
    [InlineData(GpuCullMode.Front)]
    [InlineData(GpuCullMode.None)]
    public void Clockwise_triangles_are_the_front_faces(GpuCullMode cull) => Checks.Clockwise_triangles_are_the_front_faces(cull);

    [VulkanFact]
    public void Depth_test_without_write_keeps_the_stored_depth() => Checks.Depth_test_without_write_keeps_the_stored_depth();

    [VulkanTheory]
    [InlineData(0, 0f, false)]
    [InlineData(-100, 0f, true)]
    [InlineData(0, -1f, true)]
    public void Depth_bias_lets_a_coplanar_draw_pass_a_less_test(int bias, float slope, bool passes) => Checks.Depth_bias_lets_a_coplanar_draw_pass_a_less_test(bias, slope, passes);

    [VulkanTheory]
    [InlineData(GpuIndexFormat.Bits16)]
    [InlineData(GpuIndexFormat.Bits32)]
    public void Indexed_draw_takes_16_and_32_bit_indices(GpuIndexFormat format) => Checks.Indexed_draw_takes_16_and_32_bit_indices(format);

    [VulkanFact]
    public void Multisampled_target_resolves_to_partial_coverage_on_edges() => Checks.Multisampled_target_resolves_to_partial_coverage_on_edges();

    [VulkanFact]
    public void Timer_and_fence_complete() => Checks.Timer_and_fence_complete();

    [VulkanFact]
    public void Generated_mips_average_the_level_above() => Checks.Generated_mips_average_the_level_above();

    internal static byte[] Spirv(string entry)
    {
        using Stream stream = typeof(VulkanGpuTests).Assembly.GetManifestResourceStream(entry + ".spv")
            ?? throw new InvalidOperationException($"Falta {entry}.spv: Gpu/compilar-spirv.ps1.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
