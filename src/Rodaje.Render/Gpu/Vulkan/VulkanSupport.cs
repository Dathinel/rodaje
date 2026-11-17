using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>
/// Lo de un envío: búfer de órdenes, valla, descriptores y memoria de subida (constantes y copias desde la CPU). Se reutiliza cuando su
/// valla termina.
/// </summary>
internal sealed unsafe class FrameContext
{
    private const ulong ChunkSize = 4 << 20;
    private const uint SetsPerPool = 256;
    private readonly VkCommandPool _pool;
    private readonly List<VkDescriptorPool> _descriptorPools = [];
    private readonly List<(VkBuffer Buffer, VkDeviceMemory Memory, nint Mapped, ulong Size)> _chunks = [];
    private int _descriptorPool;
    private int _chunk;
    private ulong _offset;

    public FrameContext(VulkanGpu gpu)
    {
        VkDevice device = gpu.Device;
        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_TRANSIENT_BIT,
            queueFamilyIndex = gpu.QueueFamily,
        };
        VkCommandPool pool;
        gpu.Check(vkCreateCommandPool(device, &poolInfo, null, &pool), "vkCreateCommandPool");
        _pool = pool;
        var allocate = new VkCommandBufferAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            commandPool = pool,
            level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
            commandBufferCount = 1,
        };
        VkCommandBuffer commands;
        gpu.Check(vkAllocateCommandBuffers(device, &allocate, &commands), "vkAllocateCommandBuffers");
        Commands = commands;
        var fenceInfo = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        VkFence fence;
        gpu.Check(vkCreateFence(device, &fenceInfo, null, &fence), "vkCreateFence");
        Fence = fence;
    }

    public VkCommandBuffer Commands { get; }

    public VkFence Fence { get; }

    public long Serial { get; private set; }

    public void Begin(VulkanGpu gpu, long serial)
    {
        Serial = serial;
        gpu.Check(vkResetCommandPool(gpu.Device, _pool, 0), "vkResetCommandPool");
        foreach (VkDescriptorPool pool in _descriptorPools)
        {
            gpu.Check(vkResetDescriptorPool(gpu.Device, pool, 0), "vkResetDescriptorPool");
        }

        _descriptorPool = 0;
        _chunk = 0;
        _offset = 0;
        var begin = new VkCommandBufferBeginInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        };
        gpu.Check(vkBeginCommandBuffer(Commands, &begin), "vkBeginCommandBuffer");
    }

    /// <summary>Memoria de subida que vale hasta que este envío termina.</summary>
    public (VkBuffer Buffer, ulong Offset, nint Data) Upload(VulkanGpu gpu, ulong size, ulong alignment)
    {
        while (true)
        {
            if (_chunk < _chunks.Count)
            {
                var chunk = _chunks[_chunk];
                ulong start = (_offset + alignment - 1) / alignment * alignment;
                if (start + size <= chunk.Size)
                {
                    _offset = start + size;
                    return (chunk.Buffer, start, chunk.Mapped + (nint)start);
                }

                _chunk++;
                _offset = 0;
                continue;
            }

            ulong bytes = Math.Max(ChunkSize, size);
            var created = gpu.CreateRawBuffer(bytes, VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VkBufferUsageFlags.VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT,
                hostVisible: true);
            _chunks.Add((created.Buffer, created.Memory, created.Mapped, bytes));
        }
    }

    public VkDescriptorSet AllocateSet(VulkanGpu gpu, VkDescriptorSetLayout layout)
    {
        while (true)
        {
            if (_descriptorPool == _descriptorPools.Count)
            {
                _descriptorPools.Add(CreateDescriptorPool(gpu));
            }

            var info = new VkDescriptorSetAllocateInfo
            {
                sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
                descriptorPool = _descriptorPools[_descriptorPool],
                descriptorSetCount = 1,
                pSetLayouts = &layout,
            };
            VkDescriptorSet set;
            VkResult result = vkAllocateDescriptorSets(gpu.Device, &info, &set);
            if (result == VkResult.VK_SUCCESS)
            {
                return set;
            }

            if (result is not (VkResult.VK_ERROR_OUT_OF_POOL_MEMORY or VkResult.VK_ERROR_FRAGMENTED_POOL))
            {
                gpu.Check(result, "vkAllocateDescriptorSets");
            }

            _descriptorPool++;
        }
    }

    public void Destroy(VulkanGpu gpu)
    {
        VkDevice device = gpu.Device;
        foreach (VkDescriptorPool pool in _descriptorPools)
        {
            vkDestroyDescriptorPool(device, pool, null);
        }

        foreach (var chunk in _chunks)
        {
            vkDestroyBuffer(device, chunk.Buffer, null);
            vkFreeMemory(device, chunk.Memory, null);
        }

        vkDestroyFence(device, Fence, null);
        vkDestroyCommandPool(device, _pool, null);
    }

    private static VkDescriptorPool CreateDescriptorPool(VulkanGpu gpu)
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, descriptorCount = SetsPerPool * GpuBindings.MaxConstants },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, descriptorCount = SetsPerPool * GpuBindings.MaxResources },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER, descriptorCount = SetsPerPool * GpuBindings.MaxSamplers },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, descriptorCount = SetsPerPool * GpuBindings.MaxStorage },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, descriptorCount = SetsPerPool * (GpuBindings.MaxStorage + GpuBindings.MaxResources) },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER, descriptorCount = SetsPerPool },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER, descriptorCount = SetsPerPool },
        };
        var info = new VkDescriptorPoolCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
            maxSets = SetsPerPool,
            poolSizeCount = 7,
            pPoolSizes = sizes,
        };
        VkDescriptorPool pool;
        gpu.Check(vkCreateDescriptorPool(gpu.Device, &info, null, &pool), "vkCreateDescriptorPool");
        return pool;
    }
}

/// <summary>Subidas fuera de un cuadro (contenido inicial de texturas y búferes): un envío propio que se espera.</summary>
internal sealed unsafe class ImmediateUploader(VulkanGpu gpu)
{
    private readonly object _lock = new();
    private FrameContext? _context;

    public void Run(Action<VkCommandBuffer> record)
    {
        lock (_lock)
        {
            _context ??= new FrameContext(gpu);
            _context.Begin(gpu, 0);
            record(_context.Commands);
            gpu.Check(vkEndCommandBuffer(_context.Commands), "vkEndCommandBuffer");
            VkFence fence = _context.Fence;
            gpu.Check(vkResetFences(gpu.Device, 1, &fence), "vkResetFences");
            VkCommandBuffer commands = _context.Commands;
            var submit = new VkSubmitInfo { sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, commandBufferCount = 1, pCommandBuffers = &commands };
            lock (gpu.QueueLock)
            {
                gpu.Check(vkQueueSubmit(gpu.Queue, 1, &submit, fence), "vkQueueSubmit");
            }

            gpu.Check(vkWaitForFences(gpu.Device, 1, &fence, VK_TRUE, ulong.MaxValue), "vkWaitForFences");
        }
    }

    /// <summary>Sube <paramref name="data"/> y graba su copia con <paramref name="record"/> (búfer de origen y desplazamiento).</summary>
    public void Upload(ReadOnlySpan<byte> data, Action<VkCommandBuffer, VkBuffer, ulong> record)
    {
        lock (_lock)
        {
            _context ??= new FrameContext(gpu);
            byte[] copy = data.ToArray();
            Run(commands =>
            {
                var (buffer, offset, target) = _context.Upload(gpu, (ulong)copy.Length, 16);
                copy.CopyTo(new Span<byte>((byte*)target, copy.Length));
                record(commands, buffer, offset);
            });
        }
    }

    public void Destroy() => _context?.Destroy(gpu);
}

/// <summary>Lo que se enlaza en un registro que la pasada deja vacío: en D3D11 se lee cero; aquí, recursos negros de 1×1.</summary>
internal sealed unsafe class DefaultResources
{
    private readonly VulkanGpu _gpu;

    public DefaultResources(VulkanGpu gpu)
    {
        _gpu = gpu;
        var usage = GpuTextureUsage.Sampled | GpuTextureUsage.Storage;
        Texture2D = VulkanTexture.Create(gpu, new GpuTextureDesc(1, 1, GpuFormat.Rgba16Float, usage), new byte[8]);
        Texture3D = VulkanTexture.Create(gpu, new GpuTextureDesc(1, 1, GpuFormat.Rgba16Float, GpuTextureUsage.Sampled, Depth: 1), new byte[8], forceThreeD: true);
        Buffer = VulkanBuffer.Create(gpu, new GpuBufferDesc(65536, GpuBufferKind.Raw, Storage: true), new byte[65536]);
        Sampler = new VulkanSampler(gpu, default);
    }

    public VulkanTexture Texture2D { get; }

    public VulkanTexture Texture3D { get; }

    public VulkanBuffer Buffer { get; }

    public VulkanSampler Sampler { get; }

    public void Destroy()
    {
        Texture2D.DestroyNow();
        Texture3D.DestroyNow();
        Buffer.DestroyNow();
        Sampler.DestroyNow();
    }
}

/// <summary>Formatos de <see cref="GpuFormat"/> en Vulkan.</summary>
internal static class VulkanFormats
{
    public static VkFormat Native(GpuFormat format) => format switch
    {
        GpuFormat.R8Unorm => VkFormat.VK_FORMAT_R8_UNORM,
        GpuFormat.Rg8Unorm => VkFormat.VK_FORMAT_R8G8_UNORM,
        GpuFormat.R16Unorm => VkFormat.VK_FORMAT_R16_UNORM,
        GpuFormat.Rg16Unorm => VkFormat.VK_FORMAT_R16G16_UNORM,
        GpuFormat.R16Uint => VkFormat.VK_FORMAT_R16_UINT,
        GpuFormat.R32Uint => VkFormat.VK_FORMAT_R32_UINT,
        GpuFormat.R32Float => VkFormat.VK_FORMAT_R32_SFLOAT,
        GpuFormat.Rg32Float => VkFormat.VK_FORMAT_R32G32_SFLOAT,
        GpuFormat.Rgba8Unorm => VkFormat.VK_FORMAT_R8G8B8A8_UNORM,
        GpuFormat.Bgra8Unorm => VkFormat.VK_FORMAT_B8G8R8A8_UNORM,
        // R10G10B10A2 de DXGI pone R en los bits bajos, como A2B10G10R10 de Vulkan.
        GpuFormat.Rgb10A2Unorm => VkFormat.VK_FORMAT_A2B10G10R10_UNORM_PACK32,
        GpuFormat.Rgba16Unorm => VkFormat.VK_FORMAT_R16G16B16A16_UNORM,
        GpuFormat.Rgba16Float => VkFormat.VK_FORMAT_R16G16B16A16_SFLOAT,
        GpuFormat.Rgba32Float => VkFormat.VK_FORMAT_R32G32B32A32_SFLOAT,
        GpuFormat.D32Float => VkFormat.VK_FORMAT_D32_SFLOAT,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Formato sin equivalente en Vulkan."),
    };

    public static bool IsInteger(GpuFormat format) => format is GpuFormat.R16Uint or GpuFormat.R32Uint;
}
