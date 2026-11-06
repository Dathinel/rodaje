using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Matrix;

/// <summary>Las miniaturas (biblioteca, tira de fotogramas, módulos) no pasan por el compositor: convierten el HDR en la CPU.</summary>
public sealed class HdrThumbnailTests
{
    [HdrFact]
    public void Hdr_thumbnails_are_tone_mapped_and_sdr_ones_are_not_touched()
    {
        string pq = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mp4-hdr10-pq")).Path!;
        string hlg = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mov-hlg")).Path!;
        string sdr = MatrixMedia.GetOrCreate(FfmpegTool.Default!, MatrixCases.Get("mp4-hevc10-aac")).Path!;

        byte[] sdrPixels = Thumbnail(sdr);
        byte[] pqPixels = Thumbnail(pq);
        byte[] hlgPixels = Thumbnail(hlg);

        // Mismos códigos de origen con distinta curva: la miniatura cambia porque se convierte, y PQ y HLG no se convierten igual.
        Assert.False(sdrPixels.AsSpan().SequenceEqual(pqPixels), "La miniatura PQ salió sin convertir.");
        Assert.False(sdrPixels.AsSpan().SequenceEqual(hlgPixels), "La miniatura HLG salió sin convertir.");
        Assert.False(pqPixels.AsSpan().SequenceEqual(hlgPixels));
        Assert.True(pqPixels.Where((_, i) => i % 4 == 3).All(a => a == 255));

        // Lo mismo sin pasar por el decodificador: SDR intacto.
        byte[] copy = [.. sdrPixels];
        HdrToneMap.ToSdrInPlace(copy, VideoColor.Bt709Limited);
        Assert.Equal(sdrPixels, copy);
    }

    private static byte[] Thumbnail(string path)
    {
        using ThumbnailDecoder decoder = ThumbnailDecoder.Open(path);
        return decoder.Decode(Time.FromSeconds(1), 72)!.Pixels;
    }
}
