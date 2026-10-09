using FotoToma.Core.Timing;

namespace FotoToma.TestMedia;

/// <summary>Los archivos que piden los criterios de la fase 2: seek exacto en H.264 y HEVC, y deriva A/V.</summary>
public static class TestMediaPresets
{
    /// <summary>H.264 1080p a 29,97 (30000/1001), GOP 60 con cuadros B, en MP4.</summary>
    public static MediaSpec H264Ntsc30BFrames { get; } =
        new("h264-1080p-2997-bframes", 1920, 1080, Rational.Ntsc30, TimeSpan.FromSeconds(20))
        {
            Codec = VideoCodec.H264,
            GopLength = 60,
            BFrames = 2,
            Container = ContainerFormat.Mp4,
        };

    /// <summary>HEVC 1080p a 25 fps, GOP 50 con cuadros B, en MKV.</summary>
    public static MediaSpec Hevc25Mkv { get; } =
        new("hevc-1080p-25", 1920, 1080, Rational.Fps25, TimeSpan.FromSeconds(20))
        {
            Codec = VideoCodec.Hevc,
            GopLength = 50,
            BFrames = 2,
            Container = ContainerFormat.Mkv,
        };

    /// <summary>H.264 1080p a 23,976 (24000/1001) con AAC estéreo a 48 kHz, en MP4.</summary>
    public static MediaSpec H264Ntsc24Aac { get; } =
        new("h264-1080p-23976-aac", 1920, 1080, Rational.Ntsc24, TimeSpan.FromSeconds(30))
        {
            Codec = VideoCodec.H264,
            GopLength = 48,
            BFrames = 2,
            Audio = AudioCodec.Aac,
            Container = ContainerFormat.Mp4,
        };

    /// <summary>Diez minutos de 720p a 29,97 con AAC y poca tasa de bits, para medir la deriva A/V.</summary>
    public static MediaSpec Drift720p10Min { get; } =
        new("deriva-720p-2997-10min", 1280, 720, Rational.Ntsc30, TimeSpan.FromMinutes(10))
        {
            Codec = VideoCodec.H264,
            GopLength = 60,
            BFrames = 2,
            Audio = AudioCodec.Aac,
            Container = ContainerFormat.Mp4,
            VideoBitrateKbps = 1500,
        };

    public static IReadOnlyList<MediaSpec> All { get; } = [H264Ntsc30BFrames, Hevc25Mkv, H264Ntsc24Aac, Drift720p10Min];
}
