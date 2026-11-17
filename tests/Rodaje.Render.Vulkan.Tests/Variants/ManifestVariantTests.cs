using Rodaje.Render.Gpu;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Gpu;

namespace Rodaje.Render.Tests.Variants;

[Collection("Vulkan variants")]
public sealed class ManifestVariantTests
{
    public static IEnumerable<object[]> Variants => ShaderManifest.All.Select(v => new object[] { ShaderManifest.Id(v) });

    [VulkanTheory]
    [MemberData(nameof(Variants))]
    public void Manifest_variant_creates_shader_and_pipeline(string id)
    {
        Assert.Equal(100, ShaderManifest.All.Count);
        IGpu gpu = VulkanTestGpu.Gpu;
        ShaderVariant variant = ShaderManifest.All.Single(v => ShaderManifest.Id(v) == id);
        using GpuShader shader = gpu.CreateShader(variant);
        if (variant.Profile.StartsWith("vs_", StringComparison.Ordinal))
        {
            return;
        }

        if (variant.Profile.StartsWith("cs_", StringComparison.Ordinal))
        {
            using GpuPipeline compute = gpu.CreatePipeline(new GpuPipelineDesc { Compute = shader });
            return;
        }

        using GpuShader vertex = gpu.CreateShader(CaseVariants.Vertex);
        using GpuPipeline pipeline = gpu.CreatePipeline(new GpuPipelineDesc { Vertex = vertex, Pixel = shader });
        using GpuTexture target = gpu.CreateTexture(new GpuTextureDesc(8, 8, GpuFormat.Rgba16Float, GpuTextureUsage.RenderTarget));
        using IGpuFrame frame = gpu.BeginFrame();
        frame.Draw(pipeline, new GpuTargets(target), default, GpuViewport.Of(8, 8), new GpuDrawCall(4));
    }
}

[CollectionDefinition("Vulkan variants", DisableParallelization = true)]
public sealed class VulkanVariantsGroup;


