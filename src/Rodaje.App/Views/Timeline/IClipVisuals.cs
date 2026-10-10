using Microsoft.UI.Xaml.Media;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Quien da las miniaturas y las formas de onda de los medios a la línea de tiempo. Sin proveedor (o mientras no tenga
/// algo listo) se dibujan los marcadores de posición. Todo se llama desde el hilo de la interfaz y debe volver enseguida:
/// lo que falta se pide, se prepara aparte y se avisa con <see cref="Changed"/>.
/// </summary>
public interface IClipVisuals
{
    /// <summary>Hay visuales nuevos para un medio (su identificador): la línea de tiempo lo vuelve a dibujar.</summary>
    event EventHandler<string>? Changed;

    /// <summary>
    /// La miniatura del medio más cercana a <paramref name="sourceTime"/> (tiempo del origen), para dibujarla con
    /// <paramref name="height"/> píxeles de alto (independientes del DPI); <c>null</c> si todavía no está.
    /// </summary>
    ImageSource? GetThumbnail(string mediaId, Time sourceTime, double height);

    /// <summary>Los picos del sonido del medio, o <c>null</c> si todavía no están (o no tiene sonido).</summary>
    WaveformPeaks? GetWaveform(string mediaId);
}
