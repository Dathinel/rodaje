namespace Rodaje.Media.Export;

/// <summary>Formato comprimido de una pista de la salida.</summary>
public enum ExportCodec
{
    H264,
    Hevc,
    Av1,
    Vp9,

    /// <summary>Apple ProRes 422 Proxy/LT/Standard/HQ y 4444 (codificador nativo de FFmpeg).</summary>
    ProRes,

    /// <summary>Avid DNxHR LB/SQ/HQ/HQX/444.</summary>
    DnxHr,

    /// <summary>GoPro CineForm, si el FFmpeg lo trae.</summary>
    CineForm,

    /// <summary>Maestro sin pérdidas.</summary>
    Ffv1,

    /// <summary>Motion JPEG; también los JPEG de una secuencia de imágenes.</summary>
    Mjpeg,
    Png,

    /// <summary>QuickTime Animation (<c>qtrle</c>), RGB con alfa en MOV.</summary>
    QtAnimation,
    Gif,
    WebP,
    Tiff,
    Exr,
    Aac,
    Opus,
    Mp3,
    Flac,
    Alac,

    /// <summary>PCM sin comprimir (16, 24 o 32 bits, o float de 32).</summary>
    Pcm,
}

/// <summary>Quién hace el trabajo de codificar.</summary>
public enum EncoderVendor
{
    /// <summary>La CPU, con una biblioteca de software.</summary>
    Software,
    Nvidia,
    Amd,
    Intel,

    /// <summary>Media Foundation de Windows (por hardware si el controlador lo ofrece).</summary>
    Microsoft,
}

/// <summary>Licencia del código que codifica, dentro del FFmpeg LGPL (nunca GPL ni nonfree en el editor).</summary>
public enum EncoderLicense
{
    /// <summary>Código propio de FFmpeg (LGPL).</summary>
    FfmpegLgpl,

    /// <summary>Biblioteca externa LGPL (LAME).</summary>
    Lgpl,

    /// <summary>Biblioteca externa BSD o similar (SVT-AV1, libvpx, libaom, Opus, kvazaar, OpenH264, libwebp).</summary>
    Permissive,

    /// <summary>El controlador de la GPU o el sistema; FFmpeg solo lo llama (NVENC, AMF, QSV, Media Foundation).</summary>
    SystemOrDriver,

    /// <summary>GPL: solo en un complemento aparte con su propio <c>ffmpeg.exe</c> (x264, x265), nunca en el editor.</summary>
    GplAddOn,
}

/// <summary>Cómo se reparte la tasa de bits.</summary>
public enum RateControlMode
{
    /// <summary>Calidad constante: CRF (x26x, SVT-AV1, libvpx), CQ (NVENC), QVBR o <c>-q</c> (MJPEG, audio VBR por calidad).</summary>
    ConstantQuality,

    /// <summary>Tasa variable con media y máxima.</summary>
    Vbr,

    /// <summary>Tasa constante (emisión, plataformas que la piden).</summary>
    Cbr,

    /// <summary>Tasa media sin tope; con dos pasadas da el tamaño objetivo.</summary>
    Abr,

    /// <summary>Sin pérdidas (FFV1, PNG, FLAC, ALAC, PCM, x264/x265 con <c>qp 0</c>).</summary>
    Lossless,

    /// <summary>El perfil fija la tasa (ProRes, DNxHR, CineForm): no hay nada que ajustar.</summary>
    ProfileDefined,
}

/// <summary>Un perfil que el codificador admite y para qué píxeles sirve.</summary>
/// <param name="Id">Valor de <c>-profile:v</c> (o <c>-profile:a</c>), por ejemplo <c>high</c>, <c>main10</c>, <c>hq</c>, <c>dnxhr_hqx</c>, <c>aac_he</c>.</param>
/// <param name="Name">Nombre que ve el usuario, por ejemplo «High 4:2:2» o «ProRes 422 HQ».</param>
public sealed record EncoderProfile(string Id, string Name)
{
    /// <summary>Formatos de píxel con los que vale este perfil; vacío si vale con todos los del codificador.</summary>
    public IReadOnlyList<ExportPixelFormat> PixelFormats { get; init; } = [];

    public bool Allows(ExportPixelFormat format) => PixelFormats.Count == 0 || PixelFormats.Contains(format);
}

/// <summary>Escala de calidad de <see cref="RateControlMode.ConstantQuality"/>.</summary>
/// <param name="Name">Cómo la llama el codificador: «CRF», «CQ», «q».</param>
/// <param name="Argument">Argumento de FFmpeg, por ejemplo <c>-crf</c>, <c>-cq</c>, <c>-q:v</c>, <c>-q:a</c>.</param>
/// <param name="Min">Valor mínimo admitido.</param>
/// <param name="Max">Valor máximo admitido.</param>
/// <param name="Default">El que usa el codificador si no se dice otro.</param>
/// <param name="LowerIsBetter">Si un número menor da más calidad (CRF, CQ) o al revés.</param>
public sealed record QualityScale(string Name, string Argument, double Min, double Max, double Default, bool LowerIsBetter = true)
{
    public bool Contains(double value) => value >= Min && value <= Max;
}

/// <summary>Un codificador de salida de <c>ffmpeg.exe</c> y lo que admite, para elegirlo, validar y armar la línea de comandos.</summary>
/// <remarks>
/// Es solo el dato; el catálogo en orden de preferencia y la sonda que dice cuáles funcionan en el equipo están en esta
/// misma carpeta. Las capacidades son las del codificador; las del
/// contenedor están en <see cref="ExportCompatibility"/>.
/// </remarks>
public sealed record EncoderSpec
{
    /// <summary>Nombre de <c>-c:v</c> o <c>-c:a</c> en FFmpeg, por ejemplo <c>h264_nvenc</c>.</summary>
    public required string Id { get; init; }

    public required ExportCodec Codec { get; init; }

    public required bool IsHardware { get; init; }

    public required EncoderVendor Vendor { get; init; }

    public required EncoderLicense License { get; init; }

    /// <summary>
    /// Si puede entrar en una elección automática o un preajuste de fábrica. <c>false</c> en kvazaar y OpenH264: codifican
    /// H.264/HEVC en software, sin la cobertura de patentes de la GPU o de Windows (<c>docs/legal.md</c>); solo si el
    /// usuario los elige.
    /// </summary>
    public bool InDefaults { get; init; } = true;

    /// <summary>Por qué no está en los valores por defecto o qué hardware pide (por ejemplo «solo RTX 40»), en español.</summary>
    public string? Note { get; init; }

    /// <summary>Formatos de píxel que acepta, del preferido al último. Vacío en los de audio.</summary>
    public IReadOnlyList<ExportPixelFormat> PixelFormats { get; init; } = [];

    /// <summary>Argumentos fijos tras <c>-c:v id</c>; los de calidad, perfil y GOP salen de los ajustes.</summary>
    public IReadOnlyList<string> BaseArguments { get; init; } = [];

    /// <summary>Perfiles admitidos, del de por defecto al último; vacío si el codificador no tiene.</summary>
    public IReadOnlyList<EncoderProfile> Profiles { get; init; } = [];

    /// <summary>Niveles admitidos (<c>4.1</c>, <c>5.1</c>…); vacío si no tiene o si se dejan en automático.</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];

    /// <summary>Modos de control de tasa admitidos, el de por defecto primero.</summary>
    public IReadOnlyList<RateControlMode> RateControls { get; init; } = [];

    /// <summary>Escala de <see cref="RateControlMode.ConstantQuality"/>; <c>null</c> si no la admite.</summary>
    public QualityScale? Quality { get; init; }

    public bool SupportsTwoPass { get; init; }

    /// <summary>Cuadros B seguidos como máximo; 0 si no usa.</summary>
    public int MaxBFrames { get; init; }

    /// <summary>Valores de <c>-preset</c> de velocidad, del más rápido al más lento; vacío si no tiene.</summary>
    public IReadOnlyList<string> SpeedPresets { get; init; } = [];

    public string? DefaultSpeedPreset { get; init; }

    /// <summary>Valores de <c>-tune</c> (psicovisual, animación, grano…); vacío si no tiene.</summary>
    public IReadOnlyList<string> Tunes { get; init; } = [];

    /// <summary>Solo cuadros clave (ProRes, DNxHR, FFV1, MJPEG, PNG): GOP y cuadros B no aplican.</summary>
    public bool IntraOnly { get; init; }

    /// <summary>Tasas de muestreo admitidas (audio); vacío si admite cualquiera.</summary>
    public IReadOnlyList<int> SampleRates { get; init; } = [];

    /// <summary>Canales como máximo (audio); 0 en video.</summary>
    public int MaxChannels { get; init; }

    public bool IsAudio => Codec.IsAudio();

    public bool Supports10Bit => PixelFormats.Any(f => f.BitDepth() >= 10);

    public bool SupportsAlpha => PixelFormats.Any(f => f.HasAlpha());

    public bool Supports(RateControlMode mode) => RateControls.Contains(mode);

    /// <summary>El primer formato de píxel de la lista que cumple lo pedido, o <c>null</c> si no hay.</summary>
    public ExportPixelFormat? PixelFormatFor(int bitDepth, ChromaSubsampling chroma = ChromaSubsampling.Chroma420, bool alpha = false) =>
        PixelFormats.Select(f => (ExportPixelFormat?)f).FirstOrDefault(f =>
            f!.Value.BitDepth() == bitDepth && f.Value.Chroma() == chroma && f.Value.HasAlpha() == alpha);
}

public static class ExportCodecs
{
    public static bool IsAudio(this ExportCodec codec) =>
        codec is ExportCodec.Aac or ExportCodec.Opus or ExportCodec.Mp3 or ExportCodec.Flac or ExportCodec.Alac or ExportCodec.Pcm;

    /// <summary>Códecs de imagen fija, para <see cref="ExportContainer.ImageSequence"/>.</summary>
    public static bool IsImage(this ExportCodec codec) =>
        codec is ExportCodec.Png or ExportCodec.Tiff or ExportCodec.Mjpeg or ExportCodec.Exr;
}
