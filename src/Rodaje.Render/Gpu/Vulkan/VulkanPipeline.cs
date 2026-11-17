using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>
/// Disposición de enlaces (la unión de lo que leen sus sombreadores) y la canalización: la de cómputo al crearse; las de dibujo, una
/// por combinación de formatos de destino, la primera vez que se dibuja con ella (Vulkan los fija en la canalización y el contrato no).
/// </summary>
internal sealed unsafe class VulkanPipeline : GpuPipeline
{
    private readonly VulkanGpu _gpu;
    private readonly Dictionary<(VkFormat C0, VkFormat C1, VkFormat C2, VkFormat C3, int Count, VkFormat Depth), VkPipeline> _graphics = [];
    private VkPipeline _compute;

    public VulkanPipeline(VulkanGpu gpu, GpuPipelineDesc desc) : base(desc)
    {
        _gpu = gpu;
        VulkanShader[] shaders = desc.Compute is { } compute
            ? [(VulkanShader)compute]
            : [(VulkanShader)(desc.Vertex ?? throw new ArgumentException("Falta el sombreador de vértices.", nameof(desc))),
                (VulkanShader)(desc.Pixel ?? throw new ArgumentException("Falta el sombreador de píxeles.", nameof(desc)))];
        var bindings = new SortedDictionary<int, SpirvBinding>();
        foreach (VulkanShader shader in shaders)
        {
            foreach (SpirvBinding binding in shader.Module.Bindings)
            {
                if (bindings.TryGetValue(binding.Binding, out SpirvBinding other) && other != binding)
                {
                    throw new RenderException($"El enlace {binding.Binding} es {other.Kind} en un sombreador y {binding.Kind} en otro.");
                }

                bindings[binding.Binding] = binding;
            }
        }

        Bindings = [.. bindings.Values];
        var native = new VkDescriptorSetLayoutBinding[Bindings.Length];
        for (int i = 0; i < Bindings.Length; i++)
        {
            native[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)Bindings[i].Binding,
                descriptorType = DescriptorType(Bindings[i].Kind),
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_ALL,
            };
        }

        fixed (VkDescriptorSetLayoutBinding* p = native)
        {
            var info = new VkDescriptorSetLayoutCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
                bindingCount = (uint)native.Length,
                pBindings = p,
            };
            VkDescriptorSetLayout setLayout;
            gpu.Check(vkCreateDescriptorSetLayout(gpu.Device, &info, null, &setLayout), "vkCreateDescriptorSetLayout");
            SetLayout = setLayout;
        }

        try
        {
            VkDescriptorSetLayout layoutHandle = SetLayout;
            var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO, setLayoutCount = 1, pSetLayouts = &layoutHandle };
            VkPipelineLayout pipelineLayout;
            gpu.Check(VulkanGpu.Injected("vkCreatePipelineLayout") ?? vkCreatePipelineLayout(gpu.Device, &pipelineLayoutInfo, null, &pipelineLayout), "vkCreatePipelineLayout");
            Layout = pipelineLayout;

            if (desc.Compute is not null)
            {
                CreateCompute(shaders[0]);
            }
        }
        catch
        {
            if (Layout != default)
            {
                vkDestroyPipelineLayout(gpu.Device, Layout, null);
            }

            vkDestroyDescriptorSetLayout(gpu.Device, SetLayout, null);
            throw;
        }
    }

    public SpirvBinding[] Bindings { get; }

    public VkDescriptorSetLayout SetLayout { get; }

    public VkPipelineLayout Layout { get; }

    public VkPipeline Compute => _compute;

    public static VkDescriptorType DescriptorType(SpirvDescriptor kind) => kind switch
    {
        SpirvDescriptor.UniformBuffer => VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER,
        SpirvDescriptor.StorageBuffer => VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,
        SpirvDescriptor.SampledImage => VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE,
        SpirvDescriptor.StorageImage => VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_IMAGE,
        SpirvDescriptor.Sampler => VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER,
        SpirvDescriptor.UniformTexelBuffer => VkDescriptorType.VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER,
        _ => VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER,
    };

    /// <summary>La canalización de dibujo para estos destinos.</summary>
    public VkPipeline Graphics(in GpuTargets targets)
    {
        Span<VkFormat> colors = stackalloc VkFormat[GpuTargets.MaxColors];
        for (int i = 0; i < targets.Count; i++)
        {
            colors[i] = ((VulkanTexture)targets[i]!).NativeFormat;
        }

        VkFormat depth = targets.Depth is VulkanTexture d ? d.NativeFormat : VkFormat.VK_FORMAT_UNDEFINED;
        var key = (colors[0], colors[1], colors[2], colors[3], targets.Count, depth);
        if (!_graphics.TryGetValue(key, out VkPipeline pipeline))
        {
            pipeline = CreateGraphics(colors[..targets.Count], depth);
            _graphics.Add(key, pipeline);
        }

        return pipeline;
    }

    protected override void Release()
    {
        VkDevice device = _gpu.Device;
        VkPipeline[] pipelines = [.. _graphics.Values, .. _compute != default ? new[] { _compute } : []];
        VkPipelineLayout layout = Layout;
        VkDescriptorSetLayout setLayout = SetLayout;
        _gpu.Defer(() =>
        {
            foreach (VkPipeline pipeline in pipelines)
            {
                vkDestroyPipeline(device, pipeline, null);
            }

            vkDestroyPipelineLayout(device, layout, null);
            vkDestroyDescriptorSetLayout(device, setLayout, null);
        });
    }

    private void CreateCompute(VulkanShader shader)
    {
        byte[] entry = EntryName(shader);
        fixed (byte* name = entry)
        {
            var info = new VkComputePipelineCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
                stage = new VkPipelineShaderStageCreateInfo
                {
                    sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                    stage = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT,
                    module = shader.Handle,
                    pName = (sbyte*)name,
                },
                layout = Layout,
            };
            VkPipeline pipeline;
            _gpu.Check(VulkanGpu.Injected("vkCreateComputePipelines") ?? vkCreateComputePipelines(_gpu.Device, default, 1, &info, null, &pipeline), $"vkCreateComputePipelines({shader.Id})");
            _compute = pipeline;
        }
    }

    private VkPipeline CreateGraphics(ReadOnlySpan<VkFormat> colors, VkFormat depthFormat)
    {
        var vertex = (VulkanShader)Desc.Vertex!;
        var pixel = (VulkanShader)Desc.Pixel!;
        byte[] vertexName = EntryName(vertex);
        byte[] pixelName = EntryName(pixel);
        GpuVertexLayout? vertexLayout = Desc.VertexLayout;
        int attributes = vertexLayout?.Elements.Length ?? 0;
        VkVertexInputAttributeDescription* attributeDescs = stackalloc VkVertexInputAttributeDescription[Math.Max(1, attributes)];
        for (int i = 0; i < attributes; i++)
        {
            GpuVertexElement element = vertexLayout!.Elements[i];
            // DXC numera las entradas del sombreador de vértices en el orden en que se declaran.
            attributeDescs[i] = new VkVertexInputAttributeDescription
            {
                location = (uint)i,
                binding = 0,
                format = element.Format switch
                {
                    GpuVertexFormat.Float1 => VkFormat.VK_FORMAT_R32_SFLOAT,
                    GpuVertexFormat.Float2 => VkFormat.VK_FORMAT_R32G32_SFLOAT,
                    GpuVertexFormat.Float3 => VkFormat.VK_FORMAT_R32G32B32_SFLOAT,
                    GpuVertexFormat.Float4 => VkFormat.VK_FORMAT_R32G32B32A32_SFLOAT,
                    GpuVertexFormat.Unorm8x4 => VkFormat.VK_FORMAT_R8G8B8A8_UNORM,
                    _ => VkFormat.VK_FORMAT_R32_UINT,
                },
                offset = (uint)element.Offset,
            };
        }

        var vertexBinding = new VkVertexInputBindingDescription { binding = 0, stride = (uint)(vertexLayout?.Stride ?? 0), inputRate = VkVertexInputRate.VK_VERTEX_INPUT_RATE_VERTEX };
        GpuRasterState raster = Desc.Raster;
        GpuBlend blend = Desc.Blend;
        VkPipelineColorBlendAttachmentState* blends = stackalloc VkPipelineColorBlendAttachmentState[GpuTargets.MaxColors];
        for (int i = 0; i < colors.Length; i++)
        {
            blends[i] = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = blend.IsOpaque ? VK_FALSE : VK_TRUE,
                srcColorBlendFactor = Factor(blend.Source),
                dstColorBlendFactor = Factor(blend.Destination),
                colorBlendOp = VkBlendOp.VK_BLEND_OP_ADD,
                srcAlphaBlendFactor = Factor(blend.SourceAlpha),
                dstAlphaBlendFactor = Factor(blend.DestinationAlpha),
                alphaBlendOp = VkBlendOp.VK_BLEND_OP_ADD,
                colorWriteMask = VkColorComponentFlags.VK_COLOR_COMPONENT_R_BIT | VkColorComponentFlags.VK_COLOR_COMPONENT_G_BIT
                    | VkColorComponentFlags.VK_COLOR_COMPONENT_B_BIT | VkColorComponentFlags.VK_COLOR_COMPONENT_A_BIT,
            };
        }

        VkDynamicState* dynamic = stackalloc VkDynamicState[] { VkDynamicState.VK_DYNAMIC_STATE_VIEWPORT, VkDynamicState.VK_DYNAMIC_STATE_SCISSOR };
        fixed (VkFormat* colorFormats = colors)
        fixed (byte* vn = vertexName)
        fixed (byte* pn = pixelName)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[]
            {
                new() { sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, stage = VkShaderStageFlags.VK_SHADER_STAGE_VERTEX_BIT, module = vertex.Handle, pName = (sbyte*)vn },
                new() { sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO, stage = VkShaderStageFlags.VK_SHADER_STAGE_FRAGMENT_BIT, module = pixel.Handle, pName = (sbyte*)pn },
            };
            var rendering = new VkPipelineRenderingCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO,
                colorAttachmentCount = (uint)colors.Length,
                pColorAttachmentFormats = colorFormats,
                depthAttachmentFormat = depthFormat,
            };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO,
                vertexBindingDescriptionCount = vertexLayout is null ? 0u : 1u,
                pVertexBindingDescriptions = &vertexBinding,
                vertexAttributeDescriptionCount = (uint)attributes,
                pVertexAttributeDescriptions = attributeDescs,
            };
            var assembly = new VkPipelineInputAssemblyStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO,
                topology = raster.Topology switch
                {
                    GpuTopology.TriangleList => VkPrimitiveTopology.VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST,
                    GpuTopology.LineList => VkPrimitiveTopology.VK_PRIMITIVE_TOPOLOGY_LINE_LIST,
                    GpuTopology.LineStrip => VkPrimitiveTopology.VK_PRIMITIVE_TOPOLOGY_LINE_STRIP,
                    _ => VkPrimitiveTopology.VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP,
                },
            };
            var viewportState = new VkPipelineViewportStateCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO, viewportCount = 1, scissorCount = 1 };
            bool bias = raster.DepthBias != 0 || raster.SlopeScaledDepthBias != 0;
            var rasterization = new VkPipelineRasterizationStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO,
                polygonMode = VkPolygonMode.VK_POLYGON_MODE_FILL,
                cullMode = raster.Cull switch
                {
                    GpuCullMode.Back => VkCullModeFlags.VK_CULL_MODE_BACK_BIT,
                    GpuCullMode.Front => VkCullModeFlags.VK_CULL_MODE_FRONT_BIT,
                    _ => VkCullModeFlags.VK_CULL_MODE_NONE,
                },
                // Con el viewport invertido (alto negativo), el sentido horario en pantalla es el de D3D11.
                frontFace = VkFrontFace.VK_FRONT_FACE_CLOCKWISE,
                depthBiasEnable = bias ? VK_TRUE : VK_FALSE,
                depthBiasConstantFactor = raster.DepthBias,
                depthBiasSlopeFactor = raster.SlopeScaledDepthBias,
                depthBiasClamp = _gpu.DepthBiasClampSupported ? raster.DepthBiasClamp : 0,
                lineWidth = 1,
            };
            var multisample = new VkPipelineMultisampleStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO,
                rasterizationSamples = (VkSampleCountFlags)Math.Max(1, Desc.Samples),
            };
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_DEPTH_STENCIL_STATE_CREATE_INFO,
                depthTestEnable = Desc.Depth.Test ? VK_TRUE : VK_FALSE,
                depthWriteEnable = Desc.Depth.Test && Desc.Depth.Write ? VK_TRUE : VK_FALSE,
                depthCompareOp = Desc.Depth.Compare switch
                {
                    GpuCompare.Never => VkCompareOp.VK_COMPARE_OP_NEVER,
                    GpuCompare.Less => VkCompareOp.VK_COMPARE_OP_LESS,
                    GpuCompare.Equal => VkCompareOp.VK_COMPARE_OP_EQUAL,
                    GpuCompare.LessEqual => VkCompareOp.VK_COMPARE_OP_LESS_OR_EQUAL,
                    GpuCompare.Greater => VkCompareOp.VK_COMPARE_OP_GREATER,
                    GpuCompare.NotEqual => VkCompareOp.VK_COMPARE_OP_NOT_EQUAL,
                    GpuCompare.GreaterEqual => VkCompareOp.VK_COMPARE_OP_GREATER_OR_EQUAL,
                    _ => VkCompareOp.VK_COMPARE_OP_ALWAYS,
                },
            };
            var colorBlend = new VkPipelineColorBlendStateCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO,
                attachmentCount = (uint)colors.Length,
                pAttachments = blends,
            };
            var dynamicState = new VkPipelineDynamicStateCreateInfo { sType = VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO, dynamicStateCount = 2, pDynamicStates = dynamic };
            var info = new VkGraphicsPipelineCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO,
                pNext = &rendering,
                stageCount = 2,
                pStages = stages,
                pVertexInputState = &vertexInput,
                pInputAssemblyState = &assembly,
                pViewportState = &viewportState,
                pRasterizationState = &rasterization,
                pMultisampleState = &multisample,
                pDepthStencilState = &depthStencil,
                pColorBlendState = &colorBlend,
                pDynamicState = &dynamicState,
                layout = Layout,
            };
            VkPipeline pipeline;
            _gpu.Check(vkCreateGraphicsPipelines(_gpu.Device, default, 1, &info, null, &pipeline), $"vkCreateGraphicsPipelines({vertex.Id}, {pixel.Id})");
            return pipeline;
        }
    }

    private static byte[] EntryName(VulkanShader shader) => [.. System.Text.Encoding.UTF8.GetBytes(shader.Module.EntryPoint), 0];

    private static VkBlendFactor Factor(GpuBlendFactor factor) => factor switch
    {
        GpuBlendFactor.Zero => VkBlendFactor.VK_BLEND_FACTOR_ZERO,
        GpuBlendFactor.One => VkBlendFactor.VK_BLEND_FACTOR_ONE,
        GpuBlendFactor.SourceColor => VkBlendFactor.VK_BLEND_FACTOR_SRC_COLOR,
        GpuBlendFactor.InverseSourceColor => VkBlendFactor.VK_BLEND_FACTOR_ONE_MINUS_SRC_COLOR,
        GpuBlendFactor.SourceAlpha => VkBlendFactor.VK_BLEND_FACTOR_SRC_ALPHA,
        GpuBlendFactor.InverseSourceAlpha => VkBlendFactor.VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
        GpuBlendFactor.DestinationColor => VkBlendFactor.VK_BLEND_FACTOR_DST_COLOR,
        GpuBlendFactor.InverseDestinationColor => VkBlendFactor.VK_BLEND_FACTOR_ONE_MINUS_DST_COLOR,
        GpuBlendFactor.DestinationAlpha => VkBlendFactor.VK_BLEND_FACTOR_DST_ALPHA,
        _ => VkBlendFactor.VK_BLEND_FACTOR_ONE_MINUS_DST_ALPHA,
    };
}
