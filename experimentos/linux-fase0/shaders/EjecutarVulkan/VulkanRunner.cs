using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Fase0.Shaders;

internal sealed class VulkanException(string message) : Exception(message);

/// <summary>
/// Vulkan sin ventana: un objetivo RGBA16F, el vertex shader de Rodaje y un pipeline por variante armado con lo que dice el
/// manifiesto (bindings y tipos de descriptor). Cada caso se dibuja, se espera y se lee.
/// </summary>
internal sealed unsafe class VulkanRunner : IDisposable
{
    private const VkFormat TargetFormat = VkFormat.VK_FORMAT_R16G16B16A16_SFLOAT;
    private readonly int _width;
    private readonly int _height;
    private readonly VkInstance _instance;
    private readonly VkPhysicalDevice _physical;
    private readonly VkDevice _device;
    private readonly VkQueue _queue;
    private readonly VkCommandPool _pool;
    private readonly VkCommandBuffer _cmd;
    private readonly VkFence _fence;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private readonly VkSampler _sampler;
    private readonly VkDescriptorPool _descriptors;
    private readonly VkRenderPass _renderPass;
    private readonly Image _target;
    private readonly VkFramebuffer _framebuffer;
    private readonly Buffer _readback;
    private readonly Buffer _quad;
    private readonly Buffer _effects;
    private readonly Dictionary<string, (Image Image, TextureSpec Spec)> _textures = [];
    private readonly Dictionary<int, Pipeline> _pipelines = [];
    private VkShaderModule _vertex;
    private ResourceBinding[] _vertexResources = [];

    private readonly record struct Image(VkImage Handle, VkDeviceMemory Memory, VkImageView View);

    private readonly record struct Buffer(VkBuffer Handle, VkDeviceMemory Memory, nint Mapped);

    private readonly record struct Pipeline(VkShaderModule Fragment, VkDescriptorSetLayout SetLayout, VkPipelineLayout Layout, VkPipeline Handle, ResourceBinding[] Resources);

    public string DeviceName { get; }

    public string DriverInfo { get; }

    public VulkanRunner(int width, int height, string? gpuFilter)
    {
        _width = width;
        _height = height;
        fixed (byte* name = "rodaje-fase0-shaders\0"u8)
        {
            var app = new VkApplicationInfo
            {
                sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pApplicationName = (sbyte*)name,
                pEngineName = (sbyte*)name,
                apiVersion = VK_API_VERSION_1_1,
            };
            var info = new VkInstanceCreateInfo { sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, pApplicationInfo = &app };
            VkInstance instance;
            Check(vkCreateInstance(&info, null, &instance), "vkCreateInstance");
            _instance = instance;
        }

        uint count = 0;
        Check(vkEnumeratePhysicalDevices(_instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = devices)
        {
            Check(vkEnumeratePhysicalDevices(_instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        var candidates = devices.Select(d =>
        {
            VkPhysicalDeviceProperties props;
            vkGetPhysicalDeviceProperties(d, &props);
            return (Device: d, Props: props, Name: NameOf(props));
        }).ToList();
        foreach (var c in candidates)
        {
            Console.WriteLine($"GPU vista: {c.Name} ({c.Props.deviceType})");
        }

        var chosen = gpuFilter is { Length: > 0 }
            ? candidates.FirstOrDefault(c => c.Name.Contains(gpuFilter, StringComparison.OrdinalIgnoreCase))
            : candidates.OrderBy(c => c.Props.deviceType switch
            {
                VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU => 0,
                VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU => 1,
                _ => 2,
            }).FirstOrDefault();
        if (chosen.Name is null)
        {
            throw new VulkanException(gpuFilter is { Length: > 0 } ? $"Ninguna GPU contiene «{gpuFilter}» (RODAJE_VK_GPU)." : "No hay ninguna GPU con Vulkan.");
        }

        _physical = chosen.Device;
        DeviceName = chosen.Name;
        uint v = chosen.Props.apiVersion;
        DriverInfo = $"Vulkan {v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}, controlador 0x{chosen.Props.driverVersion:X8}, {chosen.Props.deviceType}";
        VkPhysicalDeviceMemoryProperties memory;
        vkGetPhysicalDeviceMemoryProperties(_physical, &memory);
        _memory = memory;

        uint families = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(_physical, &families, null);
        var familyProps = new VkQueueFamilyProperties[families];
        fixed (VkQueueFamilyProperties* p = familyProps)
        {
            vkGetPhysicalDeviceQueueFamilyProperties(_physical, &families, p);
        }

        int family = Array.FindIndex(familyProps, f => (f.queueFlags & VkQueueFlags.VK_QUEUE_GRAPHICS_BIT) != 0);
        if (family < 0)
        {
            throw new VulkanException("La GPU no tiene una cola de gráficos.");
        }

        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
            queueFamilyIndex = (uint)family,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        var deviceInfo = new VkDeviceCreateInfo { sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, queueCreateInfoCount = 1, pQueueCreateInfos = &queueInfo };
        VkDevice device;
        Check(vkCreateDevice(_physical, &deviceInfo, null, &device), "vkCreateDevice");
        _device = device;
        VkQueue queue;
        vkGetDeviceQueue(_device, (uint)family, 0, &queue);
        _queue = queue;

        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
            queueFamilyIndex = (uint)family,
        };
        VkCommandPool pool;
        Check(vkCreateCommandPool(_device, &poolInfo, null, &pool), "vkCreateCommandPool");
        _pool = pool;
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            commandPool = _pool,
            level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
            commandBufferCount = 1,
        };
        VkCommandBuffer cmd;
        Check(vkAllocateCommandBuffers(_device, &allocInfo, &cmd), "vkAllocateCommandBuffers");
        _cmd = cmd;
        var fenceInfo = new VkFenceCreateInfo { sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        VkFence fence;
        Check(vkCreateFence(_device, &fenceInfo, null, &fence), "vkCreateFence");
        _fence = fence;

        // Como el D3D11_SAMPLER_DESC de QuadPipeline: lineal en todo, borde fijado, sin tope de LOD.
        var samplerInfo = new VkSamplerCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
            magFilter = VkFilter.VK_FILTER_LINEAR,
            minFilter = VkFilter.VK_FILTER_LINEAR,
            mipmapMode = VkSamplerMipmapMode.VK_SAMPLER_MIPMAP_MODE_LINEAR,
            addressModeU = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            addressModeV = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            addressModeW = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            maxLod = VK_LOD_CLAMP_NONE,
        };
        VkSampler sampler;
        Check(vkCreateSampler(_device, &samplerInfo, null, &sampler), "vkCreateSampler");
        _sampler = sampler;

        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3]
        {
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, descriptorCount = 16 },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, descriptorCount = 32 },
            new() { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER, descriptorCount = 16 },
        };
        var descriptorPoolInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
            maxSets = 1,
            poolSizeCount = 3,
            pPoolSizes = sizes,
        };
        VkDescriptorPool descriptors;
        Check(vkCreateDescriptorPool(_device, &descriptorPoolInfo, null, &descriptors), "vkCreateDescriptorPool");
        _descriptors = descriptors;

        _target = CreateImage(VkImageType.VK_IMAGE_TYPE_2D, VkImageViewType.VK_IMAGE_VIEW_TYPE_2D, TargetFormat, (uint)width, (uint)height, 1,
            VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_SRC_BIT);
        _renderPass = CreateRenderPass();
        VkImageView view = _target.View;
        var framebufferInfo = new VkFramebufferCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO,
            renderPass = _renderPass,
            attachmentCount = 1,
            pAttachments = &view,
            width = (uint)width,
            height = (uint)height,
            layers = 1,
        };
        VkFramebuffer framebuffer;
        Check(vkCreateFramebuffer(_device, &framebufferInfo, null, &framebuffer), "vkCreateFramebuffer");
        _framebuffer = framebuffer;
        _readback = CreateBuffer((ulong)(width * height * 8), VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT);
        _quad = CreateBuffer(17 * 16, VkBufferUsageFlags.VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT);
        _effects = CreateBuffer(26 * 16, VkBufferUsageFlags.VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT);
    }

    private static string NameOf(VkPhysicalDeviceProperties props)
    {
        ReadOnlySpan<sbyte> chars = props.deviceName;
        ReadOnlySpan<byte> bytes = MemoryMarshal.Cast<sbyte, byte>(chars);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    public void LoadVertex(byte[] spirv, ResourceBinding[] resources)
    {
        _vertex = CreateShaderModule(spirv);
        _vertexResources = resources;
    }

    public void AddTexture(TextureSpec t)
    {
        byte[] bytes = Patterns.Bytes(t);
        if (Patterns.Hash(bytes) != t.Hash)
        {
            throw new InvalidDataException($"La textura {t.Name} no sale idéntica a la de la referencia (hash {Patterns.Hash(bytes)} frente a {t.Hash}).");
        }

        VkFormat format = t.Format switch
        {
            "rgba16f" => VkFormat.VK_FORMAT_R16G16B16A16_SFLOAT,
            "rg16f" => VkFormat.VK_FORMAT_R16G16_SFLOAT,
            "r16f" => VkFormat.VK_FORMAT_R16_SFLOAT,
            "r32f" => VkFormat.VK_FORMAT_R32_SFLOAT,
            "rgba32f" => VkFormat.VK_FORMAT_R32G32B32A32_SFLOAT,
            _ => throw new InvalidDataException(t.Format),
        };
        (VkImageType type, VkImageViewType viewType) = t.Dimension switch
        {
            "3d" => (VkImageType.VK_IMAGE_TYPE_3D, VkImageViewType.VK_IMAGE_VIEW_TYPE_3D),
            "2d-array" => (VkImageType.VK_IMAGE_TYPE_2D, VkImageViewType.VK_IMAGE_VIEW_TYPE_2D_ARRAY),
            _ => (VkImageType.VK_IMAGE_TYPE_2D, VkImageViewType.VK_IMAGE_VIEW_TYPE_2D),
        };
        Image image = CreateImage(type, viewType, format, (uint)t.Width, (uint)t.Height, (uint)t.Depth,
            VkImageUsageFlags.VK_IMAGE_USAGE_SAMPLED_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT);
        Buffer staging = CreateBuffer((ulong)bytes.Length, VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_SRC_BIT);
        Marshal.Copy(bytes, 0, staging.Mapped, bytes.Length);
        Begin();
        Barrier(image.Handle, VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0,
            VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT, VkPipelineStageFlags.VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT);
        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 },
            imageExtent = new VkExtent3D { width = (uint)t.Width, height = (uint)t.Height, depth = (uint)t.Depth },
        };
        vkCmdCopyBufferToImage(_cmd, staging.Handle, image.Handle, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
        Barrier(image.Handle, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT, VkAccessFlags.VK_ACCESS_SHADER_READ_BIT, VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
        Submit();
        Destroy(staging);
        _textures[t.Name] = (image, t);
    }

    public float[] Draw(CaseSpec c, ShaderEntry variant, byte[] fragmentSpirv)
    {
        if (!_pipelines.TryGetValue(variant.Index, out Pipeline pipeline))
        {
            pipeline = CreatePipeline(variant, fragmentSpirv);
            _pipelines[variant.Index] = pipeline;
        }

        Marshal.Copy(c.Quad, 0, _quad.Mapped, c.Quad.Length);
        Marshal.Copy(c.Effects, 0, _effects.Mapped, c.Effects.Length);

        Check(vkResetDescriptorPool(_device, _descriptors, 0), "vkResetDescriptorPool");
        VkDescriptorSetLayout setLayout = pipeline.SetLayout;
        var setInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
            descriptorPool = _descriptors,
            descriptorSetCount = 1,
            pSetLayouts = &setLayout,
        };
        VkDescriptorSet set;
        Check(vkAllocateDescriptorSets(_device, &setInfo, &set), "vkAllocateDescriptorSets");

        int n = pipeline.Resources.Length;
        var writes = new VkWriteDescriptorSet[n];
        var buffers = new VkDescriptorBufferInfo[n];
        var images = new VkDescriptorImageInfo[n];
        fixed (VkWriteDescriptorSet* w = writes)
        fixed (VkDescriptorBufferInfo* b = buffers)
        fixed (VkDescriptorImageInfo* im = images)
        {
            for (int i = 0; i < n; i++)
            {
                ResourceBinding r = pipeline.Resources[i];
                w[i] = new VkWriteDescriptorSet
                {
                    sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
                    dstSet = set,
                    dstBinding = (uint)r.Binding,
                    descriptorCount = 1,
                    descriptorType = DescriptorType(r),
                };
                switch (r.Kind)
                {
                    case "uniform-buffer":
                        Buffer buffer = r.Register switch
                        {
                            "b0" => _quad,
                            "b1" => _effects,
                            _ => throw new InvalidDataException($"cbuffer inesperado: {r.Register}"),
                        };
                        b[i] = new VkDescriptorBufferInfo { buffer = buffer.Handle, range = (ulong)(r.Size ?? 0) };
                        w[i].pBufferInfo = &b[i];
                        break;
                    case "sampled-image":
                        if (!c.Textures.TryGetValue(r.Register, out string? name) || !_textures.TryGetValue(name, out var tex))
                        {
                            throw new InvalidDataException($"{c.Name}: el shader usa {r.Register} ({r.Name}) y el caso no le da textura.");
                        }

                        if (tex.Spec.Dimension != r.Dimension)
                        {
                            throw new InvalidDataException($"{c.Name}: {r.Register} es {r.Dimension} en el SPIR-V y la textura {name} es {tex.Spec.Dimension}.");
                        }

                        im[i] = new VkDescriptorImageInfo { imageView = tex.Image.View, imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
                        w[i].pImageInfo = &im[i];
                        break;
                    case "sampler":
                        im[i] = new VkDescriptorImageInfo { sampler = _sampler };
                        w[i].pImageInfo = &im[i];
                        break;
                }
            }

            vkUpdateDescriptorSets(_device, (uint)n, w, 0, null);
        }

        Begin();
        VkClearValue clear = default;
        var passInfo = new VkRenderPassBeginInfo
        {
            sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO,
            renderPass = _renderPass,
            framebuffer = _framebuffer,
            renderArea = new VkRect2D { extent = new VkExtent2D { width = (uint)_width, height = (uint)_height } },
            clearValueCount = 1,
            pClearValues = &clear,
        };
        vkCmdBeginRenderPass(_cmd, &passInfo, VkSubpassContents.VK_SUBPASS_CONTENTS_INLINE);
        vkCmdBindPipeline(_cmd, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline.Handle);
        vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS, pipeline.Layout, 0, 1, &set, 0, null);
        vkCmdDraw(_cmd, 4, 1, 0, 0);
        vkCmdEndRenderPass(_cmd);
        var region = new VkBufferImageCopy
        {
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 },
            imageExtent = new VkExtent3D { width = (uint)_width, height = (uint)_height, depth = 1 },
        };
        vkCmdCopyImageToBuffer(_cmd, _target.Handle, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, _readback.Handle, 1, &region);
        var hostBarrier = new VkMemoryBarrier
        {
            sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER,
            srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
            dstAccessMask = VkAccessFlags.VK_ACCESS_HOST_READ_BIT,
        };
        vkCmdPipelineBarrier(_cmd, VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT, VkPipelineStageFlags.VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &hostBarrier, 0, null, 0, null);
        Submit();

        var pixels = new ReadOnlySpan<Half>((void*)_readback.Mapped, _width * _height * 4);
        var result = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            result[i] = (float)pixels[i];
        }

        return result;
    }

    private static VkDescriptorType DescriptorType(ResourceBinding r) => r.Kind switch
    {
        "uniform-buffer" => VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER,
        "sampled-image" => VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,
        "sampler" => VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER,
        _ => throw new InvalidDataException($"Tipo de recurso sin soporte en el prototipo: {r.Kind} ({r.Register})."),
    };

    private Pipeline CreatePipeline(ShaderEntry variant, byte[] spirv)
    {
        // El vertex shader solo usa b0; si el fragment también, es el mismo binding.
        ResourceBinding[] resources = [.. _vertexResources.Concat(variant.Resources).GroupBy(r => (r.Set, r.Binding)).Select(g => g.First())];
        if (resources.Any(r => r.Set != 0))
        {
            throw new InvalidDataException($"{variant.File}: el prototipo solo maneja el set 0.");
        }

        var bindings = new VkDescriptorSetLayoutBinding[resources.Length];
        for (int i = 0; i < resources.Length; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)resources[i].Binding,
                descriptorType = DescriptorType(resources[i]),
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_VERTEX_BIT | VkShaderStageFlags.VK_SHADER_STAGE_FRAGMENT_BIT,
            };
        }

        VkDescriptorSetLayout setLayout;
        fixed (VkDescriptorSetLayoutBinding* b = bindings)
        {
            var info = new VkDescriptorSetLayoutCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
                bindingCount = (uint)bindings.Length,
                pBindings = b,
            };
            Check(vkCreateDescriptorSetLayout(_device, &info, null, &setLayout), "vkCreateDescriptorSetLayout");
        }

        var layoutInfo = new VkPipelineLayoutCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, setLayoutCount = 1, pSetLayouts = &setLayout };
        VkPipelineLayout layout;
        Check(vkCreatePipelineLayout(_device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        VkShaderModule fragment = CreateShaderModule(spirv);

        byte[] vsName = "VsQuad\0"u8.ToArray();
        byte[] psName = Encoding.ASCII.GetBytes(variant.Entry + "\0");
        VkPipeline handle;
        fixed (byte* vn = vsName, pn = psName)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                new() { sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, stage = VkShaderStageFlags.VK_SHADER_STAGE_VERTEX_BIT, module = _vertex, pName = (sbyte*)vn },
                new() { sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, stage = VkShaderStageFlags.VK_SHADER_STAGE_FRAGMENT_BIT, module = fragment, pName = (sbyte*)pn },
            };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
            var assembly = new VkPipelineInputAssemblyStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO,
                topology = VkPrimitiveTopology.VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP,
            };

            // Viewport con el alto positivo: el vertex shader ya invierte Y (-fvk-invert-y), así el NDC de D3D sirve tal cual.
            var viewport = new VkViewport { width = _width, height = _height, maxDepth = 1 };
            var scissor = new VkRect2D { extent = new VkExtent2D { width = (uint)_width, height = (uint)_height } };
            var viewportState = new VkPipelineViewportStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO,
                viewportCount = 1,
                pViewports = &viewport,
                scissorCount = 1,
                pScissors = &scissor,
            };
            var raster = new VkPipelineRasterizationStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO,
                polygonMode = VkPolygonMode.VK_POLYGON_MODE_FILL,
                cullMode = VkCullModeFlags.VK_CULL_MODE_NONE,
                frontFace = VkFrontFace.VK_FRONT_FACE_CLOCKWISE,
                lineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO,
                rasterizationSamples = VkSampleCountFlags.VK_SAMPLE_COUNT_1_BIT,
            };
            var attachment = new VkPipelineColorBlendAttachmentState
            {
                colorWriteMask = VkColorComponentFlags.VK_COLOR_COMPONENT_R_BIT | VkColorComponentFlags.VK_COLOR_COMPONENT_G_BIT
                    | VkColorComponentFlags.VK_COLOR_COMPONENT_B_BIT | VkColorComponentFlags.VK_COLOR_COMPONENT_A_BIT,
            };
            var blend = new VkPipelineColorBlendStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO,
                attachmentCount = 1,
                pAttachments = &attachment,
            };
            var info = new VkGraphicsPipelineCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO,
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &assembly,
                pViewportState = &viewportState,
                pRasterizationState = &raster,
                pMultisampleState = &multisample,
                pColorBlendState = &blend,
                layout = layout,
                renderPass = _renderPass,
            };
            Check(vkCreateGraphicsPipelines(_device, default, 1, &info, null, &handle), $"vkCreateGraphicsPipelines({variant.File})");
        }

        return new Pipeline(fragment, setLayout, layout, handle, resources);
    }

    private VkShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new VkShaderModuleCreateInfo { sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO, codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            VkShaderModule module;
            Check(vkCreateShaderModule(_device, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    private VkRenderPass CreateRenderPass()
    {
        var attachment = new VkAttachmentDescription
        {
            format = TargetFormat,
            samples = VkSampleCountFlags.VK_SAMPLE_COUNT_1_BIT,
            loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_CLEAR,
            storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
            stencilLoadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_DONT_CARE,
            stencilStoreOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_DONT_CARE,
            initialLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
            finalLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
        };
        var colorRef = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL };
        var subpass = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS,
            colorAttachmentCount = 1,
            pColorAttachments = &colorRef,
        };
        VkSubpassDependency* dependencies = stackalloc VkSubpassDependency[2]
        {
            new()
            {
                srcSubpass = VK_SUBPASS_EXTERNAL,
                dstSubpass = 0,
                srcStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                dstStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                dstAccessMask = VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
            },
            new()
            {
                srcSubpass = 0,
                dstSubpass = VK_SUBPASS_EXTERNAL,
                srcStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                dstStageMask = VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                srcAccessMask = VkAccessFlags.VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
            },
        };
        var info = new VkRenderPassCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO,
            attachmentCount = 1,
            pAttachments = &attachment,
            subpassCount = 1,
            pSubpasses = &subpass,
            dependencyCount = 2,
            pDependencies = dependencies,
        };
        VkRenderPass pass;
        Check(vkCreateRenderPass(_device, &info, null, &pass), "vkCreateRenderPass");
        return pass;
    }

    private Image CreateImage(VkImageType type, VkImageViewType viewType, VkFormat format, uint width, uint height, uint depth, VkImageUsageFlags usage)
    {
        var info = new VkImageCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            imageType = type,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = depth },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.VK_SAMPLE_COUNT_1_BIT,
            tiling = VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
            usage = usage,
            sharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE,
            initialLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
        };
        VkImage image;
        Check(vkCreateImage(_device, &info, null, &image), "vkCreateImage");
        VkMemoryRequirements req;
        vkGetImageMemoryRequirements(_device, image, &req);
        VkDeviceMemory memory = Allocate(req, VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        Check(vkBindImageMemory(_device, image, memory, 0), "vkBindImageMemory");
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            image = image,
            viewType = viewType,
            format = format,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 },
        };
        VkImageView view;
        Check(vkCreateImageView(_device, &viewInfo, null, &view), "vkCreateImageView");
        return new Image(image, memory, view);
    }

    private Buffer CreateBuffer(ulong size, VkBufferUsageFlags usage)
    {
        var info = new VkBufferCreateInfo { sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, size = size, usage = usage, sharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE };
        VkBuffer buffer;
        Check(vkCreateBuffer(_device, &info, null, &buffer), "vkCreateBuffer");
        VkMemoryRequirements req;
        vkGetBufferMemoryRequirements(_device, buffer, &req);
        VkDeviceMemory memory = Allocate(req, VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        Check(vkBindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        void* mapped;
        Check(vkMapMemory(_device, memory, 0, VK_WHOLE_SIZE, 0, &mapped), "vkMapMemory");
        return new Buffer(buffer, memory, (nint)mapped);
    }

    private VkDeviceMemory Allocate(VkMemoryRequirements req, VkMemoryPropertyFlags wanted)
    {
        uint index = uint.MaxValue;
        for (uint i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((req.memoryTypeBits & (1u << (int)i)) != 0 && (_memory.memoryTypes[(int)i].propertyFlags & wanted) == wanted)
            {
                index = i;
                break;
            }
        }

        if (index == uint.MaxValue)
        {
            throw new VulkanException($"No hay un tipo de memoria con {wanted}.");
        }

        var info = new VkMemoryAllocateInfo { sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, allocationSize = req.size, memoryTypeIndex = index };
        VkDeviceMemory memory;
        Check(vkAllocateMemory(_device, &info, null, &memory), "vkAllocateMemory");
        return memory;
    }

    private void Barrier(VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess, VkAccessFlags dstAccess,
        VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            srcAccessMask = srcAccess,
            dstAccessMask = dstAccess,
            oldLayout = from,
            newLayout = to,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 },
        };
        vkCmdPipelineBarrier(_cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private void Begin()
    {
        Check(vkResetCommandBuffer(_cmd, 0), "vkResetCommandBuffer");
        var info = new VkCommandBufferBeginInfo
        {
            sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT,
        };
        Check(vkBeginCommandBuffer(_cmd, &info), "vkBeginCommandBuffer");
    }

    private void Submit()
    {
        Check(vkEndCommandBuffer(_cmd), "vkEndCommandBuffer");
        VkCommandBuffer cmd = _cmd;
        var info = new VkSubmitInfo { sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, commandBufferCount = 1, pCommandBuffers = &cmd };
        VkFence fence = _fence;
        Check(vkQueueSubmit(_queue, 1, &info, fence), "vkQueueSubmit");
        Check(vkWaitForFences(_device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        Check(vkResetFences(_device, 1, &fence), "vkResetFences");
    }

    private void Destroy(Buffer b)
    {
        vkDestroyBuffer(_device, b.Handle, null);
        vkFreeMemory(_device, b.Memory, null);
    }

    private void Destroy(Image i)
    {
        vkDestroyImageView(_device, i.View, null);
        vkDestroyImage(_device, i.Handle, null);
        vkFreeMemory(_device, i.Memory, null);
    }

    private static void Check(VkResult result, string operation)
    {
        if (result != VkResult.VK_SUCCESS)
        {
            throw new VulkanException($"{operation} devolvió {result}.");
        }
    }

    public void Dispose()
    {
        if (_device.Value == null)
        {
            return;
        }

        vkDeviceWaitIdle(_device);
        foreach (Pipeline p in _pipelines.Values)
        {
            vkDestroyPipeline(_device, p.Handle, null);
            vkDestroyPipelineLayout(_device, p.Layout, null);
            vkDestroyDescriptorSetLayout(_device, p.SetLayout, null);
            vkDestroyShaderModule(_device, p.Fragment, null);
        }

        foreach (var (image, _) in _textures.Values)
        {
            Destroy(image);
        }

        vkDestroyShaderModule(_device, _vertex, null);
        Destroy(_quad);
        Destroy(_effects);
        Destroy(_readback);
        vkDestroyFramebuffer(_device, _framebuffer, null);
        vkDestroyRenderPass(_device, _renderPass, null);
        Destroy(_target);
        vkDestroyDescriptorPool(_device, _descriptors, null);
        vkDestroySampler(_device, _sampler, null);
        vkDestroyFence(_device, _fence, null);
        vkDestroyCommandPool(_device, _pool, null);
        vkDestroyDevice(_device, null);
        vkDestroyInstance(_instance, null);
    }
}
