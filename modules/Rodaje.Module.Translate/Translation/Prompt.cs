using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rodaje.Module.Translate.Translation;

/// <summary>
/// Las instrucciones para el modelo, en el formato de chat de Qwen (ChatML), y la lectura de su respuesta. Cada
/// subtítulo va numerado (<c>[1] texto</c>) y se pide la respuesta con los mismos números: así un lote conserva qué
/// traducción es de qué subtítulo aunque el modelo junte o parta frases. Las líneas anteriores van como contexto, sin
/// número, para que el modelo entienda frases que siguen de un subtítulo al siguiente.
/// </summary>
internal static partial class Prompt
{
    public const string EndOfTurn = "<|im_end|>";

    public static string Batch(IReadOnlyList<string> texts, IReadOnlyList<string> context, TranslationLanguage? source, TranslationLanguage target)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);
        string from = source?.EnglishName ?? "the source language (detect it)";
        StringBuilder user = new();
        user.Append(CultureInfo.InvariantCulture, $"Translate these video subtitles from {from} to {target.EnglishName}.\n");
        if (context.Count > 0)
        {
            user.Append("Previous subtitles, for context only (do not translate them):\n");
            foreach (string line in context)
            {
                user.Append("> ").Append(UserText(line)).Append('\n');
            }
        }

        user.Append("Subtitles:\n");
        for (int i = 0; i < texts.Count; i++)
        {
            user.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {UserText(texts[i])}\n");
        }

        return Chat(System(target), user.ToString());
    }

    public static string Single(string text, TranslationLanguage? source, TranslationLanguage target)
    {
        string from = source?.EnglishName ?? "the source language (detect it)";
        string user = string.Create(CultureInfo.InvariantCulture,
            $"Translate this video subtitle from {from} to {target.EnglishName}.\nSubtitles:\n[1] {UserText(text)}\n");
        return Chat(System(target), user);
    }

    /// <summary>
    /// Las traducciones numeradas de la respuesta. Una línea sin número sigue a la anterior. Devuelve <c>null</c> en
    /// las posiciones que faltan o se repiten, para traducirlas solas después.
    /// </summary>
    public static string?[] Parse(string response, int count)
    {
        ArgumentNullException.ThrowIfNull(response);
        string?[] result = new string?[count];
        bool[] repeated = new bool[count];
        int current = -1;
        foreach (string raw in StripThinking(response).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match m = Numbered().Match(line);
            int n = m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
            if (m.Success && (n < 1 || n > count))
            {
                // Un número que no se pidió: esa línea y lo que la siga no son de ningún subtítulo.
                current = -1;
            }
            else if (m.Success)
            {
                current = n - 1;
                if (result[current] is not null)
                {
                    repeated[current] = true;
                }

                result[current] = Clean(m.Groups[2].Value);
            }
            else if (current >= 0)
            {
                result[current] = Clean(result[current] + " " + line);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (repeated[i])
            {
                result[i] = null;
            }
        }

        return result;
    }

    /// <summary>La respuesta a <see cref="Single"/>: con número o sin él.</summary>
    public static string ParseSingle(string response)
    {
        string text = StripThinking(response).Trim();
        Match m = Numbered().Match(text);
        return Clean(m.Success ? m.Groups[2].Value + " " + text[m.Length..] : text);
    }

    /// <summary>Sin comillas que envuelvan toda la frase (el modelo a veces las agrega) ni espacios repetidos.</summary>
    public static string Clean(string? text)
    {
        string s = LineWrapper.Normalize(text ?? "");
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '“' && s[^1] == '”') || (s[0] == '«' && s[^1] == '»'))
            && s.AsSpan(1, s.Length - 2).IndexOfAny("\"“”«»") < 0)
        {
            s = s[1..^1].Trim();
        }

        return s;
    }

    /// <summary>
    /// Un texto del usuario listo para ir en el prompt. Va sin plantilla (<c>ApplyTemplate = false</c>), así que un
    /// subtítulo con <c>&lt;|im_end|&gt;&lt;|im_start|&gt;</c> cerraría el turno, y uno con <c>&lt;/think&gt;</c> haría
    /// que <see cref="StripThinking"/> descartara lo anterior: se separan con un espacio para que no sean marcas.
    /// </summary>
    public static string UserText(string text) =>
        LineWrapper.Normalize(text).Replace("<|", "< |", StringComparison.Ordinal).Replace("</think>", "< /think>", StringComparison.Ordinal);

    private static string System(TranslationLanguage target) =>
        "You are a professional subtitle translator. Translate each numbered subtitle into natural, fluent " + target.EnglishName
        + " that fits on screen: keep it about as short as the original, keep names, numbers and tone, and do not add notes or explanations. "
        + "Answer only with the translated subtitles, one per line, each starting with the same [number] as its original. "
        + "Never merge or skip subtitles: if a sentence continues in the next subtitle, translate each part in its own line.";

    private static string Chat(string system, string user) =>
        $"<|im_start|>system\n{system}{EndOfTurn}\n<|im_start|>user\n{user}{EndOfTurn}\n<|im_start|>assistant\n";

    /// <summary>Qwen3 con razonamiento escribe <c>&lt;think&gt;…&lt;/think&gt;</c> antes de responder; la variante Instruct-2507 no, pero por si acaso.</summary>
    private static string StripThinking(string response)
    {
        int end = response.LastIndexOf("</think>", StringComparison.Ordinal);
        string s = end >= 0 ? response[(end + "</think>".Length)..] : response;
        int stop = s.IndexOf(EndOfTurn, StringComparison.Ordinal);
        return stop >= 0 ? s[..stop] : s;
    }

    [GeneratedRegex(@"^\[(\d{1,3})\]\s*(.*)$")]
    private static partial Regex Numbered();
}
