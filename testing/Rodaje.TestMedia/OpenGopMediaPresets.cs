using Rodaje.Core.Timing;

namespace Rodaje.TestMedia;

/// <summary>Medios con GOP abierto: keyframes CRA de HEVC con cuadros iniciales (RASL) que dependen del GOP anterior.</summary>
public static class OpenGopMediaPresets
{
    /// <summary>Único codificador del preset: el build LGPL lo trae en cualquier máquina y por omisión cierra los GOP con CRA.</summary>
    public const string Encoder = "libkvazaar";

    /// <summary>
    /// HEVC 640×360 a 25 fps, 10 s, en MP4: IDR en 0 y CRA en 64, 128 y 192, cada uno precedido en presentación por los
    /// cuadros RASL que se codifican después de él. Con el número de cuadro dibujado (<see cref="FrameCode"/>).
    /// </summary>
    public static MediaSpec HevcOpenGop { get; } =
        new("hevc-gop-abierto-360p-25", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(10))
        {
            Codec = VideoCodec.Hevc,
            Encoder = Encoder,
            GopLength = 64,
            Container = ContainerFormat.Mp4,
        };

    public static IReadOnlyList<MediaSpec> All { get; } = [HevcOpenGop];
}
