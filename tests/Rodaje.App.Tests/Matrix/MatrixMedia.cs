using System.Globalization;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

/// <summary>Genera (una vez, en la caché de medios de prueba) los archivos de <see cref="MatrixCases"/>.</summary>
internal static class MatrixMedia
{
    public const int FormatVersion = 1;

    private static readonly Lock Gate = new();

    public static string Folder => Path.Combine(TestMediaGenerator.DefaultCacheDirectory, "matriz");

    /// <summary>La ruta del archivo (la primera imagen, en una secuencia), o el motivo por el que no se pudo generar.</summary>
    public static (string? Path, string? Problem) GetOrCreate(FfmpegTool ffmpeg, MatrixCase c)
    {
        if (c.Requires is { } encoder && Unusable(ffmpeg, encoder) is { } reason)
        {
            return (null, reason);
        }

        string stem = Path.Combine(Folder, $"{c.Name}-{c.CacheKey()}");
        string path = c.Kind == MatrixKind.ImageSequence ? Path.Combine(stem, "img_001.png") : stem + c.Extension;
        lock (Gate)
        {
            if (File.Exists(path))
            {
                return (path, null);
            }

            Directory.CreateDirectory(Folder);
            string partial = c.Kind == MatrixKind.ImageSequence ? stem + ".parcial" : stem + ".parcial" + c.Extension;
            try
            {
                Generate(ffmpeg, c, partial);
                if (c.Kind == MatrixKind.ImageSequence)
                {
                    Directory.Move(partial, stem);
                }
                else
                {
                    File.Move(partial, path);
                }
            }
            catch (FfmpegException ex)
            {
                return (null, ex.Message);
            }
            finally
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
                else if (Directory.Exists(partial))
                {
                    Directory.Delete(partial, recursive: true);
                }
            }

            return (path, null);
        }
    }

    // ProbeEncoder solo sabe probar como audio aac y opus: los demás codificadores de audio se prueban como video y fallan.
    private static string? Unusable(FfmpegTool ffmpeg, string encoder) => encoder is "ac3" or "flac" or "libmp3lame"
        ? (ffmpeg.IsEncoderListed(encoder) ? null : $"ffmpeg.exe no incluye {encoder}.")
        : ffmpeg.ProbeEncoder(encoder);

    private static void Generate(FfmpegTool ffmpeg, MatrixCase c, string output)
    {
        string seconds = c.Seconds.ToString("R", CultureInfo.InvariantCulture);
        int frames = c.Kind == MatrixKind.Image ? 1 : checked((int)Math.Round(c.Seconds * c.Rate.Num / c.Rate.Den));
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-nostdin" };
        if (c.HasVideo)
        {
            args.AddRange(["-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{c.Width}x{c.Height}", "-framerate", $"{c.Rate.Num}/{c.Rate.Den}", "-i", "pipe:0"]);
        }

        if (c.HasAudio)
        {
            // Pitidos de 0,1 s cada 0,5 s (forma de onda con estructura y ritmo detectable), o voz real.
            args.AddRange(c.Voice is { } voice
                ? ["-ss", c.VoiceOffset.ToString("R", CultureInfo.InvariantCulture), "-t", seconds, "-i", voice]
                : ["-f", "lavfi", "-i", $"aevalsrc=0.5*sin(2*PI*440*t)*lt(mod(t\\,0.5)\\,0.1):s=48000:d={seconds}"]);
            args.AddRange(c.HasVideo ? ["-map", "0:v:0", "-map", "1:a:0"] : ["-map", "0:a:0"]);
        }

        if (c.VariableRate)
        {
            args.AddRange(["-vf", c.VariableRateFilter]);
        }

        args.AddRange(c.Video);
        args.AddRange(c.Audio);
        args.AddRange(c.Output);
        if (c.Kind == MatrixKind.Image)
        {
            args.AddRange(["-frames:v", "1", "-update", "1"]);
        }

        string target = output;
        string? beforeRotation = null;
        if (c.Kind == MatrixKind.ImageSequence)
        {
            Directory.CreateDirectory(output);
            target = Path.Combine(output, "img_%03d.png");
        }
        else if (c.Rotation != 0)
        {
            beforeRotation = output + ".sin-rotar" + c.Extension;
            target = beforeRotation;
        }

        args.Add(target);
        try
        {
            if (c.HasVideo)
            {
                var video = new SyntheticVideo(c.Width, c.Height);
                ffmpeg.Run(args, input =>
                {
                    var frame = new byte[video.FrameSize];
                    for (int i = 0; i < frames; i++)
                    {
                        video.Render(frame, i);
                        input.Write(frame);
                    }
                });
            }
            else
            {
                ffmpeg.Run(args);
            }

            if (beforeRotation is not null)
            {
                ffmpeg.Run(["-hide_banner", "-loglevel", "error", "-y", "-nostdin", "-display_rotation", c.Rotation.ToString(CultureInfo.InvariantCulture),
                    "-i", beforeRotation, "-map", "0", "-c", "copy", output]);
            }
        }
        finally
        {
            if (beforeRotation is not null)
            {
                File.Delete(beforeRotation);
            }
        }
    }
}
