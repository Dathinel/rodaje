using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;

namespace Rodaje.VulkanVideo;

public sealed unsafe class VulkanContext : IDisposable
{
    public struct PushConsts
    {
        public uint outWidth;
        public uint outHeight;
        public uint is10Bit;
        public uint isFullRange;
        public float maxU;
        public float maxV;
    }

    public string GpuName { get; private set; } = "";
    public string DriverVersion { get; private set; } = "";
    public bool SupportsVulkanVideo { get; private set; }
    public List<string> PresentVideoExtensions { get; } = new();
    public List<string> AbsentVideoExtensions { get; } = new();
    public int ValidationErrors { get; private set; }

    public AVBufferRef* HwDeviceRef { get; private set; }
    public VkDevice Device { get; private set; }
    public VkInstance Instance { get; private set; }
    public VkPhysicalDevice PhysDevice { get; private set; }
    public int GfxQueueFamily { get; private set; }
    public int DecodeQueueFamily { get; private set; } = -1;
    public VkQueue GfxQueue { get; private set; }

    // Compute resources
    private VkShaderModule _shaderModule;
    private VkDescriptorSetLayout _descLayout;
    private VkPipelineLayout _pipeLayout;
    private VkPipeline _pipeline;
    private VkSampler _sampler;
    private VkImage _outImage;
    private VkDeviceMemory _outMem;
    private VkImageView _outView;
    private VkBuffer _stagingBuf;
    private VkDeviceMemory _stagingMem;
    private VkDescriptorPool _descPool;
    private VkDescriptorSet _descSet;
    private VkCommandPool _cmdPool;
    private VkCommandBuffer _cmd;
    private VkFence _fence;

    private readonly VkWriteDescriptorSet* _imgWrites;
    private readonly VkImageMemoryBarrier* _barriersInit;
    private readonly List<IntPtr> _allocatedDevExtStrings = new();
    private byte** _devExtPtrsArray;

    public const int TargetWidth = 1920;
    public const int TargetHeight = 1080;
    public const ulong TargetBufferSize = TargetWidth * TargetHeight * 4;

    public VulkanContext(string? gpuSubstring, bool forceUpload)
    {
        _imgWrites = (VkWriteDescriptorSet*)NativeMemory.AllocZeroed(4, (nuint)sizeof(VkWriteDescriptorSet));
        _barriersInit = (VkImageMemoryBarrier*)NativeMemory.AllocZeroed(2, (nuint)sizeof(VkImageMemoryBarrier));

        CreateInstance();
        SelectPhysicalDevice(gpuSubstring);
        CreateDeviceAndHwContext(forceUpload);
        InitComputePipeline();
    }

    private void CreateInstance()
    {
        nint vkHandle = NativeLibrary.Load(OperatingSystem.IsWindows() ? "vulkan-1.dll" : "libvulkan.so.1");

        // Check validation layers
        uint layerCount = 0;
        vkEnumerateInstanceLayerProperties(&layerCount, null);
        var layers = new VkLayerProperties[layerCount];
        bool hasValidationLayer = false;
        fixed (VkLayerProperties* pLayers = layers)
        {
            vkEnumerateInstanceLayerProperties(&layerCount, pLayers);
            for (int i = 0; i < layerCount; i++)
            {
                sbyte* pName = (sbyte*)&pLayers[i].layerName;
                string name = Marshal.PtrToStringUTF8((IntPtr)pName) ?? "";
                if (name == "VK_LAYER_KHRONOS_validation")
                {
                    hasValidationLayer = true;
                    break;
                }
            }
        }

        bool enableValidation = (Environment.GetEnvironmentVariable("RODAJE_VK_VALIDAR") == "1") && hasValidationLayer;

        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_APPLICATION_INFO,
            apiVersion = (1 << 22) | (3 << 12), // Vulkan 1.3
        };

        var instInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
            pApplicationInfo = &appInfo,
        };

        byte* validationLayerName = null;
        if (enableValidation)
        {
            validationLayerName = (byte*)Marshal.StringToHGlobalAnsi("VK_LAYER_KHRONOS_validation");
            byte** layerNames = stackalloc byte*[1] { validationLayerName };
            instInfo.enabledLayerCount = 1;
            instInfo.ppEnabledLayerNames = (sbyte**)layerNames;
        }

        VkInstance inst;
        var res = vkCreateInstance(&instInfo, null, &inst);
        if (validationLayerName != null) Marshal.FreeHGlobal((IntPtr)validationLayerName);

        if (res != VkResult.VK_SUCCESS)
        {
            throw new InvalidOperationException($"vkCreateInstance falló: {res}");
        }

        Instance = inst;
    }

    private void SelectPhysicalDevice(string? gpuSubstring)
    {
        uint devCount = 0;
        vkEnumeratePhysicalDevices(Instance, &devCount, null);
        if (devCount == 0) throw new InvalidOperationException("No se encontraron dispositivos Vulkan físicos.");

        var pDevs = new VkPhysicalDevice[devCount];
        fixed (VkPhysicalDevice* ptr = pDevs)
        {
            vkEnumeratePhysicalDevices(Instance, &devCount, ptr);
        }

        int selectedIdx = -1;
        for (int i = 0; i < pDevs.Length; i++)
        {
            VkPhysicalDeviceProperties props;
            vkGetPhysicalDeviceProperties(pDevs[i], &props);
            sbyte* pName = (sbyte*)&props.deviceName;
            string devName = Marshal.PtrToStringUTF8((IntPtr)pName) ?? "";

            if (!string.IsNullOrEmpty(gpuSubstring) && devName.Contains(gpuSubstring, StringComparison.OrdinalIgnoreCase))
            {
                selectedIdx = i;
                break;
            }

            if (selectedIdx < 0 && props.deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)
            {
                selectedIdx = i;
            }
        }

        if (selectedIdx < 0) selectedIdx = 0;

        PhysDevice = pDevs[selectedIdx];
        VkPhysicalDeviceProperties selProps;
        vkGetPhysicalDeviceProperties(PhysDevice, &selProps);
        sbyte* pSelName = (sbyte*)&selProps.deviceName;
        GpuName = Marshal.PtrToStringUTF8((IntPtr)pSelName) ?? "Desconocido";

        uint v = selProps.driverVersion;
        if (selProps.vendorID == 0x10DE) // NVIDIA: major.minor
        {
            DriverVersion = $"{(v >> 22) & 0x3FF}.{(v >> 14) & 0xFF}.{(v >> 6) & 0xFF}.{v & 0x3F}";
        }
        else
        {
            DriverVersion = $"{v >> 22}.{(v >> 12) & 0x3FF}.{v & 0xFFF}";
        }

        // Query queue families
        uint qfCount = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(PhysDevice, &qfCount, null);
        var qfProps = new VkQueueFamilyProperties[qfCount];
        fixed (VkQueueFamilyProperties* pQf = qfProps)
        {
            vkGetPhysicalDeviceQueueFamilyProperties(PhysDevice, &qfCount, pQf);
        }

        int gfx = -1;
        int decode = -1;
        for (int i = 0; i < qfProps.Length; i++)
        {
            uint f = (uint)qfProps[i].queueFlags;
            if ((f & (uint)VkQueueFlags.VK_QUEUE_GRAPHICS_BIT) != 0 && gfx < 0) gfx = i;
            // 0x20 is VK_QUEUE_VIDEO_DECODE_BIT_KHR
            if ((f & 0x00000020) != 0 && decode < 0) decode = i;
        }

        if (gfx < 0) throw new InvalidOperationException("No se encontró una familia de colas gráficas/cómputo.");
        GfxQueueFamily = gfx;
        DecodeQueueFamily = decode;

        // Query device extensions
        uint extCount = 0;
        vkEnumerateDeviceExtensionProperties(PhysDevice, null, &extCount, null);
        var exts = new VkExtensionProperties[extCount];
        var supportedExts = new HashSet<string>(StringComparer.Ordinal);
        fixed (VkExtensionProperties* pe = exts)
        {
            vkEnumerateDeviceExtensionProperties(PhysDevice, null, &extCount, pe);
            for (int i = 0; i < extCount; i++)
            {
                sbyte* namePtr = (sbyte*)&pe[i].extensionName;
                supportedExts.Add(Marshal.PtrToStringUTF8((IntPtr)namePtr)!);
            }
        }

        string[] checkedVideoExts = new[]
        {
            "VK_KHR_video_queue",
            "VK_KHR_video_decode_queue",
            "VK_KHR_video_decode_h264",
            "VK_KHR_video_decode_h265"
        };

        foreach (var ext in checkedVideoExts)
        {
            if (supportedExts.Contains(ext)) PresentVideoExtensions.Add(ext);
            else AbsentVideoExtensions.Add(ext);
        }

        SupportsVulkanVideo = (DecodeQueueFamily >= 0) &&
                              supportedExts.Contains("VK_KHR_video_queue") &&
                              supportedExts.Contains("VK_KHR_video_decode_queue") &&
                              supportedExts.Contains("VK_KHR_video_decode_h264") &&
                              supportedExts.Contains("VK_KHR_video_decode_h265");
    }

    private void CreateDeviceAndHwContext(bool forceUpload)
    {
        uint extCount = 0;
        vkEnumerateDeviceExtensionProperties(PhysDevice, null, &extCount, null);
        var exts = new VkExtensionProperties[extCount];
        var supportedExts = new HashSet<string>(StringComparer.Ordinal);
        fixed (VkExtensionProperties* pe = exts)
        {
            vkEnumerateDeviceExtensionProperties(PhysDevice, null, &extCount, pe);
            for (int i = 0; i < extCount; i++)
            {
                sbyte* namePtr = (sbyte*)&pe[i].extensionName;
                supportedExts.Add(Marshal.PtrToStringUTF8((IntPtr)namePtr)!);
            }
        }

        var devExtsToEnable = new List<string>();
        string[] candidateDevExts = new[]
        {
            "VK_KHR_sampler_ycbcr_conversion",
            "VK_KHR_synchronization2",
            "VK_KHR_timeline_semaphore",
            "VK_KHR_video_queue",
            "VK_KHR_video_decode_queue",
            "VK_KHR_video_decode_h264",
            "VK_KHR_video_decode_h265",
            "VK_KHR_video_maintenance1",
            "VK_KHR_internally_synchronized_queues"
        };

        foreach (var ext in candidateDevExts)
        {
            if (ext.Contains("video") && (!SupportsVulkanVideo || forceUpload)) continue;
            if (supportedExts.Contains(ext)) devExtsToEnable.Add(ext);
        }

        byte** devExtPtrs = (byte**)NativeMemory.Alloc((nuint)devExtsToEnable.Count, (nuint)sizeof(byte*));
        _devExtPtrsArray = devExtPtrs;
        for (int i = 0; i < devExtsToEnable.Count; i++)
        {
            nint p = Marshal.StringToHGlobalAnsi(devExtsToEnable[i]);
            _allocatedDevExtStrings.Add(p);
            devExtPtrs[i] = (byte*)p;
        }

        var qFamilies = new List<int> { GfxQueueFamily };
        if (SupportsVulkanVideo && !forceUpload && DecodeQueueFamily >= 0 && DecodeQueueFamily != GfxQueueFamily)
        {
            qFamilies.Add(DecodeQueueFamily);
        }

        float priority = 1.0f;
        var qInfos = stackalloc VkDeviceQueueCreateInfo[qFamilies.Count];
        for (int i = 0; i < qFamilies.Count; i++)
        {
            qInfos[i] = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = (uint)qFamilies[i],
                queueCount = 1,
                pQueuePriorities = &priority
            };
        }

        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES,
            synchronization2 = 1
        };
        var features12 = new VkPhysicalDeviceVulkan12Features
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES,
            pNext = &features13,
            timelineSemaphore = 1
        };
        var features11 = new VkPhysicalDeviceVulkan11Features
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES,
            pNext = &features12,
            samplerYcbcrConversion = 1
        };

        var devInfo = new VkDeviceCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
            pNext = &features11,
            queueCreateInfoCount = (uint)qFamilies.Count,
            pQueueCreateInfos = qInfos,
            enabledExtensionCount = (uint)devExtsToEnable.Count,
            ppEnabledExtensionNames = (sbyte**)devExtPtrs
        };

        VkDevice dev;
        var res = vkCreateDevice(PhysDevice, &devInfo, null, &dev);
        if (res != VkResult.VK_SUCCESS) throw new InvalidOperationException($"vkCreateDevice falló: {res}");
        Device = dev;

        VkQueue q;
        vkGetDeviceQueue(Device, (uint)GfxQueueFamily, 0, &q);
        GfxQueue = q;

        // Allocate FFmpeg HW device context
        nint vkHandle = NativeLibrary.Load(OperatingSystem.IsWindows() ? "vulkan-1.dll" : "libvulkan.so.1");
        var pGetProcAddr = (delegate* unmanaged<VkInstance, sbyte*, void*>)NativeLibrary.GetExport(vkHandle, "vkGetInstanceProcAddr");

        AVBufferRef* hwdevBuf = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
        if (hwdevBuf == null) throw new InvalidOperationException("av_hwdevice_ctx_alloc falló.");

        AVHWDeviceContext* hwdev = (AVHWDeviceContext*)hwdevBuf->data;
        var vkctx = (FfmpegInterop.AVVulkanDeviceContext*)hwdev->hwctx;
        vkctx->get_proc_addr = pGetProcAddr;
        vkctx->inst = Instance;
        vkctx->phys_dev = PhysDevice;
        vkctx->act_dev = Device;
        vkctx->device_features.sType = VkStructureType.VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2;
        vkctx->device_features.pNext = null;
        vkGetPhysicalDeviceFeatures(PhysDevice, &vkctx->device_features.features);
        vkctx->enabled_dev_extensions = devExtPtrs;
        vkctx->nb_enabled_dev_extensions = devExtsToEnable.Count;

        uint qfCount = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(PhysDevice, &qfCount, null);
        var qfProps = new VkQueueFamilyProperties[qfCount];
        fixed (VkQueueFamilyProperties* pQf = qfProps)
        {
            vkGetPhysicalDeviceQueueFamilyProperties(PhysDevice, &qfCount, pQf);
        }

        var qfArray = (FfmpegInterop.AVVulkanDeviceQueueFamily*)vkctx->qf_raw;
        for (int i = 0; i < qFamilies.Count; i++)
        {
            int qfIdx = qFamilies[i];
            qfArray[i].idx = qfIdx;
            qfArray[i].num = 1;
            qfArray[i].flags = (uint)qfProps[qfIdx].queueFlags;
            qfArray[i].video_caps = 0;
        }
        vkctx->nb_qf = qFamilies.Count;

        int initRet = ffmpeg.av_hwdevice_ctx_init(hwdevBuf);
        if (initRet < 0) throw new InvalidOperationException($"av_hwdevice_ctx_init falló con código {initRet}.");

        HwDeviceRef = hwdevBuf;
    }

    private void InitComputePipeline()
    {
        ReadOnlySpan<byte> spvBytes = Shaders.ConvertSpv;
        VkShaderModule sm;
        fixed (byte* pSpv = spvBytes)
        {
            var smInfo = new VkShaderModuleCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO,
                codeSize = (nuint)spvBytes.Length,
                pCode = (uint*)pSpv
            };
            vkCreateShaderModule(Device, &smInfo, null, &sm);
        }
        _shaderModule = sm;

        var bindings = stackalloc VkDescriptorSetLayoutBinding[4];
        bindings[0] = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, descriptorCount = 1, stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT };
        bindings[1] = new VkDescriptorSetLayoutBinding { binding = 1, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, descriptorCount = 1, stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT };
        bindings[2] = new VkDescriptorSetLayoutBinding { binding = 2, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER, descriptorCount = 1, stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT };
        bindings[3] = new VkDescriptorSetLayoutBinding { binding = 3, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, descriptorCount = 1, stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT };

        var dslInfo = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO,
            bindingCount = 4,
            pBindings = bindings
        };
        VkDescriptorSetLayout dsl;
        vkCreateDescriptorSetLayout(Device, &dslInfo, null, &dsl);
        _descLayout = dsl;

        var pushConstant = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT,
            offset = 0,
            size = (uint)sizeof(PushConsts)
        };

        var pipeLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO,
            setLayoutCount = 1,
            pSetLayouts = &dsl,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushConstant
        };
        VkPipelineLayout pl;
        vkCreatePipelineLayout(Device, &pipeLayoutInfo, null, &pl);
        _pipeLayout = pl;

        sbyte* pEntry = (sbyte*)Marshal.StringToHGlobalAnsi("main");
        var pipeInfo = new VkComputePipelineCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO,
            stage = new VkPipelineShaderStageCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO,
                stage = VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT,
                module = _shaderModule,
                pName = pEntry
            },
            layout = _pipeLayout
        };
        VkPipeline pipe;
        vkCreateComputePipelines(Device, default, 1, &pipeInfo, null, &pipe);
        Marshal.FreeHGlobal((IntPtr)pEntry);
        _pipeline = pipe;

        var samplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO,
            magFilter = VkFilter.VK_FILTER_LINEAR,
            minFilter = VkFilter.VK_FILTER_LINEAR,
            mipmapMode = VkSamplerMipmapMode.VK_SAMPLER_MIPMAP_MODE_LINEAR,
            addressModeU = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            addressModeV = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
            addressModeW = VkSamplerAddressMode.VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE,
        };
        VkSampler samp;
        vkCreateSampler(Device, &samplerInfo, null, &samp);
        _sampler = samp;

        // Output image (480x270 RGBA8_UNORM)
        var outImgInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO,
            imageType = VkImageType.VK_IMAGE_TYPE_2D,
            format = VkFormat.VK_FORMAT_R8G8B8A8_UNORM,
            extent = new VkExtent3D { width = (uint)TargetWidth, height = (uint)TargetHeight, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.VK_SAMPLE_COUNT_1_BIT,
            tiling = VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
            usage = VkImageUsageFlags.VK_IMAGE_USAGE_STORAGE_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_SRC_BIT,
            sharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE
        };
        VkImage outImg;
        vkCreateImage(Device, &outImgInfo, null, &outImg);
        _outImage = outImg;

        VkMemoryRequirements outMemReq;
        vkGetImageMemoryRequirements(Device, _outImage, &outMemReq);
        var outAlloc = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            allocationSize = outMemReq.size,
            memoryTypeIndex = FindMemoryType(PhysDevice, outMemReq.memoryTypeBits, VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)
        };
        VkDeviceMemory outMem;
        vkAllocateMemory(Device, &outAlloc, null, &outMem);
        _outMem = outMem;
        vkBindImageMemory(Device, _outImage, _outMem, 0);

        var outViewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            image = _outImage,
            viewType = VkImageViewType.VK_IMAGE_VIEW_TYPE_2D,
            format = VkFormat.VK_FORMAT_R8G8B8A8_UNORM,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                levelCount = 1,
                layerCount = 1
            }
        };
        VkImageView outV;
        vkCreateImageView(Device, &outViewInfo, null, &outV);
        _outView = outV;

        // Staging buffer
        var bufInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
            size = TargetBufferSize,
            usage = VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT,
            sharingMode = VkSharingMode.VK_SHARING_MODE_EXCLUSIVE
        };
        VkBuffer sBuf;
        vkCreateBuffer(Device, &bufInfo, null, &sBuf);
        _stagingBuf = sBuf;

        VkMemoryRequirements bufReq;
        vkGetBufferMemoryRequirements(Device, _stagingBuf, &bufReq);
        var bufAlloc = new VkMemoryAllocateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
            allocationSize = bufReq.size,
            memoryTypeIndex = FindMemoryType(PhysDevice, bufReq.memoryTypeBits, VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)
        };
        VkDeviceMemory sMem;
        vkAllocateMemory(Device, &bufAlloc, null, &sMem);
        _stagingMem = sMem;
        vkBindBufferMemory(Device, _stagingBuf, _stagingMem, 0);

        // Descriptors
        var poolSizes = stackalloc VkDescriptorPoolSize[3];
        poolSizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, descriptorCount = 2 };
        poolSizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER, descriptorCount = 1 };
        poolSizes[2] = new VkDescriptorPoolSize { type = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, descriptorCount = 1 };

        var poolInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO,
            maxSets = 1,
            poolSizeCount = 3,
            pPoolSizes = poolSizes
        };
        VkDescriptorPool dp;
        vkCreateDescriptorPool(Device, &poolInfo, null, &dp);
        _descPool = dp;

        VkDescriptorSet ds;
        var setAlloc = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO,
            descriptorPool = _descPool,
            descriptorSetCount = 1,
            pSetLayouts = &dsl
        };
        vkAllocateDescriptorSets(Device, &setAlloc, &ds);
        _descSet = ds;

        // Command pool, buffer, fence
        var cmdPoolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
            queueFamilyIndex = (uint)GfxQueueFamily,
            flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT
        };
        VkCommandPool cp;
        vkCreateCommandPool(Device, &cmdPoolInfo, null, &cp);
        _cmdPool = cp;

        var cmdAlloc = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
            commandPool = _cmdPool,
            level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
            commandBufferCount = 1
        };
        VkCommandBuffer cb;
        vkAllocateCommandBuffers(Device, &cmdAlloc, &cb);
        _cmd = cb;

        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        VkFence f;
        vkCreateFence(Device, &fenceInfo, null, &f);
        _fence = f;
    }

    public void ProcessFrame(AVFrame* hwFrame, bool readbackToCpu, byte[]? cpuRgbaBuffer)
    {
        var vkf = (FfmpegInterop.AVVkFrame*)hwFrame->data[0];
        var hwfc = (AVHWFramesContext*)hwFrame->hw_frames_ctx->data;
        var vkfc = (FfmpegInterop.AVVulkanFramesContext*)hwfc->hwctx;

        if (vkfc->lock_frame != null)
        {
            vkfc->lock_frame(hwfc, vkf);
        }

        VkImage srcImg = *(VkImage*)&vkf->img[0];
        bool isP010 = (hwfc->sw_format == AVPixelFormat.AV_PIX_FMT_P010LE);
        VkFormat fmt0 = isP010 ? VkFormat.VK_FORMAT_R16_UNORM : VkFormat.VK_FORMAT_R8_UNORM;
        VkFormat fmt1 = isP010 ? VkFormat.VK_FORMAT_R16G16_UNORM : VkFormat.VK_FORMAT_R8G8_UNORM;

        var view0Info = new VkImageViewCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            image = srcImg,
            viewType = VkImageViewType.VK_IMAGE_VIEW_TYPE_2D,
            format = fmt0,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_PLANE_0_BIT,
                levelCount = 1,
                layerCount = 1
            }
        };
        VkImageView view0;
        vkCreateImageView(Device, &view0Info, null, &view0);

        var view1Info = new VkImageViewCreateInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO,
            image = srcImg,
            viewType = VkImageViewType.VK_IMAGE_VIEW_TYPE_2D,
            format = fmt1,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_PLANE_1_BIT,
                levelCount = 1,
                layerCount = 1
            }
        };
        VkImageView view1;
        vkCreateImageView(Device, &view1Info, null, &view1);

        // Update descriptors
        var imgInfo0 = new VkDescriptorImageInfo { imageView = view0, imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
        var imgInfo1 = new VkDescriptorImageInfo { imageView = view1, imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL };
        var sampInfo = new VkDescriptorImageInfo { sampler = _sampler };
        var outImgInfoDesc = new VkDescriptorImageInfo { imageView = _outView, imageLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL };

        _imgWrites[0] = new VkWriteDescriptorSet { sType = VkStructureType.VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, dstSet = _descSet, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, pImageInfo = &imgInfo0 };
        _imgWrites[1] = new VkWriteDescriptorSet { sType = VkStructureType.VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, dstSet = _descSet, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE, pImageInfo = &imgInfo1 };
        _imgWrites[2] = new VkWriteDescriptorSet { sType = VkStructureType.VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, dstSet = _descSet, dstBinding = 2, descriptorCount = 1, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_SAMPLER, pImageInfo = &sampInfo };
        _imgWrites[3] = new VkWriteDescriptorSet { sType = VkStructureType.VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET, dstSet = _descSet, dstBinding = 3, descriptorCount = 1, descriptorType = VkDescriptorType.VK_DESCRIPTOR_TYPE_STORAGE_IMAGE, pImageInfo = &outImgInfoDesc };
        vkUpdateDescriptorSets(Device, 4, _imgWrites, 0, null);

        var cmdBegin = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
            flags = VkCommandBufferUsageFlags.VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT
        };
        vkBeginCommandBuffer(_cmd, &cmdBegin);

        var barrierSrc = new VkImageMemoryBarrier
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            oldLayout = (VkImageLayout)vkf->layout[0],
            newLayout = VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            srcAccessMask = (VkAccessFlags)vkf->access[0],
            dstAccessMask = VkAccessFlags.VK_ACCESS_SHADER_READ_BIT,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = srcImg,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 }
        };

        var barrierOut = new VkImageMemoryBarrier
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
            newLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
            srcAccessMask = 0,
            dstAccessMask = VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = _outImage,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 }
        };

        _barriersInit[0] = barrierSrc;
        _barriersInit[1] = barrierOut;
        vkCmdPipelineBarrier(_cmd,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            0, 0, null, 0, null, 2, _barriersInit);

        vkCmdBindPipeline(_cmd, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_COMPUTE, _pipeline);
        VkDescriptorSet ds = _descSet;
        vkCmdBindDescriptorSets(_cmd, VkPipelineBindPoint.VK_PIPELINE_BIND_POINT_COMPUTE, _pipeLayout, 0, 1, &ds, 0, null);

        float maxU = (hwfc->width > 0 && hwFrame->width > 0) ? (float)hwFrame->width / hwfc->width : 1.0f;
        float maxV = (hwfc->height > 0 && hwFrame->height > 0) ? (float)hwFrame->height / hwfc->height : 1.0f;

        PushConsts push = new PushConsts
        {
            outWidth = (uint)TargetWidth,
            outHeight = (uint)TargetHeight,
            is10Bit = isP010 ? 1u : 0u,
            isFullRange = 0,
            maxU = maxU,
            maxV = maxV
        };
        vkCmdPushConstants(_cmd, _pipeLayout, VkShaderStageFlags.VK_SHADER_STAGE_COMPUTE_BIT, 0, (uint)sizeof(PushConsts), &push);
        vkCmdDispatch(_cmd, (uint)((TargetWidth + 15) / 16), (uint)((TargetHeight + 15) / 16), 1);

        if (readbackToCpu)
        {
            var barrierOutCopy = new VkImageMemoryBarrier
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_GENERAL,
                newLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                srcAccessMask = VkAccessFlags.VK_ACCESS_SHADER_WRITE_BIT,
                dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                image = _outImage,
                subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 }
            };
            vkCmdPipelineBarrier(_cmd,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                0, 0, null, 0, null, 1, &barrierOutCopy);

            var copyRegion = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, layerCount = 1 },
                imageExtent = new VkExtent3D { width = (uint)TargetWidth, height = (uint)TargetHeight, depth = 1 }
            };
            vkCmdCopyImageToBuffer(_cmd, _outImage, VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, _stagingBuf, 1, &copyRegion);
        }

        var barrierRestore = new VkImageMemoryBarrier
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
            oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
            newLayout = (VkImageLayout)vkf->layout[0],
            srcAccessMask = VkAccessFlags.VK_ACCESS_SHADER_READ_BIT,
            dstAccessMask = (VkAccessFlags)vkf->access[0],
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = srcImg,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, levelCount = 1, layerCount = 1 }
        };
        vkCmdPipelineBarrier(_cmd,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
            VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
            0, 0, null, 0, null, 1, &barrierRestore);

        vkEndCommandBuffer(_cmd);

        ulong waitVal = vkf->sem_value[0];
        ulong sigVal = waitVal + 1;
        VkSemaphore sem = *(VkSemaphore*)&vkf->sem[0];

        var timelineInfo = new VkTimelineSemaphoreSubmitInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_TIMELINE_SEMAPHORE_SUBMIT_INFO,
            waitSemaphoreValueCount = 1,
            pWaitSemaphoreValues = &waitVal,
            signalSemaphoreValueCount = 1,
            pSignalSemaphoreValues = &sigVal
        };

        VkPipelineStageFlags waitStage = VkPipelineStageFlags.VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT;
        VkCommandBuffer cb = _cmd;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO,
            pNext = &timelineInfo,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &sem,
            pWaitDstStageMask = &waitStage,
            commandBufferCount = 1,
            pCommandBuffers = &cb,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &sem
        };

        VkFence f = _fence;
        vkResetFences(Device, 1, &f);
        vkQueueSubmit(GfxQueue, 1, &submit, _fence);
        vkf->sem_value[0] = sigVal;

        if (vkfc->unlock_frame != null)
        {
            vkfc->unlock_frame(hwfc, vkf);
        }

        vkWaitForFences(Device, 1, &f, 1, ulong.MaxValue);

        if (readbackToCpu && cpuRgbaBuffer != null)
        {
            void* mapped;
            vkMapMemory(Device, _stagingMem, 0, TargetBufferSize, 0, &mapped);
            Marshal.Copy((IntPtr)mapped, cpuRgbaBuffer, 0, cpuRgbaBuffer.Length);
            vkUnmapMemory(Device, _stagingMem);
        }

        vkDestroyImageView(Device, view0, null);
        vkDestroyImageView(Device, view1, null);
    }

    private static uint FindMemoryType(VkPhysicalDevice physDev, uint typeFilter, VkMemoryPropertyFlags properties)
    {
        VkPhysicalDeviceMemoryProperties memProperties;
        vkGetPhysicalDeviceMemoryProperties(physDev, &memProperties);
        for (uint i = 0; i < memProperties.memoryTypeCount; i++)
        {
            if ((typeFilter & (1 << (int)i)) != 0 && (memProperties.memoryTypes[(int)i].propertyFlags & properties) == properties)
            {
                return i;
            }
        }
        throw new InvalidOperationException("No se encontró un tipo de memoria Vulkan compatible.");
    }

    public void Dispose()
    {
        if (Device != default)
        {
            vkDeviceWaitIdle(Device);

            if (_fence != default) { vkDestroyFence(Device, _fence, null); _fence = default; }
            if (_cmdPool != default) { vkDestroyCommandPool(Device, _cmdPool, null); _cmdPool = default; }
            if (_descPool != default) { vkDestroyDescriptorPool(Device, _descPool, null); _descPool = default; }
            if (_stagingBuf != default) { vkDestroyBuffer(Device, _stagingBuf, null); _stagingBuf = default; }
            if (_stagingMem != default) { vkFreeMemory(Device, _stagingMem, null); _stagingMem = default; }
            if (_outView != default) { vkDestroyImageView(Device, _outView, null); _outView = default; }
            if (_outImage != default) { vkDestroyImage(Device, _outImage, null); _outImage = default; }
            if (_outMem != default) { vkFreeMemory(Device, _outMem, null); _outMem = default; }
            if (_sampler != default) { vkDestroySampler(Device, _sampler, null); _sampler = default; }
            if (_pipeline != default) { vkDestroyPipeline(Device, _pipeline, null); _pipeline = default; }
            if (_pipeLayout != default) { vkDestroyPipelineLayout(Device, _pipeLayout, null); _pipeLayout = default; }
            if (_descLayout != default) { vkDestroyDescriptorSetLayout(Device, _descLayout, null); _descLayout = default; }
            if (_shaderModule != default) { vkDestroyShaderModule(Device, _shaderModule, null); _shaderModule = default; }

            if (HwDeviceRef != null)
            {
                AVBufferRef* buf = HwDeviceRef;
                ffmpeg.av_buffer_unref(&buf);
                HwDeviceRef = null;
            }

            vkDestroyDevice(Device, null);
            Device = default;
        }

        if (Instance != default)
        {
            vkDestroyInstance(Instance, null);
            Instance = default;
        }

        foreach (var p in _allocatedDevExtStrings)
        {
            Marshal.FreeHGlobal(p);
        }
        _allocatedDevExtStrings.Clear();

        if (_devExtPtrsArray != null)
        {
            NativeMemory.Free(_devExtPtrsArray);
            _devExtPtrsArray = null;
        }

        NativeMemory.Free(_imgWrites);
        NativeMemory.Free(_barriersInit);
    }
}
