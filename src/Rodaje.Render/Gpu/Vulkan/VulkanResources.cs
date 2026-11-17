using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>Una imagen con sus vistas: muestreo 2D y 2D_ARRAY (los planos), 3D, destino y escritura del mip 0.</summary>
internal sealed unsafe class VulkanTexture : GpuTexture
{
    private readonly VulkanGpu _gpu;
    private VkImageView _storageArray;

    private VulkanTexture(VulkanGpu gpu, GpuTextureDesc desc) : base(desc)
    {
        _gpu = gpu;
    }

    public VkImage Image { get; private set; }

    public VkDeviceMemory Memory { get; private set; }

    public VkFormat NativeFormat { get; private set; }

    public int Mips { get; private set; }

    public bool ThreeD { get; private set; }

    public VkImageAspectFlags Aspect { get; private set; }

    public VkImageView Sampled2D { get; private set; }

    public VkImageView SampledArray { get; private set; }

    public VkImageView Sampled3D { get; private set; }

    public VkImageView Attachment { get; private set; }

    public VkImageView Storage2D { get; private set; }

    /// <summary>Estado de la imagen entera para las barreras (lo toca solo quien graba).</summary>
    public VkImageLayout Layout { get; set; }

    /// <summary>La última orden que la tocó escribió en ella.</summary>
    public bool Written { get; set; }

    public static VulkanTexture Create(VulkanGpu gpu, GpuTextureDesc desc, ReadOnlySpan<byte> initialData, bool forceThreeD = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Height);
        bool threeD = forceThreeD || desc.Depth > 1;
        int mips = desc.MipLevels == 0 ? 1 + (int)Math.Log2(Math.Max(desc.Width, desc.Height)) : desc.MipLevels;
        bool depth = desc.Format == GpuFormat.D32Float;
        VkFormat format = VulkanFormats.Native(desc.Format);
        VkImageUsageFlags usage = VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        if ((desc.Usage & GpuTextureUsage.Sampled) != 0) usage |= VkImageUsageFlags.VK_IMAGE_USAGE_SAMPLED_BIT;
        if ((desc.Usage & GpuTextureUsage.RenderTarget) != 0) usage |= VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT;
        if ((desc.Usage & GpuTextureUsage.Storage) != 0) usage |= VkImageUsageFlags.VK_IMAGE_USAGE_STORAGE_BIT;
        if ((desc.Usage & GpuTextureUsage.DepthStencil) != 0) usage |= VkImageUsageFlags.VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT;
        if ((desc.Usage & GpuTextureUsage.Storage) != 0
            && (gpu.FormatFeatures(format) & VkFormatFeatureFlags.VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT) == 0)
        {
            throw new RenderException($"La GPU no admite escritura desde un shader en {desc.Format}.");
        }

        var info = new VkImageCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            imageType = threeD ? VkImageType.VK_IMAGE_TYPE_3D : VkImageType.VK_IMAGE_TYPE_2D,
            format = format,
            extent = new VkExtent3D { width = (uint)desc.Width, height = (uint)desc.Height, depth = (uint)Math.Max(1, desc.Depth) },
            mipLevels = (uint)mips,
            arrayLayers = 1,
            samples = (VkSampleCountFlags)Math.Max(1, desc.Samples),
            tiling = VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
            usage = usage,
            initialLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
        };
        var texture = new VulkanTexture(gpu, desc)
        {
            NativeFormat = format,
            Mips = mips,
            ThreeD = threeD,
            Aspect = depth ? VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT : VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
            Layout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
        };
        try
        {
            VkImage image;
            gpu.Check(vkCreateImage(gpu.Device, &info, null, &image), "vkCreateImage");
            texture.Image = image;
            VkMemoryRequirements requirements;
            vkGetImageMemoryRequirements(gpu.Device, image, &requirements);
            texture.Memory = gpu.Allocate(requirements, VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
            gpu.Check(vkBindImageMemory(gpu.Device, image, texture.Memory, 0), "vkBindImageMemory");
            if ((usage & VkImageUsageFlags.VK_IMAGE_USAGE_SAMPLED_BIT) != 0)
            {
                if (threeD)
                {
                    texture.Sampled3D = texture.CreateView(VkImageViewType.VK_IMAGE_VIEW_TYPE_3D, 0, mips);
                }
                else
                {
                    texture.Sampled2D = texture.CreateView(VkImageViewType.VK_IMAGE_VIEW_TYPE_2D, 0, mips);
                    texture.SampledArray = texture.CreateView(VkImageViewType.VK_IMAGE_VIEW_TYPE_2D_ARRAY, 0, mips);
                }
            }

            if ((usage & (VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT)) != 0)
            {
                texture.Attachment = texture.CreateView(VkImageViewType.VK_IMAGE_VIEW_TYPE_2D, 0, 1);
            }

            if ((usage & VkImageUsageFlags.VK_IMAGE_USAGE_STORAGE_BIT) != 0)
            {
                texture.Storage2D = texture.CreateView(threeD ? VkImageViewType.VK_IMAGE_VIEW_TYPE_3D : VkImageViewType.VK_IMAGE_VIEW_TYPE_2D, 0, 1);
            }

            if (!initialData.IsEmpty)
            {
                texture.UploadInitial(initialData);
            }
        }
        catch
        {
            texture.DestroyNow();
            throw;
        }

        return texture;
    }

    /// <summary>La vista de muestreo que pide el sombreador; <c>default</c> si la textura no la tiene.</summary>
    public VkImageView Sampled(SpirvDimension dimension) => dimension switch
    {
        SpirvDimension.D3 => Sampled3D,
        SpirvDimension.D2Array => SampledArray,
        _ => Sampled2D,
    };

    /// <summary>La vista de escritura del mip 0 como <c>RWTexture2DArray</c>.</summary>
    public VkImageView StorageArray()
    {
        if (_storageArray == default)
        {
            _storageArray = CreateView(VkImageViewType.VK_IMAGE_VIEW_TYPE_2D_ARRAY, 0, 1);
        }

        return _storageArray;
    }

    public VkImageSubresourceRange Range(int firstMip = 0, int mips = -1) => new()
    {
        aspectMask = Aspect,
        baseMipLevel = (uint)firstMip,
        levelCount = mips < 0 ? (uint)Mips : (uint)mips,
        layerCount = 1,
    };

    public (int Width, int Height, int Depth) MipSize(int mip) =>
        (Math.Max(1, Width >> mip), Math.Max(1, Height >> mip), ThreeD ? Math.Max(1, Math.Max(1, Desc.Depth) >> mip) : 1);

    public void DestroyNow()
    {
        VkDevice device = _gpu.Device;
        foreach (VkImageView view in new[] { Sampled2D, SampledArray, Sampled3D, Attachment, Storage2D, _storageArray })
        {
            if (view != default)
            {
                vkDestroyImageView(device, view, null);
            }
        }

        if (Image != default) vkDestroyImage(device, Image, null);
        if (Memory != default) vkFreeMemory(device, Memory, null);
        Image = default;
        Memory = default;
    }

    protected override void Release() => _gpu.Defer(DestroyNow);

    private VkImageView CreateView(VkImageViewType type, int firstMip, int mips)
    {
        var info = new VkImageViewCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            image = Image,
            viewType = type,
            format = NativeFormat,
            subresourceRange = Range(firstMip, mips),
        };
        VkImageView view;
        _gpu.Check(vkCreateImageView(_gpu.Device, &info, null, &view), "vkCreateImageView");
        return view;
    }

    private void UploadInitial(ReadOnlySpan<byte> data)
    {
        VkImageLayout final = (Desc.Usage & GpuTextureUsage.Sampled) != 0
            ? VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL
            : VkImageLayout.VK_IMAGE_LAYOUT_GENERAL;
        VkImage image = Image;
        VkImageSubresourceRange range = Range();
        uint width = (uint)Width, height = (uint)Height, depth = (uint)(ThreeD ? Math.Max(1, Desc.Depth) : 1);
        VkImageAspectFlags aspect = Aspect;
        _gpu.UploadImmediate(data, (commands, buffer, offset) =>
        {
            VulkanBarriers.Image(commands, image, range, VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
            var region = new VkBufferImageCopy
            {
                bufferOffset = offset,
                imageSubresource = new VkImageSubresourceLayers { aspectMask = aspect, layerCount = 1 },
                imageExtent = new VkExtent3D { width = width, height = height, depth = depth },
            };
            vkCmdCopyBufferToImage(commands, buffer, image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
            VulkanBarriers.Image(commands, image, range, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, final);
        });
        Layout = final;
    }
}

/// <summary>
/// Un búfer. Los de constantes viven en la CPU y se suben a la memoria del envío al enlazarse (como <c>WRITE_DISCARD</c> de D3D11:
/// cada escritura vale para lo que se dibuje después).
/// </summary>
internal sealed unsafe class VulkanBuffer : GpuBuffer
{
    private readonly VulkanGpu _gpu;

    private VulkanBuffer(VulkanGpu gpu, GpuBufferDesc desc) : base(desc)
    {
        _gpu = gpu;
    }

    public VkBuffer Handle { get; private set; }

    public VkDeviceMemory Memory { get; private set; }

    public byte[]? Shadow { get; private set; }

    /// <summary>Copia subida de <see cref="Shadow"/>: envío, búfer y desplazamiento.</summary>
    public (long Serial, VkBuffer Buffer, ulong Offset) Uploaded { get; set; } = (-1, default, 0);

    public static VulkanBuffer Create(VulkanGpu gpu, GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desc.Size);
        if (initialData.Length > desc.Size)
        {
            throw new ArgumentException("Los datos no caben en el búfer.", nameof(initialData));
        }

        var buffer = new VulkanBuffer(gpu, desc);
        if (desc.Kind == GpuBufferKind.Constant)
        {
            buffer.Shadow = new byte[desc.Size];
            initialData.CopyTo(buffer.Shadow);
            return buffer;
        }

        const VkBufferUsageFlags usage = VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT
            | VkBufferUsageFlags.VK_BUFFER_USAGE_STORAGE_BUFFER_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT
            | VkBufferUsageFlags.VK_BUFFER_USAGE_VERTEX_BUFFER_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_INDEX_BUFFER_BIT;
        var created = gpu.CreateRawBuffer((ulong)desc.Size, usage, hostVisible: false);
        buffer.Handle = created.Buffer;
        buffer.Memory = created.Memory;
        VkBuffer handle = created.Buffer;
        ulong size = (ulong)desc.Size;
        // Sin contenido inicial, a cero (D3D11 crea los búferes a cero en la práctica y los shaders de cómputo acumulan sobre ellos).
        if (initialData.IsEmpty)
        {
            gpu.Immediate(commands =>
            {
                vkCmdFillBuffer(commands, handle, 0, VK_WHOLE_SIZE, 0);
            });
        }
        else
        {
            int length = initialData.Length;
            gpu.UploadImmediate(initialData, (commands, source, offset) =>
            {
                var region = new VkBufferCopy { srcOffset = offset, size = (ulong)length };
                if (length < (int)size)
                {
                    vkCmdFillBuffer(commands, handle, 0, VK_WHOLE_SIZE, 0);
                    VulkanBarriers.Memory(commands);
                }

                vkCmdCopyBuffer(commands, source, handle, 1, &region);
            });
        }

        return buffer;
    }

    public void DestroyNow()
    {
        if (Handle != default) vkDestroyBuffer(_gpu.Device, Handle, null);
        if (Memory != default) vkFreeMemory(_gpu.Device, Memory, null);
        Handle = default;
        Memory = default;
    }

    protected override void Release() => _gpu.Defer(DestroyNow);
}

internal sealed unsafe class VulkanSampler : GpuSampler
{
    private readonly VulkanGpu _gpu;

    public VulkanSampler(VulkanGpu gpu, GpuSamplerDesc desc)
    {
        _gpu = gpu;
        VkFilter filter = desc.Filter == GpuFilter.Point ? VkFilter.VK_FILTER_NEAREST : VkFilter.VK_FILTER_LINEAR;
        VkSamplerAddressMode address = desc.Address switch
        {
            GpuAddressMode.Wrap => VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_REPEAT,
            GpuAddressMode.Mirror => VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT,
            GpuAddressMode.Border => VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER,
            _ => VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
        };
        var info = new VkSamplerCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
            magFilter = filter,
            minFilter = filter,
            mipmapMode = desc.Filter == GpuFilter.Point ? VkSamplerMipmapMode.VK_SAMPLER_MIPMAP_MODE_NEAREST : VkSamplerMipmapMode.VK_SAMPLER_MIPMAP_MODE_LINEAR,
            addressModeU = address,
            addressModeV = address,
            addressModeW = address,
            maxLod = VK_LOD_CLAMP_NONE,
            borderColor = VkBorderColor.VK_BORDER_COLOR_FLOAT_TRANSPARENT_BLACK,
        };
        VkSampler sampler;
        gpu.Check(vkCreateSampler(gpu.Device, &info, null, &sampler), "vkCreateSampler");
        Handle = sampler;
    }

    public VkSampler Handle { get; private set; }

    public void DestroyNow()
    {
        if (Handle != default) vkDestroySampler(_gpu.Device, Handle, null);
        Handle = default;
    }

    protected override void Release() => _gpu.Defer(DestroyNow);
}

internal sealed unsafe class VulkanShader : GpuShader
{
    private readonly VulkanGpu _gpu;

    public VulkanShader(VulkanGpu gpu, SpirvModule module)
    {
        _gpu = gpu;
        Module = module;
        fixed (byte* code = module.Code)
        {
            var info = new VkShaderModuleCreateInfo { sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, codeSize = (nuint)module.Code.Length, pCode = (uint*)code };
            VkShaderModule handle;
            gpu.Check(vkCreateShaderModule(gpu.Device, &info, null, &handle), "vkCreateShaderModule");
            Handle = handle;
        }
    }

    public SpirvModule Module { get; }

    public VkShaderModule Handle { get; private set; }

    public VkShaderStageFlags Stage => Module.ExecutionModel switch
    {
        0 => VkShaderStageFlags.VK_SHADER_STAGE_VERTEX_BIT,
        4 => VkShaderStageFlags.VK_SHADER_STAGE_FRAGMENT_BIT,
        5 => VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT,
        _ => throw new RenderException($"Etapa de sombreador {Module.ExecutionModel} sin soporte."),
    };

    protected override void Release()
    {
        VkShaderModule handle = Handle;
        VkDevice device = _gpu.Device;
        _gpu.Defer(() => vkDestroyShaderModule(device, handle, null));
    }
}

internal sealed unsafe class VulkanTimerQuery : GpuTimerQuery
{
    private readonly VulkanGpu _gpu;

    public VulkanTimerQuery(VulkanGpu gpu)
    {
        _gpu = gpu;
        var info = new VkQueryPoolCreateInfo { sType = VK_STRUCTURE_TYPE_QUERY_POOL_CREATE_INFO, queryType = VkQueryType.VK_QUERY_TYPE_TIMESTAMP, queryCount = 2 };
        VkQueryPool pool;
        gpu.Check(vkCreateQueryPool(gpu.Device, &info, null, &pool), "vkCreateQueryPool");
        Pool = pool;
    }

    public VkQueryPool Pool { get; }

    /// <summary>Envío que grabó el final; -1 si no se grabó.</summary>
    public long Serial { get; set; } = -1;

    public override bool TryGetMilliseconds(out double? milliseconds)
    {
        milliseconds = null;
        if (Serial < 0 || !_gpu.IsComplete(Serial))
        {
            return Serial < 0;
        }

        ulong* stamps = stackalloc ulong[2];
        VkResult result = vkGetQueryPoolResults(_gpu.Device, Pool, 0, 2, 16, stamps, 8, VkQueryResultFlags.VK_QUERY_RESULT_64_BIT);
        if (result == VkResult.VK_NOT_READY)
        {
            return false;
        }

        _gpu.Check(result, "vkGetQueryPoolResults");
        if (_gpu.Limits.timestampComputeAndGraphics != 0 && stamps[1] >= stamps[0])
        {
            milliseconds = (stamps[1] - stamps[0]) * (double)_gpu.Limits.timestampPeriod / 1e6;
        }

        return true;
    }

    protected override void Release()
    {
        VkQueryPool pool = Pool;
        VkDevice device = _gpu.Device;
        _gpu.Defer(() => vkDestroyQueryPool(device, pool, null));
    }
}

/// <summary>Un búfer visible desde la CPU, mapeado siempre; la valla del cuadro que grabó la copia dice cuándo vale.</summary>
internal sealed unsafe class VulkanReadback : GpuReadback
{
    private readonly VulkanGpu _gpu;
    private readonly int _rowPitch;

    public VulkanReadback(VulkanGpu gpu, int width, int height, GpuFormat format, int totalBytes)
        : base(width, height, format, format == GpuFormat.Unknown ? totalBytes : width * GpuFormats.BytesPerPixel(format))
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalBytes);
        _gpu = gpu;
        _rowPitch = Bytes;
        var created = gpu.CreateRawBuffer((ulong)totalBytes, VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT, hostVisible: true, cached: true);
        Handle = created.Buffer;
        Memory = created.Memory;
        Data = (byte*)created.Mapped;
    }

    public VkBuffer Handle { get; }

    public VkDeviceMemory Memory { get; }

    public byte* Data { get; }

    public long Serial { get; set; } = -1;

    public override bool TryMap(out GpuMappedRows rows)
    {
        if (!_gpu.IsComplete(Serial))
        {
            rows = default;
            return false;
        }

        rows = new GpuMappedRows(this, Data, _rowPitch);
        return true;
    }

    public override GpuMappedRows Map()
    {
        if (!_gpu.Wait(Serial, TimeSpan.FromMinutes(1)))
        {
            throw new RenderException("La lectura de la GPU no terminó en un minuto.");
        }

        return new GpuMappedRows(this, Data, _rowPitch);
    }

    protected internal override void Unmap()
    {
    }

    protected override void Release()
    {
        VkBuffer handle = Handle;
        VkDeviceMemory memory = Memory;
        VkDevice device = _gpu.Device;
        _gpu.Defer(() =>
        {
            vkDestroyBuffer(device, handle, null);
            vkFreeMemory(device, memory, null);
        });
    }
}

internal sealed class VulkanFence(VulkanGpu gpu, long serial) : GpuFence
{
    public override bool IsComplete => gpu.IsComplete(serial);

    public override bool Wait(TimeSpan timeout) => gpu.Wait(serial, timeout);
}

/// <summary>Hasta la fase 9 no hay superficies Vulkan del decodificador que guardar.</summary>
internal sealed class VulkanSurfaceCache : GpuSurfaceCache
{
    public override int Count => 0;

    public override void EndFrame()
    {
    }

    public override void Trim()
    {
    }

    public override void Clear()
    {
    }

    protected override void Release()
    {
    }
}

/// <summary>Barreras sencillas: todo lo anterior antes de todo lo siguiente. Correctas siempre; afinar las etapas queda para después.</summary>
internal static unsafe class VulkanBarriers
{
    private const VkAccessFlags All = VkAccessFlags.VK_ACCESS_MEMORY_READ_BIT | VkAccessFlags.VK_ACCESS_MEMORY_WRITE_BIT;

    public static void Image(VkCommandBuffer commands, VkImage image, in VkImageSubresourceRange range, VkImageLayout from, VkImageLayout to)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = All,
            dstAccessMask = All,
            oldLayout = from,
            newLayout = to,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = range,
        };
        vkCmdPipelineBarrier(commands, VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
            0, 0, null, 0, null, 1, &barrier);
    }

    public static void Memory(VkCommandBuffer commands)
    {
        var barrier = new VkMemoryBarrier { sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER, srcAccessMask = All, dstAccessMask = All | VkAccessFlags.VK_ACCESS_HOST_READ_BIT };
        vkCmdPipelineBarrier(commands, VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT | VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &barrier, 0, null, 0, null);
    }
}
