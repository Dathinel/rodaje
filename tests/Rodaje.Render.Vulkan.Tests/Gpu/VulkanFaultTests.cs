using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Render.Export;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.Vulkan;
using Rodaje.Render.Spirv;
using TerraFX.Interop.Vulkan;

namespace Rodaje.Render.Tests.Gpu;

/// <summary>
/// Lo que la revisiÃ³n de la fase 4 encontrÃ³ en el motor Vulkan: formatos de las imÃ¡genes de escritura, GPU perdida, esperas sobre un
/// envÃ­o reciclado y recursos a medio crear. Cada una con su propio dispositivo: las que lo rompen no estropean el compartido.
/// Las fugas las ve la pasada de validaciÃ³n de la CI (<c>RODAJE_VK_VALIDACION=1</c>): <see cref="VulkanGpu.ValidationErrors"/> no crece.
/// </summary>
public sealed unsafe class VulkanFaultTests
{
    private const int Size = 16;

    [Fact]
    public void Manifest_declares_storage_images_without_a_format()
    {
        int checkedImages = 0;
        foreach (string name in typeof(SpirvShaders).Assembly.GetManifestResourceNames().Where(n => n.EndsWith(".spv", StringComparison.Ordinal)))
        {
            using Stream stream = typeof(SpirvShaders).Assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            SpirvModule module = SpirvModule.Read(memory.ToArray());
            Assert.DoesNotContain(SpirvModule.CapabilityExtendedFormats, module.Capabilities);
            foreach (SpirvBinding binding in module.Bindings.Where(b => b.Kind == SpirvDescriptor.StorageImage))
            {
                Assert.True(binding.ImageFormat == 0, $"{name}: u{binding.Binding - 48} declara el formato {binding.ImageFormat}.");
                checkedImages++;
            }
        }

        Assert.True(checkedImages >= 10, $"Solo {checkedImages} imÃ¡genes de escritura en el manifiesto.");
    }

    /// <summary>La pasada de validación de la CI solo vale si las capas cargan de verdad.</summary>
    [VulkanFact]
    public void Validation_layers_are_active_when_requested()
    {
        using VulkanGpu gpu = VulkanGpu.Create(preferSoftware: true);
        Assert.Equal(Environment.GetEnvironmentVariable(VulkanGpu.ValidationVariable) == "1", VulkanGpu.ValidationActive);
    }

    [VulkanFact]
    public void Storage_image_with_another_declared_format_is_rejected_when_bound()
    {
        VulkanGpu gpu = VulkanTestGpu.Gpu;
        // CsFill con la imagen declarada Rgba32f, como la compilaba DXC sin -fspv-use-unknown-image-format.
        using GpuShader shader = gpu.CreateShader(WithStorageFormat(VulkanGpuTests.Spirv("CsFill"), 1), "CsFill-Rgba32f");
        using GpuPipeline pipeline = gpu.CreatePipeline(new GpuPipelineDesc { Compute = shader });
        using GpuTexture image = gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
        using GpuBuffer bytes = gpu.CreateBuffer(new GpuBufferDesc(Size * 4, GpuBufferKind.Raw, Storage: true));
        using IGpuFrame frame = gpu.BeginFrame();
        var bindings = default(GpuBindings);
        bindings.SetStorage(0, image);
        bindings.SetStorage(1, bytes);
        RenderException error = Assert.Throws<RenderException>(() => frame.Dispatch(pipeline, bindings, 1, 1));
        Assert.Contains("Rgba8Unorm", error.Message, StringComparison.Ordinal);
    }

    [VulkanFact]
    public void Lost_device_after_an_accepted_submit_throws_instead_of_waiting()
    {
        int errors = VulkanGpu.ValidationErrors.Count;
        using (VulkanGpu gpu = VulkanGpu.Create(preferSoftware: true))
        {
            using GpuTexture texture = gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled));
            using GpuReadback read = gpu.CreateReadback(Size, Size, GpuFormat.Rgba8Unorm);
            using (IGpuFrame frame = gpu.BeginFrame())
            {
                frame.Copy(texture, new GpuBox(0, 0, Size, Size), read);
            }

            gpu.FenceStatusForTests = static () => VkResult.VK_ERROR_DEVICE_LOST;
            Assert.Throws<RenderException>(() => read.TryMap(out _));
            Assert.True(gpu.IsLost);
            Assert.Throws<RenderException>(() => read.Map());
        }

        Assert.Equal(errors, VulkanGpu.ValidationErrors.Count);
    }

    [VulkanFact]
    [Trait("Categoria", "TiempoReal")]
    public void Export_read_gives_up_after_its_timeout_and_on_cancel()
    {
        using VulkanGpu gpu = VulkanGpu.Create(preferSoftware: true);
        using GpuTexture picture = gpu.CreateTexture(new GpuTextureDesc(64, 64, GpuFormat.Rgba16Float, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));
        var format = new ExportVideoFormat(64, 64, new Rational(30, 1), ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using var converter = new ExportFrameConverter(gpu, 64, 64, format) { ReadTimeout = TimeSpan.FromMilliseconds(300) };
        // Una GPU colgada: ninguna valla termina.
        gpu.FenceStatusForTests = static () => VkResult.VK_NOT_READY;
        converter.Submit(picture, 0);
        Assert.Throws<RenderException>(() => converter.TryRead(static (in ExportVideoFrame _) => { }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => converter.TryRead(static (in ExportVideoFrame _) => { }, canceled.Token));
        gpu.FenceStatusForTests = null;
        Assert.True(converter.TryRead(static (in ExportVideoFrame _) => { }));
    }

    [VulkanFact]
    public void Wait_keeps_its_submission_out_of_reuse_until_it_returns()
    {
        using VulkanGpu gpu = VulkanGpu.Create(preferSoftware: true);
        using GpuTexture texture = gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled));
        using GpuReadback read = gpu.CreateReadback(Size, Size, GpuFormat.Rgba8Unorm);
        GpuFence fence;
        using (IGpuFrame frame = gpu.BeginFrame())
        {
            frame.Copy(texture, new GpuBox(0, 0, Size, Size), read);
            fence = frame.Submit();
        }

        var reused = new List<FrameContext>();
        FrameContext? waited = null;
        // Hasta que Wait elija la valla, nadie ve el envÃ­o terminado (si no, Wait volverÃ­a sin esperar).
        gpu.FenceStatusForTests = static () => VkResult.VK_NOT_READY;
        gpu.WaitSelected = context =>
        {
            gpu.WaitSelected = null;
            gpu.FenceStatusForTests = null;
            waited = context;
            // Entre elegir la valla y esperarla, el envÃ­o termina y otros cuadros piden contextos (Poll los recicla).
            VkFence handle = context.Fence;
            Assert.Equal(VkResult.VK_SUCCESS, Vulkan.vkWaitForFences(gpu.Device, 1, &handle, Vulkan.VK_TRUE, 5_000_000_000));
            for (int i = 0; i < 3; i++)
            {
                using IGpuFrame other = gpu.BeginFrame();
                reused.Add(((VulkanFrame)other).Context);
            }
        };

        Assert.True(fence.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotNull(waited);
        Assert.DoesNotContain(reused, context => ReferenceEquals(context, waited));
    }

    [VulkanTheory]
    [InlineData("vkBindBufferMemory")]
    [InlineData("vkMapMemory")]
    [InlineData("vkCreatePipelineLayout")]
    [InlineData("vkCreateComputePipelines")]
    [InlineData("vkCreateImageView")]
    public void Failed_creation_leaves_nothing_behind(string failing)
    {
        int errors = VulkanGpu.ValidationErrors.Count;
        using (VulkanGpu gpu = VulkanGpu.Create(preferSoftware: true))
        {
            using GpuShader shader = gpu.CreateShader(VulkanGpuTests.Spirv("CsFill"), "CsFill");
            VulkanGpu.FailForTests = failing;
            try
            {
                Assert.Throws<RenderException>(() =>
                {
                    using GpuBuffer buffer = gpu.CreateBuffer(new GpuBufferDesc(256, GpuBufferKind.Raw, Storage: true));
                    using GpuReadback read = gpu.CreateReadback(256);
                    using GpuPipeline pipeline = gpu.CreatePipeline(new GpuPipelineDesc { Compute = shader });
                    using GpuTexture texture = gpu.CreateTexture(new GpuTextureDesc(Size, Size, GpuFormat.Rgba8Unorm, GpuTextureUsage.Sampled));
                });
            }
            finally
            {
                VulkanGpu.FailForTests = null;
            }
        }

        Assert.Equal(errors, VulkanGpu.ValidationErrors.Count);
    }

    [VulkanFact]
    public void Failed_device_setup_releases_the_device()
    {
        int errors = VulkanGpu.ValidationErrors.Count;
        // El muestreador es lo Ãºltimo de los recursos por omisiÃ³n: texturas, bÃºfer y dispositivo ya existen y deben soltarse.
        VulkanGpu.FailForTests = "vkCreateSampler";
        try
        {
            Assert.False(VulkanGpu.TryCreate(preferSoftware: true, out VulkanGpu? gpu, out string? reason));
            Assert.Null(gpu);
            Assert.Contains("vkCreateSampler", reason, StringComparison.Ordinal);
        }
        finally
        {
            VulkanGpu.FailForTests = null;
        }

        Assert.Equal(errors, VulkanGpu.ValidationErrors.Count);
    }

    /// <summary>Cambia el <c>Image Format</c> de las imÃ¡genes de escritura (<c>OpTypeImage</c> con <c>Sampled</c> = 2) del mÃ³dulo.</summary>
    private static byte[] WithStorageFormat(byte[] spirv, uint format)
    {
        byte[] copy = (byte[])spirv.Clone();
        Span<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(copy.AsSpan());
        for (int i = 5; i < words.Length;)
        {
            int count = (int)(words[i] >> 16);
            if ((words[i] & 0xFFFF) == 25 && count >= 9 && words[i + 7] == 2)
            {
                words[i + 8] = format;
            }

            i += count;
        }

        return copy;
    }
}
