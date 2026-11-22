using System.Globalization;
using Rodaje.App.Services.Format;

namespace Rodaje.App.Services.Inspector;

/// <summary>Lo escrito en un cuadro numérico, sin WinUI.</summary>
public static class TypedNumber
{
    /// <summary>
    /// El número que hay que confirmar: el texto convertido con el mismo lector del cuadro si es un número y no es el valor
    /// que ya tiene; nulo si no hay nada que confirmar.
    /// </summary>
    /// <param name="current">El valor del cuadro; <see cref="double.NaN"/> cuando muestra «—» con varios clips distintos.</param>
    public static double? Pending(string? text, double current, Func<string, double?> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);
        return string.IsNullOrEmpty(text) || parse(text) is not { } value || !double.IsFinite(value) || value == current
            ? null
            : value;
    }

    /// <summary>
    /// Lee «29,97» y «29.97» igual en cualquier Windows. El formateador de WinRT sigue el idioma de Windows: en
    /// es-CO o de-DE rechazaba «0.5» y leía «1.000» como mil; en en-US rechazaba «1,5». Sin separador de miles, con signo
    /// «-» o «−»; lo demás (cifras nativas, dos separadores) es nulo.
    /// </summary>
    public static double? Parse(string? text)
    {
        string s = (text ?? "").Trim().Replace('−', '-').Replace(',', '.');
        return s.Length > 0
            && double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v)
            && double.IsFinite(v)
            ? v
            : null;
    }

    /// <summary>Con coma decimal, como el resto de la app, y siempre con <paramref name="decimals"/> decimales.</summary>
    public static string Format(double value, int decimals)
    {
        double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        return (rounded == 0 ? 0 : rounded).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), SpanishNumbers.Format);
    }
}
