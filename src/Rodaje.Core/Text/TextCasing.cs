using System.Globalization;
using Rodaje.Core.Model;

namespace Rodaje.Core.Text;

/// <summary>
/// Mayúsculas al dibujar (<see cref="StyleTypography.Case"/>): carácter por carácter, así el texto dibujado mide lo mismo
/// que el guardado y las posiciones de tramos y palabras siguen valiendo.
/// </summary>
public static class TextCasing
{
    /// <summary>Escala de las minúsculas convertidas en las versalitas sintéticas.</summary>
    public const double SmallCapsScale = 0.75;

    // La invariante y no «es-ES»: la app publica con InvariantGlobalization y pedir una cultura por nombre lanza
    // (CultureNotFoundException al iniciar el tipo, y ningún título en mayúsculas se dibujaría). En español da lo mismo.
    private static readonly CultureInfo Casing = CultureInfo.InvariantCulture;

    /// <summary>
    /// El texto para dibujar; en versalitas, <paramref name="lowered"/> marca las minúsculas que se agrandaron (para
    /// achicarlas a <see cref="SmallCapsScale"/>). Debe medir lo mismo que el texto.
    /// </summary>
    public static string Apply(string text, TextCase textCase, Span<bool> lowered = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (textCase == TextCase.None || text.Length == 0)
        {
            return text;
        }

        return string.Create(text.Length, (text, textCase), static (chars, state) =>
        {
            for (int i = 0; i < chars.Length; i++)
            {
                char c = state.text[i];
                chars[i] = state.textCase == TextCase.Lower ? char.ToLower(c, Casing) : char.ToUpper(c, Casing);
            }
        }) is var result && textCase == TextCase.SmallCaps && lowered.Length >= text.Length
            ? Mark(text, result, lowered)
            : result;
    }

    private static string Mark(string original, string upper, Span<bool> lowered)
    {
        for (int i = 0; i < original.Length; i++)
        {
            lowered[i] = upper[i] != original[i] && char.IsLower(original[i]);
        }

        return upper;
    }
}
