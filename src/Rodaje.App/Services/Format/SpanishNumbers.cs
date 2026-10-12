using System.Globalization;

namespace Rodaje.App.Services.Format;

/// <summary>
/// El formato de los números que ve el usuario: coma decimal y punto de miles, como en español.
/// </summary>
/// <remarks>
/// La app corre con <c>InvariantGlobalization</c>: no hay «es-ES» (pedirla lanza al abrir la app) y
/// <see cref="CultureInfo.CurrentCulture"/> es la invariante, que da «0.5». Por eso todo texto con decimales para el
/// usuario se arma con <see cref="Format"/>: <c>string.Create(SpanishNumbers.Format, $"{v:0.0} s")</c> o
/// <c>v.ToString("0.0", SpanishNumbers.Format)</c>. Lo que se guarda o se lee de archivos sigue en la invariante.
/// </remarks>
public static class SpanishNumbers
{
    /// <summary>
    /// Coma decimal y punto de miles, también en los porcentajes (<c>P</c>), que van con espacio: «35 %», «-35 %»,
    /// «12,5 %». El resto, como la invariante. Es de solo lectura.
    /// </summary>
    /// <remarks>
    /// Los patrones de porcentaje se fijan aunque coincidan con los de la invariante (0: «n %», 0: «-n %»), para que el
    /// espacio antes del signo no dependa de ella.
    /// </remarks>
    public static NumberFormatInfo Format { get; } = NumberFormatInfo.ReadOnly(new NumberFormatInfo
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        PercentDecimalSeparator = ",",
        PercentGroupSeparator = ".",
        PercentPositivePattern = 0,
        PercentNegativePattern = 0,
    });
}
