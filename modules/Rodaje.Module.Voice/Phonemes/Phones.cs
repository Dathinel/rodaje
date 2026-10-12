using System.Text;
using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice.Phonemes;

internal interface IPhonemizer
{
    /// <summary>Los fonemas de una frase en el alfabeto de Kokoro (IPA al estilo de los datos de entrenamiento).</summary>
    string Phonemize(IReadOnlyList<Token> tokens);
}

internal static class Phonemizers
{
    public static IPhonemizer For(Language language, EnglishLexicon? english) => language switch
    {
        Language.Spanish => new SpanishPhonemizer(latam: false),
        Language.SpanishLatam => new SpanishPhonemizer(latam: true),
        Language.PortugueseBrazil => new PortuguesePhonemizer(),
        _ => new EnglishPhonemizer(english ?? throw new InvalidOperationException("Falta el léxico inglés."), british: language == Language.EnglishUk),
    };
}

/// <summary>Une palabras ya fonemizadas y signos de puntuación como lo espera el modelo: «ˈola, kˈe tˈal?».</summary>
internal static class Phones
{
    public const string Primary = "ˈ";
    public const string Secondary = "ˌ";

    public static string Join(IEnumerable<(bool IsPunct, string Text)> items)
    {
        StringBuilder sb = new();
        foreach ((bool punct, string text) in items)
        {
            if (text.Length == 0) continue;
            if (punct)
            {
                while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                if (text == "—") sb.Append(' ');
                sb.Append(text).Append(' ');
            }
            else
            {
                sb.Append(text).Append(' ');
            }
        }
        return Decompose(sb.ToString().Trim());
    }

    /// <summary>Las vocales nasales precompuestas («õ») como vocal + tilde combinante, que es lo que tiene el vocabulario.</summary>
    public static string Decompose(string s) =>
        s.Replace("ã", "a\u0303", StringComparison.Ordinal).Replace("õ", "o\u0303", StringComparison.Ordinal)
            .Replace("ũ", "u\u0303", StringComparison.Ordinal).Replace("ẽ", "e\u0303", StringComparison.Ordinal)
            .Replace("ĩ", "i\u0303", StringComparison.Ordinal);

    public static bool IsStress(string s) => s is Primary or Secondary;
}
