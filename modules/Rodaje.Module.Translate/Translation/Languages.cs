namespace Rodaje.Module.Translate.Translation;

/// <summary>Un idioma que el traductor ofrece: código, nombre en inglés (para el modelo) y en español (para la app).</summary>
internal sealed record TranslationLanguage(string Code, string EnglishName, string SpanishName);

/// <summary>
/// Los idiomas que se ofrecen. Qwen3 conoce muchos más; estos son los que se probaron o se piden a menudo. El código es
/// BCP 47 en minúsculas salvo la región o el sistema de escritura (<c>pt-BR</c>, <c>zh-Hans</c>).
/// </summary>
internal static class Languages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } =
    [
        new("es", "Spanish", "Español"),
        new("en", "English", "Inglés"),
        new("pt-BR", "Brazilian Portuguese", "Portugués (Brasil)"),
        new("pt-PT", "European Portuguese", "Portugués (Portugal)"),
        new("fr", "French", "Francés"),
        new("it", "Italian", "Italiano"),
        new("de", "German", "Alemán"),
        new("nl", "Dutch", "Neerlandés"),
        new("ca", "Catalan", "Catalán"),
        new("gl", "Galician", "Gallego"),
        new("eu", "Basque", "Euskera"),
        new("pl", "Polish", "Polaco"),
        new("ru", "Russian", "Ruso"),
        new("uk", "Ukrainian", "Ucraniano"),
        new("tr", "Turkish", "Turco"),
        new("ar", "Arabic", "Árabe"),
        new("hi", "Hindi", "Hindi"),
        new("ja", "Japanese", "Japonés"),
        new("ko", "Korean", "Coreano"),
        new("zh-Hans", "Simplified Chinese", "Chino simplificado"),
    ];

    /// <summary>El idioma de <paramref name="code"/>, sin distinguir mayúsculas; <c>pt</c> vale como <c>pt-BR</c> y <c>zh</c> como <c>zh-Hans</c>.</summary>
    public static TranslationLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string wanted = code.Trim().Replace('_', '-') switch
        {
            var c when c.Equals("pt", StringComparison.OrdinalIgnoreCase) => "pt-BR",
            var c when c.Equals("zh", StringComparison.OrdinalIgnoreCase) || c.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) => "zh-Hans",
            var c => c,
        };
        return All.FirstOrDefault(l => l.Code.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }
}
