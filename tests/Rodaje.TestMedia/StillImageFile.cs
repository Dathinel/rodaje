namespace Rodaje.TestMedia;

/// <summary>Imagen creada durante la prueba; rojo con alfa 0, 128 y 255 en tres bandas verticales.</summary>
public sealed class StillImageFile : IDisposable
{
    public const int Width = 64;
    public const int Height = 48;
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rodaje-foto-" + Guid.NewGuid().ToString("N"));

    public StillImageFile(string extension = "png", bool blue = false)
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "foto." + extension);
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 4;
                pixels[i + (blue ? 2 : 0)] = 255;
                pixels[i + 3] = blue || x >= 2 * Width / 3 ? (byte)255 : x < Width / 3 ? (byte)0 : (byte)128;
                if (pixels[i + 3] == 0)
                {
                    pixels[i] = 0;
                    pixels[i + 1] = 255;
                }
            }
        }

        try
        {
            FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgba",
                "-video_size", "64x48", "-i", "pipe:0", "-frames:v", "1", "-threads", "1", "-update", "1", Path],
                input => input.Write(pixels));
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
