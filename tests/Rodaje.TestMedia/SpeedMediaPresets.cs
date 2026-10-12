using Rodaje.Core.Timing;

namespace Rodaje.TestMedia;

/// <summary>Los medios de la fase 5c: número de cuadro dibujado y una marca de audio por segundo, para seek y deriva con velocidad.</summary>
public static class SpeedMediaPresets
{
    /// <summary>
    /// 16 minutos de 720p a 29,97 con AAC: a 1,5× dan los 10 minutos de la medida de deriva con margen, y a 100× todavía
    /// 9,6 s de línea de tiempo para los seeks.
    /// </summary>
    public static MediaSpec Speed720p16Min { get; } =
        new("velocidad-720p-2997-16min", 1280, 720, Rational.Ntsc30, TimeSpan.FromMinutes(16))
        {
            Codec = VideoCodec.H264,
            GopLength = 60,
            BFrames = 2,
            Audio = AudioCodec.Aac,
            Container = ContainerFormat.Mp4,
            VideoBitrateKbps = 1500,
        };

    public static IReadOnlyList<MediaSpec> All { get; } = [Speed720p16Min];
}
