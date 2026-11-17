using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>
/// Un cuadro de Vulkan: graba en el búfer de órdenes de su envío y lo envía al cerrarse (o con <see cref="Submit"/>/<see cref="Flush"/>,
/// tras lo que sigue en otro). Cada imagen lleva su estado y las barreras se ponen al usarla; cada dibujo es una pasada de
/// <c>vkCmdBeginRendering</c> con <c>LOAD</c>, así el orden de las órdenes es el de D3D11.
/// </summary>
internal sealed unsafe class VulkanFrame(VulkanGpu gpu, FrameContext context) : IGpuFrame
{
    private const int MaxBindings = 64;
    private bool _closed;

    public FrameContext Context { get; private set; } = context;

    /// <summary>Cuadros abiertos dentro de este en el mismo hilo.</summary>
    public int Depth { get; set; }

    private VkCommandBuffer Commands => Context.Commands;

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        if (Depth > 0)
        {
            Depth--;
            gpu.LeaveNested();
            return;
        }

        _closed = true;
        gpu.CloseFrame(this);
    }

    public GpuFence Submit()
    {
        long serial = Context.Serial;
        Flush();
        return new VulkanFence(gpu, serial);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        FrameContext next = gpu.AcquireContext();
        gpu.Submit(Context);
        Context = next;
    }

    // Subida y copia

    public void Write<T>(GpuBuffer buffer, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        var native = (VulkanBuffer)buffer;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        if (bytes.Length > native.Size)
        {
            throw new ArgumentException("Los datos no caben en el búfer.", nameof(data));
        }

        if (native.Shadow is { } shadow)
        {
            bytes.CopyTo(shadow);
            native.Uploaded = (-1, default, 0);
            return;
        }

        if (bytes.IsEmpty)
        {
            return;
        }

        var (source, offset, target) = Context.Upload(gpu, (ulong)bytes.Length, 16);
        bytes.CopyTo(new Span<byte>((byte*)target, bytes.Length));
        VulkanBarriers.Memory(Commands);
        var region = new VkBufferCopy { srcOffset = offset, size = (ulong)bytes.Length };
        vkCmdCopyBuffer(Commands, source, native.Handle, 1, &region);
        VulkanBarriers.Memory(Commands);
    }

    public void Write(GpuTexture texture, ReadOnlySpan<byte> pixels, int rowPitch, int depthPitch = 0, int mip = 0)
    {
        var native = (VulkanTexture)texture;
        (int width, int height, int depth) = native.MipSize(mip);
        int bpp = GpuFormats.BytesPerPixel(native.Format);
        if (rowPitch % bpp != 0)
        {
            throw new ArgumentException("El paso de fila no es múltiplo del tamaño del píxel.", nameof(rowPitch));
        }

        int slicePitch = depthPitch > 0 ? depthPitch : rowPitch * height;
        long needed = ((long)slicePitch * (depth - 1)) + ((long)rowPitch * (height - 1)) + ((long)width * bpp);
        if (pixels.Length < needed)
        {
            throw new ArgumentException("Faltan píxeles para el mip.", nameof(pixels));
        }

        var (source, offset, target) = Context.Upload(gpu, (ulong)needed, 16);
        pixels[..(int)needed].CopyTo(new Span<byte>((byte*)target, (int)needed));
        Use(native, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        var region = new VkBufferImageCopy
        {
            bufferOffset = offset,
            bufferRowLength = (uint)(rowPitch / bpp),
            bufferImageHeight = (uint)(slicePitch / rowPitch),
            imageSubresource = new VkImageSubresourceLayers { aspectMask = native.Aspect, mipLevel = (uint)mip, layerCount = 1 },
            imageExtent = new VkExtent3D { width = (uint)width, height = (uint)height, depth = (uint)depth },
        };
        vkCmdCopyBufferToImage(Commands, source, native.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    }

    public void Copy(GpuTexture source, GpuTexture destination)
    {
        var from = (VulkanTexture)source;
        var to = (VulkanTexture)destination;
        Use(from, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, write: false);
        Use(to, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        int mips = Math.Min(from.Mips, to.Mips);
        VkImageCopy* regions = stackalloc VkImageCopy[mips];
        for (int mip = 0; mip < mips; mip++)
        {
            (int w, int h, int d) = from.MipSize(mip);
            regions[mip] = new VkImageCopy
            {
                srcSubresource = new VkImageSubresourceLayers { aspectMask = from.Aspect, mipLevel = (uint)mip, layerCount = 1 },
                dstSubresource = new VkImageSubresourceLayers { aspectMask = to.Aspect, mipLevel = (uint)mip, layerCount = 1 },
                extent = new VkExtent3D { width = (uint)w, height = (uint)h, depth = (uint)d },
            };
        }

        vkCmdCopyImage(Commands, from.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, to.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            (uint)mips, regions);
    }

    public void Copy(GpuTexture source, in GpuBox region, GpuTexture destination, int x, int y)
    {
        var from = (VulkanTexture)source;
        var to = (VulkanTexture)destination;
        if (region.Width <= 0 || region.Height <= 0)
        {
            return;
        }

        Use(from, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, write: false);
        Use(to, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        var copy = new VkImageCopy
        {
            srcSubresource = new VkImageSubresourceLayers { aspectMask = from.Aspect, layerCount = 1 },
            srcOffset = new VkOffset3D { x = region.X, y = region.Y },
            dstSubresource = new VkImageSubresourceLayers { aspectMask = to.Aspect, layerCount = 1 },
            dstOffset = new VkOffset3D { x = x, y = y },
            extent = new VkExtent3D { width = (uint)region.Width, height = (uint)region.Height, depth = 1 },
        };
        vkCmdCopyImage(Commands, from.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, to.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &copy);
    }

    public void GenerateMips(GpuTexture texture)
    {
        var native = (VulkanTexture)texture;
        if (native.Mips < 2)
        {
            return;
        }

        // Todo el arreglo a TRANSFER_DST y, nivel a nivel, el de arriba a TRANSFER_SRC para reducirlo a la mitad (filtro lineal = caja de 2×2).
        Use(native, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        for (int mip = 1; mip < native.Mips; mip++)
        {
            VulkanBarriers.Image(Commands, native.Image, native.Range(mip - 1, 1), VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
            (int sw, int sh, int sd) = native.MipSize(mip - 1);
            (int dw, int dh, int dd) = native.MipSize(mip);
            var blit = new VkImageBlit
            {
                srcSubresource = new VkImageSubresourceLayers { aspectMask = native.Aspect, mipLevel = (uint)(mip - 1), layerCount = 1 },
                dstSubresource = new VkImageSubresourceLayers { aspectMask = native.Aspect, mipLevel = (uint)mip, layerCount = 1 },
            };
            blit.srcOffsets[1] = new VkOffset3D { x = sw, y = sh, z = sd };
            blit.dstOffsets[1] = new VkOffset3D { x = dw, y = dh, z = dd };
            vkCmdBlitImage(Commands, native.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, native.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                1, &blit, VkFilter.VK_FILTER_LINEAR);
        }

        // Los niveles 0..n-2 quedaron en TRANSFER_SRC y el último en TRANSFER_DST: todo a SRC, que es lo que el estado dice.
        VulkanBarriers.Image(Commands, native.Image, native.Range(native.Mips - 1, 1), VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
        native.Layout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        native.Written = true;
    }

    public void Clear(GpuTexture target, in GpuColor color)
    {
        var native = (VulkanTexture)target;
        Use(native, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        VkClearColorValue value = default;
        value.float32[0] = color.R;
        value.float32[1] = color.G;
        value.float32[2] = color.B;
        value.float32[3] = color.A;
        VkImageSubresourceRange range = native.Range(0, 1);
        vkCmdClearColorImage(Commands, native.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &value, 1, &range);
    }

    public void ClearUint(GpuResource target, uint value)
    {
        switch (target)
        {
            case VulkanTexture texture:
                Use(texture, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
                VkClearColorValue clear = default;
                for (int i = 0; i < 4; i++)
                {
                    if (VulkanFormats.IsInteger(texture.Format))
                    {
                        clear.uint32[i] = value;
                    }
                    else
                    {
                        clear.float32[i] = value;
                    }
                }

                VkImageSubresourceRange range = texture.Range(0, 1);
                vkCmdClearColorImage(Commands, texture.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clear, 1, &range);
                break;
            case VulkanBuffer { Handle.Value: not 0 } buffer:
                VulkanBarriers.Memory(Commands);
                vkCmdFillBuffer(Commands, buffer.Handle, 0, VK_WHOLE_SIZE, value);
                VulkanBarriers.Memory(Commands);
                break;
            default:
                throw new ArgumentException("Solo se pone a un valor un destino de color o algo con escritura desde un shader.", nameof(target));
        }
    }

    public void ClearDepth(GpuTexture depth, float value = 1)
    {
        var native = (VulkanTexture)depth;
        Use(native, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        var clear = new VkClearDepthStencilValue { depth = value };
        VkImageSubresourceRange range = native.Range(0, 1);
        vkCmdClearDepthStencilImage(Commands, native.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &clear, 1, &range);
    }

    public GpuDecoderPlanes Acquire(GpuSurfaceCache cache, IDecoderSurface surface) =>
        throw new NotSupportedException("Las superficies Vulkan del decodificador llegan con la fase 9 (D-066).");

    // Dibujo y cómputo

    public void Draw(GpuPipeline pipeline, in GpuTargets targets, in GpuBindings bindings, in GpuViewport viewport, in GpuDrawCall draw)
    {
        var native = (VulkanPipeline)pipeline;
        if (native.IsCompute)
        {
            throw new ArgumentException("Una canalización de cómputo se usa con Dispatch.", nameof(pipeline));
        }

        if (targets.Count == 0 && targets.Depth is null)
        {
            throw new ArgumentException("Sin destinos.", nameof(targets));
        }

        int width = int.MaxValue, height = int.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            var target = (VulkanTexture)targets[i]!;
            Use(target, VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, write: true);
            width = Math.Min(width, target.Width);
            height = Math.Min(height, target.Height);
        }

        if (targets.Depth is VulkanTexture depthTarget)
        {
            Use(depthTarget, VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL, write: true);
            width = Math.Min(width, depthTarget.Width);
            height = Math.Min(height, depthTarget.Height);
        }

        VkDescriptorSet set = Bind(native, bindings, targets);
        VkPipeline handle = native.Graphics(targets);

        VkRenderingAttachmentInfo* colors = stackalloc VkRenderingAttachmentInfo[GpuTargets.MaxColors];
        for (int i = 0; i < targets.Count; i++)
        {
            colors[i] = new VkRenderingAttachmentInfo
            {
                sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
                imageView = ((VulkanTexture)targets[i]!).Attachment,
                imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD,
                storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
            };
        }

        var depthAttachment = new VkRenderingAttachmentInfo
        {
            sType = VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO,
            imageView = targets.Depth is VulkanTexture d ? d.Attachment : default,
            imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_ATTACHMENT_OPTIMAL,
            loadOp = VkAttachmentLoadOp.VK_ATTACHMENT_LOAD_OP_LOAD,
            storeOp = VkAttachmentStoreOp.VK_ATTACHMENT_STORE_OP_STORE,
        };
        var rendering = new VkRenderingInfo
        {
            sType = VK_STRUCTURE_TYPE_RENDERING_INFO,
            renderArea = new VkRect2D { extent = new VkExtent2D { width = (uint)width, height = (uint)height } },
            layerCount = 1,
            colorAttachmentCount = (uint)targets.Count,
            pColorAttachments = colors,
            pDepthAttachment = targets.Depth is null ? null : &depthAttachment,
        };
        VkCommandBuffer commands = Commands;
        vkCmdBeginRendering(commands, &rendering);
        // Origen arriba a la izquierda como en D3D11: viewport con el alto negativo.
        var vp = new VkViewport
        {
            x = viewport.X,
            y = viewport.Y + viewport.Height,
            width = viewport.Width,
            height = -viewport.Height,
            minDepth = viewport.MinDepth,
            maxDepth = viewport.MaxDepth,
        };
        vkCmdSetViewport(commands, 0, 1, &vp);
        var scissor = new VkRect2D { extent = new VkExtent2D { width = (uint)width, height = (uint)height } };
        vkCmdSetScissor(commands, 0, 1, &scissor);
        vkCmdBindPipeline(commands, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS, handle);
        vkCmdBindDescriptorSets(commands, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_GRAPHICS, native.Layout, 0, 1, &set, 0, null);
        if (draw.VertexBuffer is VulkanBuffer vertices)
        {
            VkBuffer buffer = vertices.Handle;
            ulong offset = 0;
            vkCmdBindVertexBuffers(commands, 0, 1, &buffer, &offset);
        }

        if (draw.IndexBuffer is VulkanBuffer indices)
        {
            vkCmdBindIndexBuffer(commands, indices.Handle, 0,
                draw.IndexFormat == GpuIndexFormat.Bits32 ? VkIndexType.VK_INDEX_TYPE_UINT32 : VkIndexType.VK_INDEX_TYPE_UINT16);
            vkCmdDrawIndexed(commands, (uint)draw.Count, (uint)draw.Instances, (uint)draw.FirstIndex, draw.BaseVertex, 0);
        }
        else
        {
            vkCmdDraw(commands, (uint)draw.Count, (uint)draw.Instances, 0, 0);
        }

        vkCmdEndRendering(commands);
    }

    public void Dispatch(GpuPipeline pipeline, in GpuBindings bindings, int groupsX, int groupsY, int groupsZ = 1)
    {
        var native = (VulkanPipeline)pipeline;
        if (!native.IsCompute)
        {
            throw new ArgumentException("Una canalización de dibujo se usa con Draw.", nameof(pipeline));
        }

        VkDescriptorSet set = Bind(native, bindings, default);
        vkCmdBindPipeline(Commands, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_COMPUTE, native.Compute);
        vkCmdBindDescriptorSets(Commands, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_COMPUTE, native.Layout, 0, 1, &set, 0, null);
        vkCmdDispatch(Commands, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
        // Como D3D11 entre despachos: lo escrito es visible para lo siguiente.
        VulkanBarriers.Memory(Commands);
    }

    public void Resolve(GpuTexture multisampled, GpuTexture destination)
    {
        var from = (VulkanTexture)multisampled;
        var to = (VulkanTexture)destination;
        Use(from, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, write: false);
        Use(to, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, write: true);
        var region = new VkImageResolve
        {
            srcSubresource = new VkImageSubresourceLayers { aspectMask = from.Aspect, layerCount = 1 },
            dstSubresource = new VkImageSubresourceLayers { aspectMask = to.Aspect, layerCount = 1 },
            extent = new VkExtent3D { width = (uint)to.Width, height = (uint)to.Height, depth = 1 },
        };
        vkCmdResolveImage(Commands, from.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, to.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
    }

    // Lectura y medida

    public void Copy(GpuTexture source, in GpuBox region, GpuReadback destination)
    {
        var from = (VulkanTexture)source;
        var to = (VulkanReadback)destination;
        if (region.Width > to.Width || region.Height > to.Height)
        {
            throw new ArgumentException("La región no cabe en el destino de lectura.", nameof(region));
        }

        Use(from, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, write: false);
        var copy = new VkBufferImageCopy
        {
            bufferRowLength = (uint)to.Width,
            imageSubresource = new VkImageSubresourceLayers { aspectMask = from.Aspect, layerCount = 1 },
            imageOffset = new VkOffset3D { x = region.X, y = region.Y },
            imageExtent = new VkExtent3D { width = (uint)region.Width, height = (uint)region.Height, depth = 1 },
        };
        vkCmdCopyImageToBuffer(Commands, from.Image, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, to.Handle, 1, &copy);
        VulkanBarriers.Memory(Commands);
        to.Serial = Context.Serial;
    }

    public void Copy(GpuBuffer source, int offset, int bytes, GpuReadback destination)
    {
        var from = (VulkanBuffer)source;
        var to = (VulkanReadback)destination;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, to.Bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + bytes, from.Size);
        if (from.Shadow is { } shadow)
        {
            // Un búfer de constantes vive en la CPU: lo que se lee es lo último escrito.
            var (upload, uploadOffset, data) = Context.Upload(gpu, (ulong)bytes, 16);
            shadow.AsSpan(offset, bytes).CopyTo(new Span<byte>((byte*)data, bytes));
            var fromUpload = new VkBufferCopy { srcOffset = uploadOffset, size = (ulong)bytes };
            vkCmdCopyBuffer(Commands, upload, to.Handle, 1, &fromUpload);
        }
        else
        {
            var region = new VkBufferCopy { srcOffset = (ulong)offset, size = (ulong)bytes };
            vkCmdCopyBuffer(Commands, from.Handle, to.Handle, 1, &region);
        }

        VulkanBarriers.Memory(Commands);
        to.Serial = Context.Serial;
    }

    public void BeginTimer(GpuTimerQuery timer)
    {
        var native = (VulkanTimerQuery)timer;
        vkCmdResetQueryPool(Commands, native.Pool, 0, 2);
        vkCmdWriteTimestamp(Commands, VkPipelineStageFlags.VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, native.Pool, 0);
    }

    public void EndTimer(GpuTimerQuery timer)
    {
        var native = (VulkanTimerQuery)timer;
        vkCmdWriteTimestamp(Commands, VkPipelineStageFlags.VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT, native.Pool, 1);
        native.Serial = Context.Serial;
    }

    /// <summary>Pasa la imagen entera a <paramref name="layout"/> con una barrera si hace falta (cambio de estado o escritura de por medio).</summary>
    private void Use(VulkanTexture texture, VkImageLayout layout, bool write)
    {
        if (texture.Layout != layout || texture.Written || write)
        {
            VulkanBarriers.Image(Commands, texture.Image, texture.Range(), texture.Layout, layout);
            texture.Layout = layout;
        }

        texture.Written = write;
    }

    /// <summary>Pone las entradas en su estado y escribe el conjunto de descriptores de la pasada.</summary>
    private VkDescriptorSet Bind(VulkanPipeline pipeline, in GpuBindings bindings, in GpuTargets targets)
    {
        SpirvBinding[] layout = pipeline.Bindings;
        int count = layout.Length;
        if (count > MaxBindings)
        {
            throw new RenderException($"Demasiados enlaces en una pasada ({count}).");
        }

        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[Math.Max(1, count)];
        VkDescriptorBufferInfo* buffers = stackalloc VkDescriptorBufferInfo[Math.Max(1, count)];
        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[Math.Max(1, count)];
        DefaultResources defaults = gpu.Defaults;
        ulong uniformAlignment = Math.Max(16, gpu.Limits.minUniformBufferOffsetAlignment);

        // Primero las escrituras (u#), como el motor D3D11: lo que es salida no se lee en la misma pasada.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < count; i++)
            {
                SpirvBinding binding = layout[i];
                bool storage = binding.Binding >= 48;
                if (storage != (pass == 0))
                {
                    continue;
                }

                writes[i] = new VkWriteDescriptorSet
                {
                    sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET,
                    dstBinding = (uint)binding.Binding,
                    descriptorCount = 1,
                    descriptorType = VulkanPipeline.DescriptorType(binding.Kind),
                };
                GpuResource? resource = Resolve(bindings, binding.Binding);
                switch (binding.Kind)
                {
                    case SpirvDescriptor.UniformBuffer:
                        buffers[i] = Constants(resource as VulkanBuffer, uniformAlignment, defaults);
                        writes[i].pBufferInfo = &buffers[i];
                        break;
                    case SpirvDescriptor.StorageBuffer:
                        VulkanBuffer buffer = resource is VulkanBuffer { Handle.Value: not 0 } b ? b : defaults.Buffer;
                        buffers[i] = new VkDescriptorBufferInfo { buffer = buffer.Handle, range = VK_WHOLE_SIZE };
                        writes[i].pBufferInfo = &buffers[i];
                        break;
                    case SpirvDescriptor.Sampler:
                        images[i] = new VkDescriptorImageInfo { sampler = (resource as VulkanSampler ?? defaults.Sampler).Handle };
                        writes[i].pImageInfo = &images[i];
                        break;
                    case SpirvDescriptor.SampledImage:
                        VulkanTexture sampled = resource as VulkanTexture is { } t && !IsTarget(t, targets) && !IsStorage(t, bindings) && t.Sampled(binding.Dimension) != default
                            ? t
                            : binding.Dimension == SpirvDimension.D3 ? defaults.Texture3D : defaults.Texture2D;
                        VkImageLayout readLayout = sampled.Aspect == VkImageAspectFlags.VK_IMAGE_ASPECT_DEPTH_BIT
                            ? VkImageLayout.VK_IMAGE_LAYOUT_DEPTH_READ_ONLY_OPTIMAL
                            : VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
                        Use(sampled, readLayout, write: false);
                        images[i] = new VkDescriptorImageInfo { imageView = sampled.Sampled(binding.Dimension), imageLayout = readLayout };
                        writes[i].pImageInfo = &images[i];
                        break;
                    case SpirvDescriptor.StorageImage:
                        VulkanTexture written = resource as VulkanTexture is { Storage2D.Value: not 0 } w ? w : defaults.Texture2D;
                        Use(written, VkImageLayout.VK_IMAGE_LAYOUT_GENERAL, write: true);
                        images[i] = new VkDescriptorImageInfo
                        {
                            imageView = binding.Dimension == SpirvDimension.D2Array ? written.StorageArray() : written.Storage2D,
                            imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
                        };
                        writes[i].pImageInfo = &images[i];
                        break;
                    default:
                        throw new RenderException($"El enlace {binding.Binding} ({binding.Kind}) no tiene soporte en el motor Vulkan.");
                }
            }
        }

        VkDescriptorSet set = Context.AllocateSet(gpu, pipeline.SetLayout);
        for (int i = 0; i < count; i++)
        {
            writes[i].dstSet = set;
        }

        if (count > 0)
        {
            vkUpdateDescriptorSets(gpu.Device, (uint)count, writes, 0, null);
        }

        return set;
    }

    private VkDescriptorBufferInfo Constants(VulkanBuffer? buffer, ulong alignment, DefaultResources defaults)
    {
        if (buffer?.Shadow is not { } shadow)
        {
            ulong range = Math.Min(65536, gpu.Limits.maxUniformBufferRange);
            return buffer is { Handle.Value: not 0 }
                ? new VkDescriptorBufferInfo { buffer = buffer.Handle, range = Math.Min((ulong)buffer.Size, range) }
                : new VkDescriptorBufferInfo { buffer = defaults.Buffer.Handle, range = range };
        }

        if (buffer.Uploaded.Serial != Context.Serial)
        {
            var (upload, offset, data) = Context.Upload(gpu, (ulong)shadow.Length, alignment);
            shadow.CopyTo(new Span<byte>((byte*)data, shadow.Length));
            buffer.Uploaded = (Context.Serial, upload, offset);
        }

        return new VkDescriptorBufferInfo { buffer = buffer.Uploaded.Buffer, offset = buffer.Uploaded.Offset, range = (ulong)shadow.Length };
    }

    private static GpuResource? Resolve(in GpuBindings bindings, int binding) => binding switch
    {
        < 16 => binding < GpuBindings.MaxConstants ? bindings.ConstantBuffer(binding) : null,
        < 32 => bindings.Resource(binding - 16),
        < 48 => binding - 32 < GpuBindings.MaxSamplers ? bindings.Sampler(binding - 32) : null,
        _ => binding - 48 < GpuBindings.MaxStorage ? bindings.StorageResource(binding - 48) : null,
    };

    // D3D11 quita de las entradas lo que en la misma pasada es salida: se lee cero.
    private static bool IsTarget(VulkanTexture texture, in GpuTargets targets)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (ReferenceEquals(targets[i], texture))
            {
                return true;
            }
        }

        return ReferenceEquals(targets.Depth, texture);
    }

    private static bool IsStorage(VulkanTexture texture, in GpuBindings bindings)
    {
        for (int i = 0; i < bindings.StorageCount; i++)
        {
            if (ReferenceEquals(bindings.StorageResource(i), texture))
            {
                return true;
            }
        }

        return false;
    }
}
