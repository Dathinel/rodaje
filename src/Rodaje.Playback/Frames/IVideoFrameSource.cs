namespace Rodaje.Playback;

/// <summary>Decodificador de un flujo de video, visto como una lista de cuadros numerados.</summary>
/// <remarks>
/// El cuadro <c>n</c> tiene <c>Pts = Time.FromFrames(n, tasa)</c> en tiempo del origen; la implementación
/// normaliza el <c>start_time</c> del contenedor y resuelve internamente el seek al keyframe previo.
/// Las llamadas nunca se solapan: el programador espera a que termine una antes de pedir la siguiente.
/// </remarks>
public interface IVideoFrameSource
{
    /// <summary>Devuelve el cuadro con ese índice exacto, o <c>null</c> si el medio no lo tiene.</summary>
    /// <remarks>
    /// El llamador recibe la única referencia del cuadro. Debe atender <paramref name="cancellationToken"/>
    /// pronto, porque un seek espera a que la decodificación en curso termine o se cancele.
    /// </remarks>
    ValueTask<DecodedFrame?> GetFrameAsync(long frameIndex, CancellationToken cancellationToken);
}
