using System;
using System.Diagnostics;
using System.IO;

namespace Rodaje.VulkanVideo;

public static class VideoGenerator
{
    public static (string H264Path, string HevcPath) EnsureSamplesExist(string ffmpegDir)
    {
        string samplesDir = Path.Combine(AppContext.BaseDirectory, "muestras");
        Directory.CreateDirectory(samplesDir);

        string h264Path = Path.Combine(samplesDir, "h264_nv12.mp4");
        string hevcPath = Path.Combine(samplesDir, "hevc_p010.mp4");

        string ffmpegExe = FindFfmpegExecutable(ffmpegDir);

        string encodersOutput = RunCommand(ffmpegExe, "-encoders");

        string h264Encoder = encodersOutput.Contains("h264_nvenc") ? "h264_nvenc"
                           : encodersOutput.Contains("libopenh264") ? "libopenh264"
                           : "h264";

        string hevcEncoder = encodersOutput.Contains("hevc_nvenc") ? "hevc_nvenc"
                           : encodersOutput.Contains("libkvazaar") ? "libkvazaar"
                           : "hevc";

        if (!File.Exists(h264Path))
        {
            Console.WriteLine($"Generando muestra H.264 8-bit ({h264Encoder})...");
            string args = $"-y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 10 -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -c:v {h264Encoder} \"{h264Path}\"";
            RunProcess(ffmpegExe, args);
        }

        if (!File.Exists(hevcPath))
        {
            Console.WriteLine($"Generando muestra HEVC Main10 ({hevcEncoder})...");
            string args = $"-y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 10 -pix_fmt p010le -profile:v main10 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -c:v {hevcEncoder} \"{hevcPath}\"";
            RunProcess(ffmpegExe, args);
        }

        return (h264Path, hevcPath);
    }

    private static string FindFfmpegExecutable(string ffmpegDir)
    {
        string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        string candidate1 = Path.Combine(ffmpegDir, exeName);
        if (File.Exists(candidate1)) return candidate1;

        string candidate2 = Path.Combine(ffmpegDir, "bin", exeName);
        if (File.Exists(candidate2)) return candidate2;

        string candidate3 = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(candidate3)) return candidate3;

        string? envDir = Environment.GetEnvironmentVariable("RODAJE_FFMPEG");
        if (!string.IsNullOrEmpty(envDir))
        {
            string candidate4 = Path.Combine(envDir, exeName);
            if (File.Exists(candidate4)) return candidate4;
            string candidate5 = Path.Combine(envDir, "bin", exeName);
            if (File.Exists(candidate5)) return candidate5;
        }

        return exeName;
    }

    private static string RunCommand(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static void RunProcess(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"No se pudo iniciar {exe}.");
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg falló al generar muestra (código {p.ExitCode}): {error}");
        }
    }
}
