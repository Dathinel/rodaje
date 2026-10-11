using Rodaje.Core.Model;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Contenido de paquetes que necesita el motor: LUT (por <see cref="IEffectResources"/>) y las curvas de las
/// transiciones de paquete. La app lo implementa sobre <c>Rodaje.Paquetes</c>; el motor no conoce los paquetes.
/// </summary>
public interface IEffectContent : IEffectResources
{
    /// <summary>
    /// Curvas del preajuste <c>paquete@versión/elemento</c> de una transición <see cref="BuiltInTransitions.Preset"/>, o
    /// <c>null</c> si falta: entonces la transición se ve como fundido cruzado. Se llama al armar el plan, no por cuadro.
    /// </summary>
    TransitionCurves? FindTransition(string reference);
}

/// <summary>
/// Una transición de paquete ya leída: curvas de la transformación del clip que sale y del que entra, más la mezcla.
/// </summary>
/// <param name="MixCode">Código de una transición propia del registro (por ejemplo el fundido cruzado).</param>
/// <param name="ReferenceWidth">Ancho de la secuencia para la que se hicieron las posiciones; se escalan a la actual.</param>
/// <param name="ReferenceHeight">Alto de esa secuencia.</param>
/// <param name="Outgoing">Curvas del clip que sale, por parámetro de <see cref="BuiltInEffects.Transform"/>.</param>
/// <param name="Incoming">Curvas del clip que entra.</param>
public sealed record TransitionCurves(
    int MixCode,
    int ReferenceWidth,
    int ReferenceHeight,
    IReadOnlyList<TransitionCurve> Outgoing,
    IReadOnlyList<TransitionCurve> Incoming);

/// <summary>Curva de un parámetro de la transformación a lo largo de la transición.</summary>
public sealed record TransitionCurve(string Parameter, IReadOnlyList<CurveKey> Keys);

/// <summary>Un punto de la curva; <paramref name="Progress"/> va de 0 (inicio de la transición) a 1 (final).</summary>
/// <param name="Progress">Instante dentro de la transición, de 0 a 1.</param>
/// <param name="Value">Valor del parámetro.</param>
/// <param name="Interpolation">Cómo se llega al punto siguiente.</param>
public readonly record struct CurveKey(double Progress, double Value, Interpolation Interpolation);
