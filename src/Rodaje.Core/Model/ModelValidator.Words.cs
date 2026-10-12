namespace Rodaje.Core.Model;

/// <summary>Reglas de las palabras transcritas de un generador (<c>generator.words</c>, formato 5).</summary>
public static partial class ModelValidator
{
    /// <summary>Palabras como mucho en un generador: un subtítulo lleva unas pocas; un título largo, unos cientos.</summary>
    public const int MaxGeneratorWords = 2_000;

    /// <summary>Caracteres como mucho de una palabra.</summary>
    public const int MaxWordLength = 100;

    /// <summary>
    /// Sin elementos nulos, cada palabra con texto visible sin espacios ni caracteres de control, tiempos dentro de
    /// ±<see cref="MaxEdgeOffsetSeconds"/> s, inicio no posterior al fin y en orden de inicio.
    /// </summary>
    private static void ValidateWords(ClipGenerator generator, string where, List<string> errors)
    {
        if (generator.Words is not { } words)
        {
            return;
        }

        if (words.Count > MaxGeneratorWords)
        {
            errors.Add($"{where} tiene más de {MaxGeneratorWords} palabras transcritas.");
            return;
        }

        GeneratorWord? previous = null;
        foreach (GeneratorWord? word in words)
        {
            if (word is null)
            {
                errors.Add($"{where} tiene una palabra transcrita vacía (null).");
                return;
            }

            if (word.Text is not { Length: > 0 and <= MaxWordLength } text || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                errors.Add($"{where} tiene una palabra transcrita vacía, de más de {MaxWordLength} caracteres o con espacios o caracteres de control.");
                return;
            }

            if (!InRange(word.Start.ToSeconds(), -MaxEdgeOffsetSeconds, MaxEdgeOffsetSeconds)
                || !InRange(word.End.ToSeconds(), -MaxEdgeOffsetSeconds, MaxEdgeOffsetSeconds)
                || word.End < word.Start || (previous is not null && word.Start < previous.Start))
            {
                errors.Add($"{where} tiene palabras transcritas con tiempos fuera de rango, que terminan antes de empezar o fuera de orden.");
                return;
            }

            previous = word;
        }
    }

    /// <summary>Caracteres de las palabras transcritas de un clip (cuentan para el tope del proyecto).</summary>
    private static long WordsLength(Clip clip) => clip.Generator?.Words?.Sum(w => (long)(w?.Text?.Length ?? 0)) ?? 0;
}
