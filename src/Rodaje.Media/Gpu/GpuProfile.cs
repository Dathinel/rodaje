using System.Globalization;
using Rodaje.Media.Export;

namespace Rodaje.Media.Gpu;

/// <summary>Un adaptador de DXGI del equipo.</summary>
/// <param name="Name">Nombre que da el controlador.</param>
/// <param name="VendorId">Identificador PCI del fabricante.</param>
/// <param name="DeviceId">Identificador PCI del modelo.</param>
/// <param name="DedicatedVideoMemory">VRAM propia en bytes (0 en muchas integradas).</param>
/// <param name="SharedSystemMemory">Memoria del sistema que puede usar, en bytes.</param>
/// <param name="IsSoftware">Adaptador de software (WARP): sin video por hardware.</param>
/// <param name="DriverVersion">Versión del controlador («32.0.15.6094»), o <c>null</c> si no se pudo leer.</param>
public sealed record GpuAdapter(string Name, uint VendorId, uint DeviceId, long DedicatedVideoMemory, long SharedSystemMemory,
    bool IsSoftware, string? DriverVersion)
{
    public GpuVendor Vendor => IsSoftware ? GpuVendor.Microsoft : GpuVendors.FromPciId(VendorId);

    public GpuGeneration Generation => GpuGenerations.Identify(Vendor, Name);

    /// <summary>Lo que identifica al adaptador y su controlador en la caché.</summary>
    public string Key => string.Create(CultureInfo.InvariantCulture, $"{VendorId:X4}:{DeviceId:X4}:{DriverVersion}");
}

/// <summary>
/// Perfil del equipo para el video: los adaptadores (el primero es el que usa Rodaje), qué decodifica de verdad el
/// principal por D3D11VA y los núcleos de la CPU. Con él se decide sin probar a ciegas qué codificador va por defecto,
/// cuáles ni se prueban y qué decodificador abre cada medio. Lo arma <see cref="GpuProfileReader"/> y se guarda en caché.
/// </summary>
/// <param name="Adapters">Adaptadores de DXGI en su orden (el 0 es el de por defecto, el de Rodaje).</param>
/// <param name="Decode">Perfiles que el controlador del principal declara para D3D11VA.</param>
/// <param name="LogicalCores">Hilos lógicos de la CPU.</param>
public sealed record GpuProfile(IReadOnlyList<GpuAdapter> Adapters, HardwareDecodeProfiles Decode, int LogicalCores)
{
    /// <summary>Un equipo sin GPU (o sin poder leerla): todo por CPU.</summary>
    public static GpuProfile CpuOnly(int logicalCores) => new([], HardwareDecodeProfiles.None, logicalCores);

    /// <summary>El adaptador de hardware que usa Rodaje, o <c>null</c> si solo hay software.</summary>
    public GpuAdapter? Primary => Adapters.FirstOrDefault(a => !a.IsSoftware);

    public GpuVendor PrimaryVendor => Primary?.Vendor ?? GpuVendor.Unknown;

    public bool HasHardwareGpu => Primary is not null;

    public bool Has(GpuVendor vendor) => Adapters.Any(a => !a.IsSoftware && a.Vendor == vendor);

    /// <summary>Clave de la caché: cambia si cambia un adaptador o su controlador.</summary>
    public string Key => string.Join("|", Adapters.Select(a => a.Key));

    /// <summary>El fabricante cuyos codificadores van primero: el de la GPU principal (en un portátil híbrido, la dedicada).</summary>
    public EncoderVendor? PreferredEncoderVendor => PrimaryVendor switch
    {
        GpuVendor.Nvidia => EncoderVendor.Nvidia,
        GpuVendor.Amd => EncoderVendor.Amd,
        GpuVendor.Intel => EncoderVendor.Intel,
        _ => null,
    };

    /// <summary>
    /// Por qué no vale la pena probar un codificador en este equipo, o <c>null</c> si hay que probarlo. Solo descarta lo
    /// seguro: falta la GPU de ese fabricante, o su generación conocida no tiene ese códec (AV1 en una RTX 30).
    /// </summary>
    public string? SkipReason(EncoderSpec encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        GpuVendor vendor = encoder.Vendor switch
        {
            EncoderVendor.Nvidia => GpuVendor.Nvidia,
            EncoderVendor.Amd => GpuVendor.Amd,
            EncoderVendor.Intel => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
        if (!encoder.IsHardware || vendor == GpuVendor.Unknown)
        {
            return null;
        }

        GpuAdapter[] adapters = [.. Adapters.Where(a => !a.IsSoftware && a.Vendor == vendor)];
        if (adapters.Length == 0)
        {
            return $"No hay GPU {vendor.DisplayName()} en este equipo.";
        }

        HardwareEncodeCodecs needed = encoder.Codec switch
        {
            ExportCodec.H264 => HardwareEncodeCodecs.H264,
            ExportCodec.Hevc => HardwareEncodeCodecs.Hevc,
            ExportCodec.Av1 => HardwareEncodeCodecs.Av1,
            _ => HardwareEncodeCodecs.None,
        };
        if (needed == HardwareEncodeCodecs.None || adapters.Any(a => !a.Generation.Known || a.Generation.Encodes(needed)))
        {
            return null;
        }

        GpuAdapter first = adapters[0];
        return $"{first.Name} ({first.Generation.Name}) no codifica {ExportEncoderCatalog.DisplayName(encoder)} por hardware.";
    }

    /// <summary>Resumen para el diagnóstico y el registro.</summary>
    public string Describe()
    {
        if (Primary is not { } gpu)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Sin GPU por hardware; {LogicalCores} hilos de CPU.");
        }

        string vram = gpu.DedicatedVideoMemory > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{gpu.DedicatedVideoMemory / (1024.0 * 1024 * 1024):0.0} GB de VRAM")
            : "memoria compartida";
        string others = Adapters.Count(a => !a.IsSoftware) > 1
            ? "; también " + string.Join(", ", Adapters.Where(a => !a.IsSoftware && a != gpu).Select(a => a.Name))
            : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{gpu.Name} ({gpu.Vendor.DisplayName()} {gpu.Generation.Name}, {gpu.Generation.Engines}), {vram}; decodifica por hardware: " +
            $"{HardwareDecodeSupport.Describe(Decode)}; {LogicalCores} hilos de CPU{others}.");
    }
}
