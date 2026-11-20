using System.Buffers.Binary;

namespace Rodaje.TestMedia;

/// <summary>Dos parches neutros: 812 y 203 nits, desde planos G, B, R lineales de media precisión.</summary>
public sealed class RawIntermediateMedia : IDisposable
{
    public const int Width = 64;
    public const int Height = 32;
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "raw-intermediate-" + Guid.NewGuid().ToString("N"));

    public RawIntermediateMedia()
    {
        Directory.CreateDirectory(_folder);
    }

    public string CreatePq() => Encode("parches.mkv",
        "zscale=tin=linear:pin=2020:min=gbr:rin=full:t=smpte2084:p=2020:m=2020_ncl:r=full:npl=203,format=yuv444p16le",
        ["-c:v", "ffv1", "-level", "3", "-g", "1", "-pix_fmt", "yuv444p16le", "-color_primaries", "bt2020",
            "-color_trc", "smpte2084", "-colorspace", "bt2020nc", "-color_range", "pc", "-f", "matroska"]);

    public string CreateSdr() => Encode("parches.mov",
        "zscale=tin=linear:pin=2020:min=gbr:rin=full:t=709:p=709:m=709:r=limited:npl=203,format=yuv422p10le",
        ["-c:v", "prores_ks", "-profile:v", "3", "-pix_fmt", "yuv422p10le", "-color_primaries", "bt709",
            "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"]);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Encode(string name, string filter, string[] encoding)
    {
        string path = Path.Combine(_folder, name);
        var planes = new byte[Width * Height * 6];
        for (int plane = 0; plane < 3; plane++)
        {
            for (int pixel = 0; pixel < Width * Height; pixel++)
            {
                Half value = (Half)(pixel % Width < Width / 2 ? 4 : 1);
                BinaryPrimitives.WriteUInt16LittleEndian(planes.AsSpan(((plane * Width * Height) + pixel) * 2), BitConverter.HalfToUInt16Bits(value));
            }
        }

        FfmpegTool.Default!.Run(["-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "gbrpf16le",
            "-s", "64x32", "-framerate", "24", "-i", "pipe:0", "-vf", filter, .. encoding, path], input => input.Write(planes));
        return path;
    }
}
