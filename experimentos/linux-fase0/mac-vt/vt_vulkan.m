// Parte (c) de la fase 0: VideoToolbox (por el hwaccel de FFmpeg) -> IOSurface -> MTLTexture por plano -> VkImage (MoltenVK,
// VK_EXT_metal_objects). Cada plano se lee desde Vulkan (vkCmdCopyImageToBuffer) y se compara byte a byte con el
// CVPixelBuffer: si coincide, el VkImage es la misma superficie que entregó el decodificador, sin copia intermedia.
// Uso: vt_vulkan <video> [segundos]   (repite el video hasta cumplir los segundos; escribe una línea JSON al final)
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <CoreVideo/CoreVideo.h>
#import <IOSurface/IOSurface.h>
#include <mach/mach.h>
#include <vulkan/vulkan.h>
#include <vulkan/vulkan_metal.h>
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/pixdesc.h>

#define CHECK(x, que) do { VkResult r_ = (x); if (r_ != VK_SUCCESS) { fprintf(stderr, "Vulkan: %s falló con %d\n", que, r_); exit(2); } } while (0)

static VkInstance instance;
static VkPhysicalDevice gpu;
static VkDevice device;
static VkQueue queue;
static VkCommandPool pool;
static VkBuffer staging;
static VkDeviceMemory stagingMemory;
static uint8_t *stagingBytes;
static const VkDeviceSize StagingSize = 64u << 20;
static id<MTLDevice> metal;

static double now_ms(void) { return [NSDate timeIntervalSinceReferenceDate] * 1000.0; }

static double resident_mb(void)
{
    struct mach_task_basic_info info;
    mach_msg_type_number_t n = MACH_TASK_BASIC_INFO_COUNT;
    return task_info(mach_task_self(), MACH_TASK_BASIC_INFO, (task_info_t)&info, &n) == KERN_SUCCESS ? info.resident_size / 1048576.0 : -1;
}

static void vulkan_start(void)
{
    const char *instanceExt[] = { VK_EXT_METAL_OBJECTS_EXTENSION_NAME };
    VkApplicationInfo app = { .sType = VK_STRUCTURE_TYPE_APPLICATION_INFO, .apiVersion = VK_API_VERSION_1_2 };
    // El dispositivo de Metal se exporta después: hay que pedirlo al crear la instancia y el dispositivo.
    VkExportMetalObjectCreateInfoEXT exportDevice = { .sType = VK_STRUCTURE_TYPE_EXPORT_METAL_OBJECT_CREATE_INFO_EXT, .exportObjectType = VK_EXPORT_METAL_OBJECT_TYPE_METAL_DEVICE_BIT_EXT };
    VkInstanceCreateInfo ici = { .sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO, .pNext = &exportDevice, .pApplicationInfo = &app };
    CHECK(vkCreateInstance(&ici, NULL, &instance), "vkCreateInstance");
    uint32_t n = 1;
    VkResult r = vkEnumeratePhysicalDevices(instance, &n, &gpu);
    if ((r != VK_SUCCESS && r != VK_INCOMPLETE) || n == 0) { fprintf(stderr, "Vulkan: no hay dispositivo físico (%d)\n", r); exit(2); }
    VkPhysicalDeviceProperties props;
    vkGetPhysicalDeviceProperties(gpu, &props);
    fprintf(stderr, "GPU: %s (Vulkan %u.%u.%u)\n", props.deviceName, VK_VERSION_MAJOR(props.apiVersion), VK_VERSION_MINOR(props.apiVersion), VK_VERSION_PATCH(props.apiVersion));

    uint32_t families = 0, family = UINT32_MAX;
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &families, NULL);
    VkQueueFamilyProperties qf[16];
    if (families > 16) { families = 16; }
    vkGetPhysicalDeviceQueueFamilyProperties(gpu, &families, qf);
    for (uint32_t i = 0; i < families; i++) { if (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { family = i; break; } }
    float priority = 1;
    VkDeviceQueueCreateInfo qci = { .sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO, .queueFamilyIndex = family, .queueCount = 1, .pQueuePriorities = &priority };
    VkDeviceCreateInfo dci = { .sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO, .pNext = &exportDevice, .queueCreateInfoCount = 1, .pQueueCreateInfos = &qci,
        .enabledExtensionCount = 1, .ppEnabledExtensionNames = instanceExt };
    CHECK(vkCreateDevice(gpu, &dci, NULL, &device), "vkCreateDevice");
    vkGetDeviceQueue(device, family, 0, &queue);

    VkExportMetalDeviceInfoEXT deviceInfo = { .sType = VK_STRUCTURE_TYPE_EXPORT_METAL_DEVICE_INFO_EXT };
    VkExportMetalObjectsInfoEXT objects = { .sType = VK_STRUCTURE_TYPE_EXPORT_METAL_OBJECTS_INFO_EXT, .pNext = &deviceInfo };
    vkExportMetalObjectsEXT(device, &objects);
    metal = deviceInfo.mtlDevice;
    if (metal == nil) { fprintf(stderr, "MoltenVK no exportó su MTLDevice\n"); exit(2); }
    fprintf(stderr, "Metal: %s\n", metal.name.UTF8String);

    VkCommandPoolCreateInfo pci = { .sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO, .flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT, .queueFamilyIndex = family };
    CHECK(vkCreateCommandPool(device, &pci, NULL, &pool), "vkCreateCommandPool");
    VkBufferCreateInfo bci = { .sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO, .size = StagingSize, .usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT };
    CHECK(vkCreateBuffer(device, &bci, NULL, &staging), "vkCreateBuffer");
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(device, staging, &req);
    VkPhysicalDeviceMemoryProperties mem;
    vkGetPhysicalDeviceMemoryProperties(gpu, &mem);
    uint32_t type = UINT32_MAX;
    const VkMemoryPropertyFlags want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    for (uint32_t i = 0; i < mem.memoryTypeCount; i++) { if ((req.memoryTypeBits & (1u << i)) && (mem.memoryTypes[i].propertyFlags & want) == want) { type = i; break; } }
    VkMemoryAllocateInfo mai = { .sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO, .allocationSize = req.size, .memoryTypeIndex = type };
    CHECK(vkAllocateMemory(device, &mai, NULL, &stagingMemory), "vkAllocateMemory");
    CHECK(vkBindBufferMemory(device, staging, stagingMemory, 0), "vkBindBufferMemory");
    CHECK(vkMapMemory(device, stagingMemory, 0, VK_WHOLE_SIZE, 0, (void **)&stagingBytes), "vkMapMemory");
}

// Envuelve un plano del IOSurface en un VkImage, lo lee desde Vulkan y lo compara con los bytes del CVPixelBuffer.
// Devuelve 1 si coincide, 0 si no, -1 si no se pudo crear la textura o la imagen.
static int check_plane(CVPixelBufferRef pixels, IOSurfaceRef surface, size_t plane, MTLPixelFormat metalFormat, VkFormat format, size_t bytesPerPixel)
{
    size_t width = CVPixelBufferGetWidthOfPlane(pixels, plane), height = CVPixelBufferGetHeightOfPlane(pixels, plane);
    MTLTextureDescriptor *descriptor = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:metalFormat width:width height:height mipmapped:NO];
    descriptor.usage = MTLTextureUsageShaderRead;
    id<MTLTexture> texture = [metal newTextureWithDescriptor:descriptor iosurface:surface plane:plane];
    if (texture == nil) { return -1; }

    VkImportMetalTextureInfoEXT import = { .sType = VK_STRUCTURE_TYPE_IMPORT_METAL_TEXTURE_INFO_EXT, .plane = VK_IMAGE_ASPECT_PLANE_0_BIT, .mtlTexture = texture };
    VkImageCreateInfo ici = { .sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO, .pNext = &import, .imageType = VK_IMAGE_TYPE_2D, .format = format,
        .extent = { (uint32_t)width, (uint32_t)height, 1 }, .mipLevels = 1, .arrayLayers = 1, .samples = VK_SAMPLE_COUNT_1_BIT, .tiling = VK_IMAGE_TILING_OPTIMAL,
        .usage = VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT, .initialLayout = VK_IMAGE_LAYOUT_UNDEFINED };
    VkImage image;
    if (vkCreateImage(device, &ici, NULL, &image) != VK_SUCCESS) { return -1; }
    // La vista que usaría un sampler del compositor.
    VkImageViewCreateInfo vci = { .sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO, .image = image, .viewType = VK_IMAGE_VIEW_TYPE_2D, .format = format,
        .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
    VkImageView view;
    if (vkCreateImageView(device, &vci, NULL, &view) != VK_SUCCESS) { vkDestroyImage(device, image, NULL); return -1; }

    VkCommandBufferAllocateInfo cai = { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO, .commandPool = pool, .level = VK_COMMAND_BUFFER_LEVEL_PRIMARY, .commandBufferCount = 1 };
    VkCommandBuffer cmd;
    CHECK(vkAllocateCommandBuffers(device, &cai, &cmd), "vkAllocateCommandBuffers");
    VkCommandBufferBeginInfo begin = { .sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO, .flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT };
    vkBeginCommandBuffer(cmd, &begin);
    // De UNDEFINED: Vulkan permite descartar el contenido; MoltenVK no lo hace (los layouts no existen en Metal).
    VkImageMemoryBarrier barrier = { .sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER, .dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT,
        .oldLayout = VK_IMAGE_LAYOUT_UNDEFINED, .newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, .srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
        .dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, .image = image, .subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 } };
    vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, NULL, 0, NULL, 1, &barrier);
    VkBufferImageCopy region = { .imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 }, .imageExtent = { (uint32_t)width, (uint32_t)height, 1 } };
    vkCmdCopyImageToBuffer(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, staging, 1, &region);
    vkEndCommandBuffer(cmd);
    VkSubmitInfo submit = { .sType = VK_STRUCTURE_TYPE_SUBMIT_INFO, .commandBufferCount = 1, .pCommandBuffers = &cmd };
    CHECK(vkQueueSubmit(queue, 1, &submit, VK_NULL_HANDLE), "vkQueueSubmit");
    CHECK(vkQueueWaitIdle(queue), "vkQueueWaitIdle");
    vkFreeCommandBuffers(device, pool, 1, &cmd);

    int equal = 1;
    CVPixelBufferLockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);
    const uint8_t *base = CVPixelBufferGetBaseAddressOfPlane(pixels, plane);
    size_t stride = CVPixelBufferGetBytesPerRowOfPlane(pixels, plane), row = width * bytesPerPixel;
    for (size_t y = 0; y < height && equal; y++) { equal = memcmp(base + y * stride, stagingBytes + y * row, row) == 0; }
    CVPixelBufferUnlockBaseAddress(pixels, kCVPixelBufferLock_ReadOnly);

    vkDestroyImageView(device, view, NULL);
    vkDestroyImage(device, image, NULL);
    return equal;
}

static enum AVPixelFormat pick_videotoolbox(AVCodecContext *c, const enum AVPixelFormat *formats)
{
    for (; *formats != AV_PIX_FMT_NONE; formats++) { if (*formats == AV_PIX_FMT_VIDEOTOOLBOX) { return *formats; } }
    return AV_PIX_FMT_NONE;
}

int main(int argc, char **argv)
{
    @autoreleasepool {
        if (argc < 2) { fprintf(stderr, "Uso: vt_vulkan <video> [segundos]\n"); return 64; }
        double seconds = argc > 2 ? atof(argv[2]) : 5;
        vulkan_start();

        AVBufferRef *hw = NULL;
        if (av_hwdevice_ctx_create(&hw, AV_HWDEVICE_TYPE_VIDEOTOOLBOX, NULL, NULL, 0) < 0) { fprintf(stderr, "FFmpeg no pudo crear el dispositivo VideoToolbox\n"); return 2; }
        long frames = 0, same = 0, different = 0, failed = 0, noSurface = 0, software = 0, loops = 0;
        OSType pixelType = 0;
        size_t frameWidth = 0, frameHeight = 0;
        double start = now_ms(), memoryStart = -1, memoryMiddle = -1, worst = 0, total = 0;

        while (now_ms() - start < seconds * 1000.0 || loops == 0) {
            AVFormatContext *input = NULL;
            if (avformat_open_input(&input, argv[1], NULL, NULL) < 0 || avformat_find_stream_info(input, NULL) < 0) { fprintf(stderr, "No se pudo abrir %s\n", argv[1]); return 2; }
            const AVCodec *codec = NULL;
            int stream = av_find_best_stream(input, AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            AVCodecContext *decoder = avcodec_alloc_context3(codec);
            avcodec_parameters_to_context(decoder, input->streams[stream]->codecpar);
            decoder->hw_device_ctx = av_buffer_ref(hw);
            decoder->get_format = pick_videotoolbox;
            if (avcodec_open2(decoder, codec, NULL) < 0) { fprintf(stderr, "No se pudo abrir el decodificador\n"); return 2; }
            AVPacket *packet = av_packet_alloc();
            AVFrame *frame = av_frame_alloc();
            int draining = 0;
            for (;;) {
                if (!draining) {
                    int read = av_read_frame(input, packet);
                    if (read < 0) { draining = 1; avcodec_send_packet(decoder, NULL); }
                    else { if (packet->stream_index == stream) { avcodec_send_packet(decoder, packet); } av_packet_unref(packet); }
                }
                int got = avcodec_receive_frame(decoder, frame);
                if (got == AVERROR(EAGAIN) && !draining) { continue; }
                if (got < 0) { break; }
                @autoreleasepool {
                    double t = now_ms();
                    frames++;
                    if (frame->format != AV_PIX_FMT_VIDEOTOOLBOX) { software++; av_frame_unref(frame); continue; }
                    CVPixelBufferRef pixels = (CVPixelBufferRef)frame->data[3];
                    IOSurfaceRef surface = CVPixelBufferGetIOSurface(pixels);
                    pixelType = CVPixelBufferGetPixelFormatType(pixels);
                    frameWidth = CVPixelBufferGetWidth(pixels);
                    frameHeight = CVPixelBufferGetHeight(pixels);
                    if (surface == NULL) { noSurface++; av_frame_unref(frame); continue; }
                    // 8 bits: '420v'/'420f' (NV12). 10 bits: 'x420'/'xf20' (P010, 16 bits por componente).
                    int wide = pixelType == kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange || pixelType == kCVPixelFormatType_420YpCbCr10BiPlanarFullRange;
                    int luma = check_plane(pixels, surface, 0, wide ? MTLPixelFormatR16Unorm : MTLPixelFormatR8Unorm, wide ? VK_FORMAT_R16_UNORM : VK_FORMAT_R8_UNORM, wide ? 2 : 1);
                    int chroma = check_plane(pixels, surface, 1, wide ? MTLPixelFormatRG16Unorm : MTLPixelFormatRG8Unorm, wide ? VK_FORMAT_R16G16_UNORM : VK_FORMAT_R8G8_UNORM, wide ? 4 : 2);
                    if (luma < 0 || chroma < 0) { failed++; } else if (luma && chroma) { same++; } else { different++; }
                    double took = now_ms() - t;
                    total += took;
                    if (took > worst) { worst = took; }
                    av_frame_unref(frame);
                }
                if (memoryStart < 0 && frames >= 30) { memoryStart = resident_mb(); }
                if (memoryMiddle < 0 && now_ms() - start > seconds * 500.0) { memoryMiddle = resident_mb(); }
            }
            av_frame_free(&frame);
            av_packet_free(&packet);
            avcodec_free_context(&decoder);
            avformat_close_input(&input);
            loops++;
        }

        char fourcc[5] = { (char)(pixelType >> 24), (char)(pixelType >> 16), (char)(pixelType >> 8), (char)pixelType, 0 };
        int ok = frames > 0 && same == frames;
        printf("{\"video\": \"%s\", \"metal\": \"%s\", \"formato\": \"%s\", \"ancho\": %zu, \"alto\": %zu, \"vueltas\": %ld, \"cuadros\": %ld, \"iguales\": %ld, "
               "\"distintos\": %ld, \"sin_textura_o_imagen\": %ld, \"sin_iosurface\": %ld, \"por_software\": %ld, \"ms_por_cuadro\": %.3f, \"ms_peor\": %.3f, "
               "\"memoria_mb_inicio\": %.1f, \"memoria_mb_mitad\": %.1f, \"memoria_mb_final\": %.1f, \"pasa\": %s}\n",
               argv[1], metal.name.UTF8String, fourcc, frameWidth, frameHeight, loops, frames, same, different, failed, noSurface, software,
               frames ? total / frames : 0, worst, memoryStart, memoryMiddle, resident_mb(), ok ? "true" : "false");
        return ok ? 0 : 1;
    }
}
