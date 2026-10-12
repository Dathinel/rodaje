using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;

namespace Rodaje.Export.Encoders;

/// <summary>Los codificadores que funcionan en este equipo, en orden de preferencia (hardware primero).</summary>
public interface IExportEncoderSource
{
    Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Los codificadores del catálogo (<see cref="ExportEncoderCatalog"/>) que pasan la sonda en este equipo: la única lista de
/// la exportación. Prueba, guarda y reintenta con <see cref="ExportEncoderAvailability"/>.
/// </summary>
/// <remarks>
/// <para>Lo que entrega son las mismas instancias del catálogo, con sus <see cref="EncoderSpec.BaseArguments"/>: lo que se
/// prueba es lo que se exporta.</para>
/// <para>Con el perfil del equipo (<see cref="GpuProfile"/>), los codificadores de GPU que no pueden existir aquí no se
/// prueban (<see cref="GpuProfileEncoderProbe"/>) y los de la GPU principal van delante de los de otra GPU
/// (<see cref="ExportEncoderChooser.PreferVendor"/>).</para>
/// </remarks>
public sealed class CatalogEncoderSource : IExportEncoderSource
{
    private readonly EncoderVendor? _preferredVendor;

    /// <param name="availability">La sonda compartida.</param>
    /// <param name="preferredVendor">Fabricante de la GPU principal, cuyos codificadores van primero; <c>null</c> deja el orden del catálogo.</param>
    public CatalogEncoderSource(ExportEncoderAvailability availability, EncoderVendor? preferredVendor = null)
    {
        ArgumentNullException.ThrowIfNull(availability);
        Availability = availability;
        _preferredVendor = preferredVendor;
    }

    /// <summary>El catálogo LGPL probado con este <c>ffmpeg.exe</c>; con <paramref name="profile"/>, filtrado y ordenado por la GPU.</summary>
    public CatalogEncoderSource(FfmpegExecutable ffmpeg, GpuProfile? profile = null)
        : this(new ExportEncoderAvailability(Probe(new FfmpegExportEncoderProbe(ffmpeg), profile)), profile?.PreferredEncoderVendor)
    {
    }

    /// <summary>La sonda compartida: el informe completo, con el motivo de cada descartado.</summary>
    public ExportEncoderAvailability Availability { get; }

    /// <summary>x264 y x265 (<see cref="GplEncoderCandidates"/>) probados con el <c>ffmpeg.exe</c> del complemento GPL.</summary>
    public static CatalogEncoderSource ForGplAddOn(FfmpegExecutable gpl) =>
        new(new ExportEncoderAvailability(new FfmpegExportEncoderProbe(gpl), catalog: GplEncoderCandidates.All));

    public async Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EncoderSpec> available = (await Availability.GetAsync(cancellationToken).ConfigureAwait(false)).Available;
        return _preferredVendor is null ? available : ExportEncoderChooser.PreferVendor(available, _preferredVendor);
    }

    private static IExportEncoderProbe Probe(IExportEncoderProbe real, GpuProfile? profile) =>
        profile is null ? real : new GpuProfileEncoderProbe(real, profile);
}
