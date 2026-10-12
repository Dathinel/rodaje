using Rodaje.Media.Export;
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
/// Lo que entrega son las mismas instancias del catálogo, con sus <see cref="EncoderSpec.BaseArguments"/>: lo que se
/// prueba es lo que se exporta.
/// </remarks>
public sealed class CatalogEncoderSource : IExportEncoderSource
{
    public CatalogEncoderSource(ExportEncoderAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        Availability = availability;
    }

    /// <summary>El catálogo LGPL probado con este <c>ffmpeg.exe</c>.</summary>
    public CatalogEncoderSource(FfmpegExecutable ffmpeg)
        : this(new ExportEncoderAvailability(new FfmpegExportEncoderProbe(ffmpeg)))
    {
    }

    /// <summary>La sonda compartida: el informe completo, con el motivo de cada descartado.</summary>
    public ExportEncoderAvailability Availability { get; }

    /// <summary>x264 y x265 (<see cref="GplEncoderCandidates"/>) probados con el <c>ffmpeg.exe</c> del complemento GPL.</summary>
    public static CatalogEncoderSource ForGplAddOn(FfmpegExecutable gpl) =>
        new(new ExportEncoderAvailability(new FfmpegExportEncoderProbe(gpl), catalog: GplEncoderCandidates.All));

    public async Task<IReadOnlyList<EncoderSpec>> GetAsync(CancellationToken cancellationToken = default) =>
        (await Availability.GetAsync(cancellationToken).ConfigureAwait(false)).Available;
}
