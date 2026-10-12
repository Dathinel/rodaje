using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rodaje.Module.Translate.Translation;

namespace Rodaje.Module.Translate.Tests;

/// <summary>
/// Un «modelo» de prueba: responde a cada prompt con la función que se le dé. Por defecto traduce cada línea numerada
/// poniéndole «EN:» delante, como haría un modelo obediente.
/// </summary>
internal sealed partial class FakeGenerator(Func<string, string>? respond = null) : ITextGenerator
{
    public List<string> Prompts { get; } = [];

    public string Device => "falso";

    public Task<string> GenerateAsync(string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.InRange(maxTokens, 1, 1200);
        Prompts.Add(prompt);
        return Task.FromResult((respond ?? Obedient)(prompt));
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
