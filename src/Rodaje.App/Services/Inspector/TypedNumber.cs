namespace Rodaje.App.Services.Inspector;

/// <summary>Lo escrito en un cuadro numérico que todavía no se ha confirmado (sin Intro), sin WinUI.</summary>
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
}
