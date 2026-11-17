using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Rodaje.Render.Shaders;
using Rodaje.Render.Spirv;
using TerraFX.Interop.Vulkan;
using static TerraFX.Interop.Vulkan.Vulkan;
using static TerraFX.Interop.Vulkan.VkStructureType;

namespace Rodaje.Render.Gpu.Vulkan;

/// <summary>
/// <see cref="IGpu"/> sobre Vulkan 1.3 (Linux y macOS; también corre en Windows con un controlador Vulkan). Un cuadro es un búfer de
/// órdenes que se envía al cerrarse; los recursos que se sueltan esperan a la valla del último envío que pudo usarlos (reglas de
/// <c>docs/igpu.md</c>). Los sombreadores son el SPIR-V del manifiesto, incrustado en <c>Rodaje.Render.Spirv</c>.
/// </summary>
public sealed unsafe partial class VulkanGpu : IGpu
{
    /// <summary>Subcadena del nombre de la GPU que se quiere (por ejemplo <c>llvmpipe</c>).</summary>
    public const string AdapterVariable = "RODAJE_VK_GPU";

    /// <summary>Con 1, carga <c>VK_LAYER_KHRONOS_validation</c> si está instalada.</summary>
    public const string ValidationVariable = "RODAJE_VK_VALIDACION";

    private readonly VkInstance _instance;
    private readonly VkPhysicalDevice _physical;
    private readonly VkDevice _device;
    private readonly VkQueue _queue;
    private readonly uint _family;
    private readonly VkPhysicalDeviceMemoryProperties _memory;
    private readonly object _frameLock = new();
    private readonly object _contextLock = new();
    private readonly object _garbageLock = new();
    private readonly Stack<FrameContext> _free = new();
    private readonly Queue<FrameContext> _inFlight = new();
    private readonly List<(long Serial, Action Destroy)> _garbage = [];
    private readonly ImmediateUploader _immediate;
    private VulkanFrame? _open;
    private long _lastSerial;
    private long _completed;
    private bool _lost;
    private bool _disposed;

    private VulkanGpu(VkInstance instance, VkPhysicalDevice physical, uint family, in VkPhysicalDeviceProperties properties, bool storageWithoutFormat,
        bool depthBiasClamp)
    {
        _instance = instance;
        _physical = physical;
        _family = family;
        Limits = properties.limits;
        VkPhysicalDeviceMemoryProperties memory;
        vkGetPhysicalDeviceMemoryProperties(physical, &memory);
        _memory = memory;
        DepthBiasClampSupported = depthBiasClamp;

        float priority = 1;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
            queueFamilyIndex = family,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        var features13 = new VkPhysicalDeviceVulkan13Features { sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES, dynamicRendering = VK_TRUE };
        var features = new VkPhysicalDeviceFeatures2 { sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, pNext = &features13 };
        if (storageWithoutFormat)
        {
            features.features.shaderStorageImageReadWithoutFormat = VK_TRUE;
            features.features.shaderStorageImageWriteWithoutFormat = VK_TRUE;
        }

        if (depthBiasClamp)
        {
            features.features.depthBiasClamp = VK_TRUE;
        }

        var deviceInfo = new VkDeviceCreateInfo
        {
            sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
            pNext = &features,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
        };
        VkDevice device;
        CheckStatic(vkCreateDevice(physical, &deviceInfo, null, &device), "vkCreateDevice");
        _device = device;
        VkQueue queue;
        vkGetDeviceQueue(device, family, 0, &queue);
        _queue = queue;

        bool software = properties.deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_CPU;
        Caps = new GpuCaps
        {
            Adapter = NameOf(properties),
            IsSoftware = software,
            MaxTextureSize = (int)properties.limits.maxImageDimension2D,
            Raster = true,
            DepthReadback = true,
            MaxSamples = MaxSamplesFor,
            CanBlend = CanBlendTo,
        };
        _immediate = new ImmediateUploader(this);
        Defaults = new DefaultResources(this);
    }

    public GpuCaps Caps { get; }

    public bool IsLost => _lost;

    /// <summary>Candado de la cola: lo toma todo envío (también el de FFmpeg cuando decodifique en este dispositivo).</summary>
    public object QueueLock { get; } = new();

    internal VkDevice Device => _device;

    internal VkQueue Queue => _queue;

    internal uint QueueFamily => _family;

    internal VkPhysicalDeviceLimits Limits { get; }

    internal bool DepthBiasClampSupported { get; }

    internal DefaultResources Defaults { get; }

    /// <summary>Un dispositivo Vulkan 1.3; <paramref name="preferSoftware"/> elige el de la CPU (lavapipe) si lo hay.</summary>
    /// <exception cref="RenderException">No hay cargador de Vulkan o ninguna GPU sirve.</exception>
    public static VulkanGpu Create(bool preferSoftware = false) =>
        TryCreate(preferSoftware, out VulkanGpu? gpu, out string? reason) ? gpu : throw new RenderException(reason);

    public static bool TryCreate(bool preferSoftware, [NotNullWhen(true)] out VulkanGpu? gpu, [NotNullWhen(false)] out string? reason)
    {
        gpu = null;
        VkInstance instance = default;
        try
        {
            instance = CreateInstance();
            string? filter = Environment.GetEnvironmentVariable(AdapterVariable);
            if (!Choose(instance, preferSoftware, filter, out VkPhysicalDevice physical, out uint family, out VkPhysicalDeviceProperties properties, out reason))
            {
                vkDestroyInstance(instance, null);
                return false;
            }

            VkPhysicalDeviceFeatures supported;
            vkGetPhysicalDeviceFeatures(physical, &supported);
            bool storage = supported.shaderStorageImageReadWithoutFormat != 0 && supported.shaderStorageImageWriteWithoutFormat != 0;
            gpu = new VulkanGpu(instance, physical, family, properties, storage, supported.depthBiasClamp != 0);
            reason = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            reason = "Sin cargador de Vulkan (libvulkan.so.1 o vulkan-1.dll).";
        }
        catch (EntryPointNotFoundException e)
        {
            reason = "El cargador de Vulkan es demasiado viejo: " + e.Message;
        }
        catch (RenderException e)
        {
            reason = e.Message;
        }

        if (instance != default)
        {
            vkDestroyInstance(instance, null);
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (QueueLock)
        {
            vkDeviceWaitIdle(_device);
        }

        Poll();
        lock (_garbageLock)
        {
            foreach ((long _, Action destroy) in _garbage)
            {
                destroy();
            }

            _garbage.Clear();
        }

        Defaults.Destroy();
        _immediate.Destroy();
        lock (_contextLock)
        {
            foreach (FrameContext context in _free.Concat(_inFlight))
            {
                context.Destroy(this);
            }

            _free.Clear();
            _inFlight.Clear();
        }

        vkDestroyDevice(_device, null);
        vkDestroyInstance(_instance, null);
    }

    public IGpuFrame BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Monitor.Enter(_frameLock);
        try
        {
            if (_open is { } open)
            {
                // Un cuadro dentro de otro en el mismo hilo (como en D3D11): se graba en el mismo búfer.
                open.Depth++;
                return open;
            }

            return _open = new VulkanFrame(this, AcquireContext());
        }
        catch
        {
            Monitor.Exit(_frameLock);
            throw;
        }
    }

    internal void LeaveNested() => Monitor.Exit(_frameLock);

    internal void CloseFrame(VulkanFrame frame)
    {
        try
        {
            Submit(frame.Context);
        }
        finally
        {
            _open = null;
            Monitor.Exit(_frameLock);
        }
    }

    public GpuTexture CreateTexture(in GpuTextureDesc desc) => VulkanTexture.Create(this, desc, default);

    public GpuTexture CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData) => VulkanTexture.Create(this, desc, initialData);

    public GpuBuffer CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData = default) => VulkanBuffer.Create(this, desc, initialData);

    public GpuSampler CreateSampler(in GpuSamplerDesc desc) => new VulkanSampler(this, desc);

    public GpuShader CreateShader(ShaderVariant variant, ShaderCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(variant);
        string id = ShaderManifest.Id(variant);
        using Stream stream = SpirvShaders.Open(id) ?? throw new RenderException($"No hay SPIR-V para «{id}»: build\\shaders.ps1 generar.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return CreateShader(memory.ToArray(), id);
    }

    /// <summary>Con SPIR-V ya compilado (con los desplazamientos de registro del manifiesto); el tipo sale del punto de entrada.</summary>
    public GpuShader CreateShader(byte[] spirv, string id)
    {
        ArgumentNullException.ThrowIfNull(spirv);
        return new VulkanShader(this, SpirvModule.Read(spirv)) { Id = id };
    }

    public GpuPipeline CreatePipeline(GpuPipelineDesc desc) => new VulkanPipeline(this, desc);

    public GpuTimerQuery CreateTimer() => new VulkanTimerQuery(this);

    public GpuReadback CreateReadback(int width, int height, GpuFormat format) =>
        new VulkanReadback(this, width, height, format, width * height * GpuFormats.BytesPerPixel(format));

    public GpuReadback CreateReadback(int bytes) => new VulkanReadback(this, bytes, 1, GpuFormat.Unknown, bytes);

    /// <summary>Las superficies Vulkan del decodificador llegan con la fase 9; hasta entonces, una caché vacía.</summary>
    public GpuSurfaceCache CreateSurfaceCache(TimeProvider? time = null) => new VulkanSurfaceCache();

    // Envíos y vallas

    internal bool IsComplete(long serial)
    {
        if (serial <= Volatile.Read(ref _completed))
        {
            return true;
        }

        Poll();
        return serial <= Volatile.Read(ref _completed);
    }

    /// <summary>Espera a que termine el envío <paramref name="serial"/>; si es el cuadro que este hilo graba, lo envía antes.</summary>
    internal bool Wait(long serial, TimeSpan timeout)
    {
        if (IsComplete(serial))
        {
            return true;
        }

        if (Monitor.IsEntered(_frameLock) && _open is { } open && open.Context.Serial <= serial)
        {
            open.Flush();
        }

        long deadline = Environment.TickCount64 + (long)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        while (true)
        {
            FrameContext? waitOn = null;
            lock (_contextLock)
            {
                foreach (FrameContext context in _inFlight)
                {
                    if (context.Serial >= serial)
                    {
                        waitOn = context;
                        break;
                    }
                }
            }

            long left = deadline - Environment.TickCount64;
            if (waitOn is not null)
            {
                VkFence fence = waitOn.Fence;
                VkResult result = vkWaitForFences(_device, 1, &fence, VK_TRUE, (ulong)Math.Max(0, left) * 1_000_000);
                if (result == VkResult.VK_ERROR_DEVICE_LOST)
                {
                    Check(result, "vkWaitForFences");
                }
            }
            else if (left > 0)
            {
                // Lo graba otro hilo y aún no lo envió.
                Thread.Yield();
            }

            if (IsComplete(serial))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }
        }
    }

    /// <summary>Destruye <paramref name="destroy"/> cuando termine todo lo enviado o grabado hasta ahora (regla 4).</summary>
    internal void Defer(Action destroy)
    {
        if (_disposed)
        {
            return;
        }

        long serial = Volatile.Read(ref _lastSerial);
        if (serial <= Volatile.Read(ref _completed))
        {
            destroy();
            return;
        }

        lock (_garbageLock)
        {
            _garbage.Add((serial, destroy));
        }
    }

    internal FrameContext AcquireContext()
    {
        Poll();
        FrameContext? context = null;
        lock (_contextLock)
        {
            _free.TryPop(out context);
        }

        context ??= new FrameContext(this);
        context.Begin(this, Interlocked.Increment(ref _lastSerial));
        return context;
    }

    internal void Submit(FrameContext context)
    {
        Check(vkEndCommandBuffer(context.Commands), "vkEndCommandBuffer");
        VkFence fence = context.Fence;
        Check(vkResetFences(_device, 1, &fence), "vkResetFences");
        VkCommandBuffer commands = context.Commands;
        var submit = new VkSubmitInfo { sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, commandBufferCount = 1, pCommandBuffers = &commands };
        VkResult result;
        lock (QueueLock)
        {
            result = vkQueueSubmit(_queue, 1, &submit, fence);
        }

        lock (_contextLock)
        {
            _inFlight.Enqueue(context);
        }

        Check(result, "vkQueueSubmit");
    }

    private void Poll()
    {
        lock (_contextLock)
        {
            while (_inFlight.TryPeek(out FrameContext? context) && vkGetFenceStatus(_device, context.Fence) == VkResult.VK_SUCCESS)
            {
                _inFlight.Dequeue();
                Volatile.Write(ref _completed, Math.Max(_completed, context.Serial));
                _free.Push(context);
            }
        }

        List<Action>? ready = null;
        lock (_garbageLock)
        {
            long completed = Volatile.Read(ref _completed);
            for (int i = _garbage.Count - 1; i >= 0; i--)
            {
                if (_garbage[i].Serial <= completed)
                {
                    (ready ??= []).Add(_garbage[i].Destroy);
                    _garbage.RemoveAt(i);
                }
            }
        }

        ready?.ForEach(static destroy => destroy());
    }

    // Memoria

    internal VkDeviceMemory Allocate(in VkMemoryRequirements requirements, VkMemoryPropertyFlags required, VkMemoryPropertyFlags preferred = 0)
    {
        int type = FindMemory(requirements.memoryTypeBits, required | preferred);
        if (type < 0)
        {
            type = FindMemory(requirements.memoryTypeBits, required);
        }

        if (type < 0)
        {
            throw new RenderException("La GPU no tiene un tipo de memoria que sirva.");
        }

        var info = new VkMemoryAllocateInfo { sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, allocationSize = requirements.size, memoryTypeIndex = (uint)type };
        VkDeviceMemory memory;
        Check(vkAllocateMemory(_device, &info, null, &memory), "vkAllocateMemory");
        return memory;
    }

    internal bool IsCoherent(VkMemoryRequirements requirements, VkMemoryPropertyFlags flags)
    {
        int type = FindMemory(requirements.memoryTypeBits, flags);
        return type >= 0 && (_memory.memoryTypes[type].propertyFlags & VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) != 0;
    }

    private int FindMemory(uint bits, VkMemoryPropertyFlags flags)
    {
        for (int i = 0; i < _memory.memoryTypeCount; i++)
        {
            if ((bits & (1u << i)) != 0 && (_memory.memoryTypes[i].propertyFlags & flags) == flags)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Un búfer con su memoria; si es visible desde la CPU, queda mapeado.</summary>
    internal (VkBuffer Buffer, VkDeviceMemory Memory, nint Mapped) CreateRawBuffer(ulong size, VkBufferUsageFlags usage, bool hostVisible,
        bool cached = false)
    {
        var info = new VkBufferCreateInfo { sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, size = Math.Max(size, 4), usage = usage };
        VkBuffer buffer;
        Check(vkCreateBuffer(_device, &info, null, &buffer), "vkCreateBuffer");
        VkMemoryRequirements requirements;
        vkGetBufferMemoryRequirements(_device, buffer, &requirements);
        VkMemoryPropertyFlags required = hostVisible
            ? VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT
            : VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT;
        VkMemoryPropertyFlags preferred = cached ? VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_CACHED_BIT : 0;
        VkDeviceMemory memory;
        try
        {
            memory = hostVisible || FindMemory(requirements.memoryTypeBits, required) >= 0
                ? Allocate(requirements, required, preferred)
                : Allocate(requirements, 0);
        }
        catch
        {
            vkDestroyBuffer(_device, buffer, null);
            throw;
        }

        Check(vkBindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
        void* mapped = null;
        if (hostVisible)
        {
            Check(vkMapMemory(_device, memory, 0, VK_WHOLE_SIZE, 0, &mapped), "vkMapMemory");
        }

        return (buffer, memory, (nint)mapped);
    }

    internal void Check(VkResult result, string operation)
    {
        if (result == VkResult.VK_ERROR_DEVICE_LOST)
        {
            _lost = true;
        }

        CheckStatic(result, operation);
    }

    internal static void CheckStatic(VkResult result, string operation)
    {
        if (result < 0)
        {
            string hint = result == VkResult.VK_ERROR_DEVICE_LOST ? " La GPU se perdió." : "";
            throw new RenderException(string.Create(CultureInfo.InvariantCulture, $"{operation} falló con {result}.{hint}"), (int)result);
        }
    }

    internal void Immediate(Action<VkCommandBuffer> record) => _immediate.Run(record);

    internal void UploadImmediate(ReadOnlySpan<byte> data, Action<VkCommandBuffer, VkBuffer, ulong> record) => _immediate.Upload(data, record);

    // Creación

    private static VkInstance CreateInstance()
    {
        bool validation = Environment.GetEnvironmentVariable(ValidationVariable) == "1" && HasLayer("VK_LAYER_KHRONOS_validation");
        fixed (byte* name = "Rodaje\0"u8)
        fixed (byte* layer = "VK_LAYER_KHRONOS_validation\0"u8)
        {
            var app = new VkApplicationInfo
            {
                sType = VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pApplicationName = (sbyte*)name,
                pEngineName = (sbyte*)name,
                apiVersion = VK_API_VERSION_1_3,
            };
            sbyte* layers = (sbyte*)layer;
            var info = new VkInstanceCreateInfo
            {
                sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = &app,
                enabledLayerCount = validation ? 1u : 0u,
                ppEnabledLayerNames = validation ? &layers : null,
            };
            VkInstance instance;
            CheckStatic(vkCreateInstance(&info, null, &instance), "vkCreateInstance");
            return instance;
        }
    }

    private static bool HasLayer(string wanted)
    {
        uint count = 0;
        if (vkEnumerateInstanceLayerProperties(&count, null) != VkResult.VK_SUCCESS || count == 0)
        {
            return false;
        }

        var layers = new VkLayerProperties[count];
        fixed (VkLayerProperties* p = layers)
        {
            vkEnumerateInstanceLayerProperties(&count, p);
            for (int i = 0; i < count; i++)
            {
                if (Utf8(&p[i].layerName.e0) == wanted)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Choose(VkInstance instance, bool preferSoftware, string? filter, out VkPhysicalDevice chosen, out uint family,
        out VkPhysicalDeviceProperties properties, [NotNullWhen(false)] out string? reason)
    {
        chosen = default;
        family = 0;
        properties = default;
        uint count = 0;
        CheckStatic(vkEnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices");
        var devices = new VkPhysicalDevice[count];
        fixed (VkPhysicalDevice* p = devices)
        {
            CheckStatic(vkEnumeratePhysicalDevices(instance, &count, p), "vkEnumeratePhysicalDevices");
        }

        int best = int.MaxValue;
        var seen = new List<string>();
        foreach (VkPhysicalDevice device in devices)
        {
            VkPhysicalDeviceProperties props;
            vkGetPhysicalDeviceProperties(device, &props);
            string name = NameOf(props);
            seen.Add($"{name} ({(props.apiVersion >> 22) & 0x7F}.{(props.apiVersion >> 12) & 0x3FF})");
            if (props.apiVersion < VK_API_VERSION_1_3 || (filter is { Length: > 0 } && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                || FindFamily(device) is not uint queueFamily || !SupportsDynamicRendering(device))
            {
                continue;
            }

            bool cpu = props.deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_CPU;
            int rank = props.deviceType switch
            {
                VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU => 0,
                VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU => 1,
                VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU => 2,
                _ => 3,
            };
            if (preferSoftware)
            {
                rank = cpu ? -1 : rank;
            }

            if (rank < best)
            {
                best = rank;
                chosen = device;
                family = queueFamily;
                properties = props;
            }
        }

        if (best == int.MaxValue)
        {
            reason = seen.Count == 0
                ? "No hay ninguna GPU con Vulkan."
                : $"Ninguna GPU con Vulkan 1.3, gráficos y cómputo{(filter is { Length: > 0 } ? $" y «{filter}» en el nombre" : "")}: {string.Join(", ", seen)}.";
            return false;
        }

        reason = null;
        return true;
    }

    private static uint? FindFamily(VkPhysicalDevice device)
    {
        uint count = 0;
        vkGetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new VkQueueFamilyProperties[count];
        fixed (VkQueueFamilyProperties* p = families)
        {
            vkGetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        }

        const VkQueueFlags wanted = VkQueueFlags.VK_QUEUE_GRAPHICS_BIT | VkQueueFlags.VK_QUEUE_COMPUTE_BIT;
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].queueFlags & wanted) == wanted)
            {
                return i;
            }
        }

        return null;
    }

    private static bool SupportsDynamicRendering(VkPhysicalDevice device)
    {
        var features13 = new VkPhysicalDeviceVulkan13Features { sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES };
        var features = new VkPhysicalDeviceFeatures2 { sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2, pNext = &features13 };
        vkGetPhysicalDeviceFeatures2(device, &features);
        return features13.dynamicRendering != 0;
    }

    private static string NameOf(in VkPhysicalDeviceProperties properties)
    {
        fixed (sbyte* name = &properties.deviceName.e0)
        {
            return Utf8(name);
        }
    }

    private static string Utf8(sbyte* text) => Marshal.PtrToStringUTF8((nint)text) ?? "";

    private int MaxSamplesFor(GpuFormat format)
    {
        VkSampleCountFlags counts = format == GpuFormat.D32Float ? Limits.framebufferDepthSampleCounts : Limits.framebufferColorSampleCounts;
        VkImageFormatProperties properties;
        if (vkGetPhysicalDeviceImageFormatProperties(_physical, VulkanFormats.Native(format), VkImageType.VK_IMAGE_TYPE_2D, VkImageTiling.VK_IMAGE_TILING_OPTIMAL,
            format == GpuFormat.D32Float ? VkImageUsageFlags.VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT : VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
            0, &properties) == VkResult.VK_SUCCESS)
        {
            counts &= properties.sampleCounts;
        }

        int max = 1;
        for (int samples = 2; samples <= 8; samples *= 2)
        {
            if (((uint)counts & (uint)samples) != 0)
            {
                max = samples;
            }
        }

        return max;
    }

    private bool CanBlendTo(GpuFormat format)
    {
        VkFormatProperties properties;
        vkGetPhysicalDeviceFormatProperties(_physical, VulkanFormats.Native(format), &properties);
        return (properties.optimalTilingFeatures & VkFormatFeatureFlags.VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BLEND_BIT) != 0;
    }

    internal VkFormatFeatureFlags FormatFeatures(VkFormat format)
    {
        VkFormatProperties properties;
        vkGetPhysicalDeviceFormatProperties(_physical, format, &properties);
        return properties.optimalTilingFeatures;
    }
}
