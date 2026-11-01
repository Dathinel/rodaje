namespace Rodaje.TestMedia;

/// <summary>Imagen creada durante la prueba; rojo con alfa 0, 128 y 255 en tres bandas verticales.</summary>
public sealed class StillImageFile : IDisposable
{
    public const int Width = 64;
    public const int Height = 48;
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rodaje-foto-" + Guid.NewGuid().ToString("N"));

    public StillImageFile(string extension = "png", bool blue = false, int width = Width, int height = Height, string? pixelFormat = null, bool orientation6 = false)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "foto." + extension);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                pixels[i + (blue ? 2 : 0)] = 255;
                pixels[i + 3] = blue || x >= 2 * width / 3 ? (byte)255 : x < width / 3 ? (byte)0 : (byte)128;
                if (pixels[i + 3] == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 255;
                }
            }
        }

        if (pixelFormat == "pal8")
        {
            pixels = new byte[width * height + 1024];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[y * width + x] = x < width / 3 ? (byte)0 : x < 2 * width / 3 ? (byte)1 : (byte)2;
                }
            }

            int palette = width * height;
            pixels[palette + 1] = 255;
            pixels[palette + 6] = 255;
            pixels[palette + 7] = 128;
            pixels[palette + 10] = 255;
            pixels[palette + 11] = 255;
        }

        try
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", pixelFormat == "pal8" ? "pal8" : "rgba",
                "-video_size", $"{width}x{height}", "-i", "pipe:0", "-frames:v", "1", "-threads", "1", "-update", "1" };
            if (pixelFormat is not null)
            {
                args.AddRange(["-pix_fmt", pixelFormat]);
            }

            args.Add(Path);
            FfmpegTool.Default!.Run(args.ToArray(),
                input => input.Write(pixels));
            if (orientation6)
            {
                byte[] jpeg = File.ReadAllBytes(Path);
                byte[] exif = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0,
                    73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
                File.WriteAllBytes(Path, [.. jpeg[..2], .. exif, .. jpeg[2..]]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
