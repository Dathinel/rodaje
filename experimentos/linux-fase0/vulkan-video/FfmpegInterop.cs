using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;
using TerraFX.Interop.Vulkan;

namespace Rodaje.VulkanVideo;

public static unsafe class FfmpegInterop
{
    private static bool _initialized;
    private static string? _ffmpegDir;

    [StructLayout(LayoutKind.Sequential)]
    public struct AVVulkanDeviceQueueFamily
    {
        public int idx;
        public int num;
        public uint flags;
        public uint video_caps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AVVulkanDeviceContext
    {
        public void* alloc;
        public delegate* unmanaged<VkInstance, sbyte*, void*> get_proc_addr;
        public VkInstance inst;
        public VkPhysicalDevice phys_dev;
        public VkDevice act_dev;
        public VkPhysicalDeviceFeatures2 device_features;
        public byte** enabled_inst_extensions;
        public int nb_enabled_inst_extensions;
        public int _pad1;
        public byte** enabled_dev_extensions;
        public int nb_enabled_dev_extensions;
        public int _pad2;
        public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> lock_queue;
        public delegate* unmanaged<AVHWDeviceContext*, uint, uint, void> unlock_queue;
        public fixed byte qf_raw[64 * 16]; // 64 * sizeof(AVVulkanDeviceQueueFamily)
        public int nb_qf;
        public uint queue_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AVVkFrame
    {
        public fixed ulong img[8];
        public int tiling;
        public fixed ulong mem[8];
        public fixed ulong size[8];
        public uint flags;
        public fixed ulong access[8];
        public fixed int layout[8];
        public fixed ulong sem[8];
        public fixed ulong sem_value[8];
        public fixed long offset[8];
        public fixed uint queue_family[8];
        public void* @internal;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AVVulkanFramesContext
    {
        public int tiling;
        public uint usage;
        public void* create_pnext;
        public fixed ulong alloc_pnext[8];
        public ulong flags;
        public uint img_flags;
        public fixed int format[8];
        public int nb_layers;
        public delegate* unmanaged<AVHWFramesContext*, void*, void> lock_frame;
        public delegate* unmanaged<AVHWFramesContext*, void*, void> unlock_frame;
    }

    public static string Initialize(string? explicitDirectory = null)
    {
        if (_initialized) return _ffmpegDir!;

        string? dir = explicitDirectory
            ?? Environment.GetEnvironmentVariable("RODAJE_FFMPEG")
            ?? (OperatingSystem.IsWindows() ? @"D:\Programas de uso libre\Rodaje\.entorno\ffmpeg\bin" : "/usr/lib/x86_64-linux-gnu");

        // Try candidates
        string[] candidates = new[]
        {
            dir,
            Path.Combine(dir, "bin"),
            Path.Combine(dir, "lib"),
            AppContext.BaseDirectory,
        };

        string? resolvedDir = null;
        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && (File.Exists(Path.Combine(c, "avcodec-63.dll")) || File.Exists(Path.Combine(c, "libavcodec.so.63")) || File.Exists(Path.Combine(c, "libavcodec.so"))))
            {
                resolvedDir = c;
                break;
            }
        }

        if (resolvedDir == null)
        {
            // Fallback to directory directly if exists
            if (Directory.Exists(dir)) resolvedDir = dir;
            else resolvedDir = AppContext.BaseDirectory;
        }

        _ffmpegDir = resolvedDir;

        Assembly bindingsAssembly = typeof(DynamicallyLinkedBindings).Assembly;
        NativeLibrary.SetDllImportResolver(bindingsAssembly, (libraryName, assembly, searchPath) =>
        {
            return ResolveLibrary(_ffmpegDir, libraryName);
        });

        DynamicallyLinkedBindings.Initialize();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        _initialized = true;
        return _ffmpegDir;
    }

    private static nint ResolveLibrary(string directory, string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            string fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
            if (!fileName.Contains('-'))
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                string withVersion = stem switch
                {
                    "avutil" => "avutil-61.dll",
                    "avcodec" => "avcodec-63.dll",
                    "avformat" => "avformat-63.dll",
                    "swresample" => "swresample-7.dll",
                    "swscale" => "swscale-10.dll",
                    "avfilter" => "avfilter-12.dll",
                    "avdevice" => "avdevice-63.dll",
                    _ => fileName
                };
                fileName = withVersion;
            }

            string path = Path.Combine(directory, fileName);
            if (File.Exists(path)) return NativeLibrary.Load(path);

            string binPath = Path.Combine(directory, "bin", fileName);
            if (File.Exists(binPath)) return NativeLibrary.Load(binPath);

            return NativeLibrary.Load(fileName);
        }
        else
        {
            // Linux: libavcodec.so.63, etc.
            string stem = libraryName;
            if (stem.StartsWith("lib")) stem = stem[3..];
            if (stem.EndsWith(".so")) stem = stem[..^3];

            string versioned = stem switch
            {
                "avutil" => "libavutil.so.61",
                "avcodec" => "libavcodec.so.63",
                "avformat" => "libavformat.so.63",
                "swresample" => "libswresample.so.7",
                "swscale" => "libswscale.so.10",
                "avfilter" => "libavfilter.so.12",
                "avdevice" => "libavdevice.so.63",
                _ => $"lib{stem}.so"
            };

            string[] paths = new[]
            {
                Path.Combine(directory, versioned),
                Path.Combine(directory, "lib", versioned),
                Path.Combine(directory, $"lib{stem}.so"),
                Path.Combine(directory, "lib", $"lib{stem}.so"),
                versioned,
                $"lib{stem}.so"
            };

            foreach (var p in paths)
            {
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out nint h)) return h;
            }

            return NativeLibrary.Load(versioned);
        }
    }
}
