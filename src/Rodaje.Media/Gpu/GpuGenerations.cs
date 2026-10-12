using System.Text.RegularExpressions;

namespace Rodaje.Media.Gpu;

/// <summary>Códecs que el motor de codificación de una generación hace por hardware.</summary>
[Flags]
public enum HardwareEncodeCodecs
{
    None = 0,
    H264 = 1 << 0,
    Hevc = 1 << 1,
    Hevc10 = 1 << 2,
    Av1 = 1 << 3,
}

/// <summary>Una generación de GPU con lo que se espera de sus bloques de video.</summary>
/// <param name="Vendor">Fabricante.</param>
/// <param name="Name">Generación («Ampere», «RDNA 3», «Xe-LPG»…).</param>
/// <param name="Engines">Bloques de video («NVENC 7.ª gen. + NVDEC 5.ª gen.», «VCN 4.0», «Quick Sync (Xe)»).</param>
/// <param name="Decode">Perfiles de decodificación que se esperan (el controlador manda: esto es para explicar y para las pruebas).</param>
/// <param name="Encode">Códecs que codifica; con <see cref="Known"/> en <c>false</c> no se usa para descartar.</param>
/// <param name="Known">Si la generación salió de la tabla; si no, la sonda decide todo.</param>
public sealed record GpuGeneration(GpuVendor Vendor, string Name, string Engines, HardwareDecodeProfiles Decode,
    HardwareEncodeCodecs Encode, bool Known = true)
{
    public bool Encodes(HardwareEncodeCodecs codec) => (Encode & codec) == codec;
}

/// <summary>
/// Tabla de generaciones conocidas por el nombre del adaptador de DXGI: NVDEC/NVENC por arquitectura, AMD VCN y Intel
/// Quick Sync por generación. Sirve para elegir sin probar a ciegas (no se prueba AV1 por GPU en una RTX 30) y para
/// explicar al usuario qué esperar; la decodificación real la dice siempre el controlador (<see cref="HardwareDecodeSupport"/>).
/// </summary>
/// <remarks>
/// Fuentes: matriz de NVENC/NVDEC de NVIDIA (Video Codec SDK), tablas de VCN de AMD (AMF) y de Quick Sync de Intel
/// (Media Capabilities). Ante la duda la entrada no existe y la generación queda como desconocida: así nunca se descarta un
/// codificador que podría funcionar.
/// </remarks>
public static class GpuGenerations
{
    private const HardwareDecodeProfiles Basic = HardwareDecodeProfiles.H264 | HardwareDecodeProfiles.Mpeg2;
    private const HardwareDecodeProfiles Hevc10 = Basic | HardwareDecodeProfiles.HevcMain | HardwareDecodeProfiles.HevcMain10;
    private const HardwareDecodeProfiles Vp9 = Hevc10 | HardwareDecodeProfiles.Vp9Profile0 | HardwareDecodeProfiles.Vp9Profile2;
    private const HardwareDecodeProfiles Modern = Vp9 | HardwareDecodeProfiles.Av1Main;

    private const HardwareEncodeCodecs H264Only = HardwareEncodeCodecs.H264;
    private const HardwareEncodeCodecs HevcEncode = HardwareEncodeCodecs.H264 | HardwareEncodeCodecs.Hevc | HardwareEncodeCodecs.Hevc10;
    private const HardwareEncodeCodecs Av1Encode = HevcEncode | HardwareEncodeCodecs.Av1;

    private static readonly (GpuVendor Vendor, Regex Pattern, GpuGeneration Generation)[] Table =
    [
        // ---- NVIDIA (del más nuevo al más viejo) ----
        Nv(@"RTX\s*50\d\d|RTX PRO \d+ Blackwell|\bB(100|200)\b", "Blackwell", "NVENC 9.ª gen. + NVDEC 6.ª gen. (HEVC/H.264 4:2:2)", Modern, Av1Encode),
        Nv(@"RTX\s*40\d\d|RTX \d+ Ada|\bL4\b|\bL40", "Ada Lovelace", "NVENC 8.ª gen. (AV1) + NVDEC 5.ª gen.", Modern, Av1Encode),
        Nv(@"RTX\s*30\d\d|RTX A\d{3,4}|\bA[1-4]0\b|\bA2\b", "Ampere", "NVENC 7.ª gen. + NVDEC 5.ª gen. (AV1)", Modern, HevcEncode),
        Nv(@"GTX\s*16[56]0", "Turing (TU117)", "NVENC 6.ª gen. (Volta) + NVDEC 4.ª gen.", Vp9, HevcEncode),
        Nv(@"RTX\s*20\d\d|GTX\s*16\d\d|TITAN RTX|Quadro RTX|\bT4\b|\bT[46]00\b|\bT1[02]00\b", "Turing", "NVENC 7.ª gen. + NVDEC 4.ª gen.", Vp9, HevcEncode),
        Nv(@"GTX\s*10\d\d|TITAN Xp?\b|Quadro P\d+|\bMX[1-4]50\b", "Pascal", "NVENC 6.ª gen. + NVDEC 3.ª gen.", Vp9, HevcEncode),
        Nv(@"GTX\s*9\d\d|GTX\s*7[45]0|Quadro M\d+", "Maxwell", "NVENC 5.ª gen. + NVDEC 2.ª gen.", Basic | HardwareDecodeProfiles.HevcMain, H264Only | HardwareEncodeCodecs.Hevc),

        // ---- AMD ----
        Amd(@"RX\s*9\d{3}", "RDNA 4", "VCN 5.0", Modern, Av1Encode),
        Amd(@"RX\s*7\d{3}|Radeon\s*(PRO\s*)?W7\d{3}|Radeon\s*7[46]0M|Radeon\s*780M|Radeon\s*8[0-9]0[MS]", "RDNA 3", "VCN 4.0 (AV1)", Modern, Av1Encode),
        Amd(@"RX\s*6[45]00", "RDNA 2 (Navi 24)", "VCN 3.0 solo decodificación", Modern, HardwareEncodeCodecs.None),
        Amd(@"RX\s*6\d{3}|Radeon\s*(PRO\s*)?W6\d{3}|Radeon\s*6[0-9]0M", "RDNA 2", "VCN 3.0", Modern, HevcEncode),
        Amd(@"RX\s*5[5-7]00\s*XT|RX\s*5[3-7]00\b|Radeon\s*(PRO\s*)?W5\d{3}", "RDNA 1", "VCN 2.0", Vp9, HevcEncode),
        Amd(@"Vega|Radeon\s*VII|RX\s*Vega", "GCN 5 (Vega)", "VCE 4 + UVD 7 / VCN 1.0", Vp9, HevcEncode),
        Amd(@"RX\s*[45][4-9]0\b|RX\s*590", "GCN 4 (Polaris)", "VCE 3.4 + UVD 6.3", Hevc10, HevcEncode),

        // ---- Intel ----
        Intel(@"Arc.*\bB\d{3}\b|Arc\(TM\)\s*1[34]0V|Arc.*\b1[34]0V\b", "Xe2 (Battlemage / Lunar Lake)", "Quick Sync Xe2 (AV1, HEVC 4:2:2)", Modern, Av1Encode),
        Intel(@"Arc.*\bA\d{3}M?\b|Arc\(TM\) Pro A", "Xe-HPG (Alchemist)", "Quick Sync Xe-HPG (AV1, HEVC 4:2:2)", Modern, Av1Encode),
        Intel(@"Arc\(TM\)\s*Graphics|Intel\(R\)\s*Graphics$", "Xe-LPG (Core Ultra)", "Quick Sync Xe-LPG (AV1)", Modern, Av1Encode),
        Intel(@"Iris\(R\)\s*Xe|UHD Graphics\s*7\d\d|UHD Graphics$|Iris Xe", "Xe-LP (Gen12: Tiger Lake a Raptor Lake)", "Quick Sync Gen12 (AV1 solo decodificación)", Modern, HevcEncode),
        Intel(@"Iris\(R\)\s*Plus|UHD Graphics\s*G\d", "Gen11 (Ice Lake)", "Quick Sync Gen11", Vp9, HevcEncode),
        Intel(@"UHD Graphics\s*6\d\d|HD Graphics\s*6\d\d|Iris\(R\)\s*Plus Graphics\s*6\d\d|Iris\(R\)\s*Graphics\s*[56]\d\d", "Gen9 (Skylake a Comet Lake)", "Quick Sync Gen9", Vp9, HevcEncode),
        Intel(@"HD Graphics\s*[45]\d{3}|HD Graphics\s*5\d\d", "Gen7.5–Gen8 (Haswell, Broadwell)", "Quick Sync Gen7.5–8", Basic, H264Only),
    ];

    /// <summary>La generación del adaptador; si no está en la tabla, una «desconocida» que no descarta nada.</summary>
    /// <param name="vendor">Fabricante (de <see cref="GpuVendors.FromPciId"/>).</param>
    /// <param name="description">Nombre del adaptador según DXGI («NVIDIA GeForce RTX 3050 Laptop GPU»).</param>
    public static GpuGeneration Identify(GpuVendor vendor, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (vendor == GpuVendor.Microsoft)
        {
            return new GpuGeneration(vendor, "Adaptador de software", "sin bloques de video", HardwareDecodeProfiles.None, HardwareEncodeCodecs.None);
        }

        foreach ((GpuVendor v, Regex pattern, GpuGeneration generation) in Table)
        {
            if (v == vendor && pattern.IsMatch(description))
            {
                return generation;
            }
        }

        return new GpuGeneration(vendor, "desconocida", "sin datos: la sonda decide", HardwareDecodeProfiles.None, HardwareEncodeCodecs.None, Known: false);
    }

    private static (GpuVendor, Regex, GpuGeneration) Nv(string pattern, string name, string engines, HardwareDecodeProfiles decode, HardwareEncodeCodecs encode) =>
        Entry(GpuVendor.Nvidia, pattern, name, engines, decode, encode);

    private static (GpuVendor, Regex, GpuGeneration) Amd(string pattern, string name, string engines, HardwareDecodeProfiles decode, HardwareEncodeCodecs encode) =>
        Entry(GpuVendor.Amd, pattern, name, engines, decode, encode);

    private static (GpuVendor, Regex, GpuGeneration) Intel(string pattern, string name, string engines, HardwareDecodeProfiles decode, HardwareEncodeCodecs encode) =>
        Entry(GpuVendor.Intel, pattern, name, engines, decode, encode);

    private static (GpuVendor, Regex, GpuGeneration) Entry(GpuVendor vendor, string pattern, string name, string engines,
        HardwareDecodeProfiles decode, HardwareEncodeCodecs encode) =>
        (vendor, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), new GpuGeneration(vendor, name, engines, decode, encode));
}
