using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rodaje.Module.Translate.Translation;

namespace Rodaje.Module.Translate.Tests;

/// <summary>
/// Un «modelo» de prueba: responde a cada prompt con la función que se le dé. Por defecto traduce cada línea numerada
/// poniéndole «EN:» delante, como haría un modelo obediente. Con la segunda forma la función recibe también el tope
/// de tokens y dice si la respuesta llegó a él (un modelo que se corta).
/// </summary>
internal sealed partial class FakeGenerator : ITextGenerator
{
    private readonly Func<string, int, Generation> _respond;

    public FakeGenerator(Func<string, string>? respond = null)
    {
        Func<string, string> answer = respond ?? Obedient;
        _respond = (prompt, _) => new Generation(answer(prompt), false);
    }

    public FakeGenerator(Func<string, int, Generation> respond) => _respond = respond;

    public List<string> Prompts { get; } = [];

    /// <summary>El tope de tokens de cada llamada, en el mismo orden que <see cref="Prompts"/>.</summary>
    public List<int> Limits { get; } = [];

    public string Device => "falso";

    public Task<Generation> GenerateAsync(string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.InRange(maxTokens, 1, SubtitleTranslator.MaxRetryTokens);
        Prompts.Add(prompt);
        Limits.Add(maxTokens);
        return Task.FromResult(_respond(prompt, maxTokens));
    }

    /// <summary>Las líneas <c>[n] texto</c> del mensaje del usuario (sin las de contexto).</summary>
    public static List<(int N, string Text)> Items(string prompt)
    {
        string user = prompt[prompt.IndexOf("<|im_start|>user", StringComparison.Ordinal)..];
        return [.. Item().Matches(user).Select(m => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value))];
    }

    public static string Obedient(string prompt)
    {
        StringBuilder sb = new();
        foreach ((int n, string text) in Items(prompt))
        {
            sb.Append(CultureInfo.InvariantCulture, $"[{n}] EN: {text}\n");
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^\[(\d+)\] (.*)$", RegexOptions.Multiline)]
    private static partial Regex Item();
}
