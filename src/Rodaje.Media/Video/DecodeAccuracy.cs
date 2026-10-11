namespace Rodaje.Media.Video;

/// <summary>Qué tan exacto debe ser el cuadro que se pide a <see cref="VideoDecoder"/>.</summary>
/// <remarks>
/// Los modos aproximados solo actúan si el decodificador se abrió con <see cref="VideoDecoderOptions.SkipNonReference"/>;
/// si no, se tratan como <see cref="Exact"/>. Un cuadro aproximado lleva su índice real, que puede ser menor que el pedido,
/// para que la caché nunca lo guarde como si fuera el cuadro pedido.
/// </remarks>
public enum DecodeAccuracy
{
    /// <summary>El cuadro con ese índice exacto. Es lo que usan la pausa, el seek, la exportación e <c>IVideoFrameSource</c>.</summary>
    Exact,

    /// <summary>
    /// Sin decodificar los cuadros que nadie usa como referencia (<c>AVDISCARD_NONREF</c>): entrega el último cuadro
    /// decodificado que no pasa del pedido. Para la reproducción rápida, por debajo de 16×.
    /// </summary>
    SkipNonReference,

    /// <summary>
    /// Solo keyframes (<c>AVDISCARD_NONKEY</c>): entrega el último keyframe que no pasa del pedido. Para la reproducción
    /// de 16× a 100×.
    /// </summary>
    Keyframe,
}
