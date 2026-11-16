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
        string ffmpegExe = FindFfmpegExecutable(ffmpegDir);
        const string Source = "-y -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 10 -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";

        // Un codificador puede estar en el build y no servir en este equipo (h264_nvenc sin GPU): se prueban en orden.
        // Sin HEVC de 10 bits LGPL por software, el respaldo es VP9 de 10 bits: en el camino de subida solo importa que el cuadro sea P010.
        string h264Path = Encode(ffmpegExe, samplesDir, "8 bits (NV12)", Source + " -pix_fmt yuv420p",
            ("h264_nvenc", "h264_nv12.mp4"), ("libopenh264", "h264_nv12.mp4"));
        string hevcPath = Encode(ffmpegExe, samplesDir, "10 bits (P010)", Source,
            ("hevc_nvenc -pix_fmt p010le -profile:v main10", "hevc_p010.mp4"),
            ("libkvazaar -pix_fmt yuv420p10le", "hevc_p010.mp4"),
            ("libvpx-vp9 -pix_fmt yuv420p10le -profile:v 2 -b:v 6M -deadline realtime -cpu-used 8", "vp9_p010.mkv"));
        return (h264Path, hevcPath);
    }

    private static string Encode(string ffmpegExe, string dir, string what, string source, params (string Encoder, string File)[] candidates)
    {
        foreach ((string _, string file) in candidates)
        {
            if (File.Exists(Path.Combine(dir, file))) return Path.Combine(dir, file);
        }

        string errors = "";
        foreach ((string encoder, string file) in candidates)
        {
            string path = Path.Combine(dir, file);
            try
            {
                Console.WriteLine($"Generando muestra de {what} con {encoder.Split(' ')[0]}...");
                RunProcess(ffmpegExe, $"{source} -c:v {encoder} \"{path}\"");
                return path;
            }
            catch (InvalidOperationException ex)
            {
                if (File.Exists(path)) File.Delete(path);
                string[] lines = ex.Message.TrimEnd().Split('\n');
                errors += $"\n  {encoder.Split(' ')[0]}: {lines[^1].Trim()}";
            }
        }

        throw new InvalidOperationException($"Ningún codificador pudo generar la muestra de {what}:{errors}");
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
