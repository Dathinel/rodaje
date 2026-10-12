using Rodaje.Media.Gpu;

namespace Rodaje.Media.Export;

/// <summary>
/// Sonda que antes de lanzar nada mira el perfil del equipo (<see cref="GpuProfile.SkipReason"/>): un codificador de GPU que
/// no puede existir aquí (falta la GPU de ese fabricante, o su generación no tiene ese códec) queda
/// <see cref="ExportProbeStatus.NoDevice">sin dispositivo</see> sin probarse ni reintentarse. Lo demás pasa a la sonda real.
/// </summary>
public sealed class GpuProfileEncoderProbe(IExportEncoderProbe inner, GpuProfile profile) : IExportEncoderProbe
{
    private readonly IExportEncoderProbe _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly GpuProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ExportProbeResult Probe(EncoderSpec encoder) =>
        _profile.SkipReason(encoder) is { } reason ? ExportProbeResult.NoDevice(reason) : _inner.Probe(encoder);

    public void Reset() => _inner.Reset();
}
