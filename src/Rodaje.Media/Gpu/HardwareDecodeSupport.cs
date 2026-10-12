namespace Rodaje.Media.Gpu;

/// <summary>Fabricante de una GPU, según el identificador PCI que da DXGI.</summary>
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,

    /// <summary>El adaptador de software de Windows (Microsoft Basic Render Driver, WARP): sin video por hardware.</summary>
    Microsoft,
}

public static class GpuVendors
{
    public const uint NvidiaId = 0x10DE;
    public const uint AmdId = 0x1002;
    public const uint AmdAlternateId = 0x1022;
    public const uint IntelId = 0x8086;
    public const uint MicrosoftId = 0x1414;

    public static GpuVendor FromPciId(uint vendorId) => vendorId switch
    {
        NvidiaId => GpuVendor.Nvidia,
        AmdId or AmdAlternateId => GpuVendor.Amd,
        IntelId => GpuVendor.Intel,
        MicrosoftId => GpuVendor.Microsoft,
        _ => GpuVendor.Unknown,
    };

    public static string DisplayName(this GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        GpuVendor.Microsoft => "Microsoft (software)",
        _ => "desconocido",
    };
}

/// <summary>Perfiles de decodificación D3D11VA que el controlador declara y FFmpeg sabe usar.</summary>
[Flags]
public enum HardwareDecodeProfiles
{
    None = 0,
    Mpeg2 = 1 << 0,
    H264 = 1 << 1,
    HevcMain = 1 << 2,
    HevcMain10 = 1 << 3,
    Vp9Profile0 = 1 << 4,
    Vp9Profile2 = 1 << 5,
    Av1Main = 1 << 6,
}

/// <summary>Qué dice el perfil de la GPU sobre decodificar un flujo por hardware.</summary>
public enum HardwareDecodeVerdict
{
    /// <summary>El controlador declara el perfil: se usa D3D11VA.</summary>
    Supported,

    /// <summary>Ni FFmpeg ni el controlador lo hacen por hardware: software con todos los hilos desde el principio.</summary>
    Unsupported,

    /// <summary>No se sabe (perfil sin leer o combinación sin tabla): se intenta D3D11VA y, si FFmpeg lo rechaza, software.</summary>
    Unknown,
}

/// <summary>
/// Decide antes de abrir el decodificador si un flujo irá por D3D11VA. Sin esto, con GPU el decodificador se abre con un
/// solo hilo (D3D11VA no usa hilos por cuadro) y, si el hardware rechaza el flujo (ProRes, DNxHR, H.264 de 10 bits o
/// 4:2:2, HEVC 4:2:2), la decodificación por software queda con ese único hilo.
/// </summary>
public static class HardwareDecodeSupport
{
    // Valores públicos de DXVA (dxva.h); los mismos que trae la tabla de avcodec (comprobado en avcodec-63.dll).
    public static readonly Guid Mpeg2Vld = new("ee27417f-5e28-4e65-beea-1d26b508adc9");
    public static readonly Guid Mpeg2And1Vld = new("86695f12-340e-4f04-9fd3-9253dd327460");
    public static readonly Guid H264VldNoFgt = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid HevcVldMain = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    public static readonly Guid HevcVldMain10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
    public static readonly Guid Vp9VldProfile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
    public static readonly Guid Vp9Vld10BitProfile2 = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");
    public static readonly Guid Av1VldProfile0 = new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a");

    /// <summary>El perfil que corresponde a un GUID de decodificador, o <see cref="HardwareDecodeProfiles.None"/>.</summary>
    public static HardwareDecodeProfiles FromGuid(Guid decoderProfile) => decoderProfile switch
    {
        _ when decoderProfile == Mpeg2Vld || decoderProfile == Mpeg2And1Vld => HardwareDecodeProfiles.Mpeg2,
        _ when decoderProfile == H264VldNoFgt => HardwareDecodeProfiles.H264,
        _ when decoderProfile == HevcVldMain => HardwareDecodeProfiles.HevcMain,
        _ when decoderProfile == HevcVldMain10 => HardwareDecodeProfiles.HevcMain10,
        _ when decoderProfile == Vp9VldProfile0 => HardwareDecodeProfiles.Vp9Profile0,
        _ when decoderProfile == Vp9Vld10BitProfile2 => HardwareDecodeProfiles.Vp9Profile2,
        _ when decoderProfile == Av1VldProfile0 => HardwareDecodeProfiles.Av1Main,
        _ => HardwareDecodeProfiles.None,
    };

    /// <summary>Submuestreo del croma según el nombre del formato de píxel de FFmpeg (<c>yuv422p10le</c>, <c>nv12</c>…).</summary>
    public static int ChromaOf(string pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(pixelFormat);
        return pixelFormat.Contains("444", StringComparison.Ordinal) || pixelFormat.StartsWith("gbr", StringComparison.Ordinal)
            ? 444
            : pixelFormat.Contains("422", StringComparison.Ordinal) || pixelFormat.StartsWith("y210", StringComparison.Ordinal)
                ? 422
                : 420;
    }

    /// <param name="codec">Nombre del códec de FFmpeg (<c>h264</c>, <c>hevc</c>, <c>prores</c>…).</param>
    /// <param name="pixelFormat">Formato de píxel del flujo.</param>
    /// <param name="bitDepth">Bits por muestra.</param>
    /// <param name="ffmpegHasD3D11Va">Si este FFmpeg tiene D3D11VA para el códec (<c>avcodec_get_hw_config</c>).</param>
    /// <param name="profiles">Lo que declara el controlador; <see cref="HardwareDecodeProfiles.None"/> si no se pudo leer.</param>
    /// <param name="vendor">Fabricante: con Intel, HEVC 4:2:2 y 4:4:4 se intentan (FFmpeg trae sus GUID propios).</param>
    public static HardwareDecodeVerdict Evaluate(string codec, string pixelFormat, int bitDepth, bool ffmpegHasD3D11Va,
        HardwareDecodeProfiles profiles, GpuVendor vendor)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (!ffmpegHasD3D11Va)
        {
            return HardwareDecodeVerdict.Unsupported;
        }

        if (profiles == HardwareDecodeProfiles.None)
        {
            return HardwareDecodeVerdict.Unknown;
        }

        int chroma = ChromaOf(pixelFormat);
        bool plain420 = chroma == 420 && bitDepth == 8;
        bool tenBit420 = chroma == 420 && bitDepth == 10;
        HardwareDecodeProfiles? needed = codec switch
        {
            "h264" => plain420 ? HardwareDecodeProfiles.H264 : HardwareDecodeProfiles.None,
            "hevc" when plain420 => HardwareDecodeProfiles.HevcMain,
            "hevc" when tenBit420 => HardwareDecodeProfiles.HevcMain10,
            "hevc" => vendor == GpuVendor.Intel ? null : HardwareDecodeProfiles.None,
            "vp9" when plain420 => HardwareDecodeProfiles.Vp9Profile0,
            "vp9" when tenBit420 => HardwareDecodeProfiles.Vp9Profile2,
            "vp9" => HardwareDecodeProfiles.None,
            "av1" => plain420 || tenBit420 ? HardwareDecodeProfiles.Av1Main : HardwareDecodeProfiles.None,
            "mpeg2video" => plain420 ? HardwareDecodeProfiles.Mpeg2 : HardwareDecodeProfiles.None,
            _ => null,
        };
        return needed switch
        {
            null => HardwareDecodeVerdict.Unknown,
            HardwareDecodeProfiles.None => HardwareDecodeVerdict.Unsupported,
            { } flag => (profiles & flag) != 0 ? HardwareDecodeVerdict.Supported : HardwareDecodeVerdict.Unsupported,
        };
    }

    /// <summary>Lista legible de los perfiles, para el diagnóstico («H.264, HEVC Main 10, AV1»).</summary>
    public static string Describe(HardwareDecodeProfiles profiles)
    {
        if (profiles == HardwareDecodeProfiles.None)
        {
            return "ninguno";
        }

        var names = new List<string>();
        void Add(HardwareDecodeProfiles flag, string name)
        {
            if ((profiles & flag) != 0)
            {
                names.Add(name);
            }
        }

        Add(HardwareDecodeProfiles.H264, "H.264");
        Add(HardwareDecodeProfiles.HevcMain, "HEVC Main");
        Add(HardwareDecodeProfiles.HevcMain10, "HEVC Main 10");
        Add(HardwareDecodeProfiles.Vp9Profile0, "VP9");
        Add(HardwareDecodeProfiles.Vp9Profile2, "VP9 10 bits");
        Add(HardwareDecodeProfiles.Av1Main, "AV1");
        Add(HardwareDecodeProfiles.Mpeg2, "MPEG-2");
        return string.Join(", ", names);
    }
}
