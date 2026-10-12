using System.Text.Json;

namespace Rodaje.App.Services.Voice;

public sealed record VoiceLanguage(string Id, string Name);

/// <param name="Gender"><c>f</c>, <c>m</c> o vacío.</param>
public sealed record VoiceInfo(string Id, string Name, IReadOnlyList<string> Languages, string Gender, string Engine, string License)
{
    public bool Speaks(string language) => Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Lo que devuelve la función <c>voces</c>: idiomas y voces instaladas.</summary>
public sealed record VoiceCatalog(IReadOnlyList<VoiceLanguage> Languages, IReadOnlyList<VoiceInfo> Voices)
{
    public static readonly VoiceCatalog Empty = new([], []);

    public IReadOnlyList<VoiceInfo> VoicesFor(string language) => [.. Voices.Where(v => v.Speaks(language))];

    /// <summary>Lee la respuesta del módulo; lo que no se entiende se salta (el módulo no es de confianza).</summary>
    public static VoiceCatalog Parse(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } root)
        {
            return Empty;
        }

        List<VoiceLanguage> languages = [];
        if (root.TryGetProperty("languages", out JsonElement langs) && langs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement l in langs.EnumerateArray())
            {
                if (Str(l, "id") is { } id && Str(l, "name") is { } name)
                {
                    languages.Add(new VoiceLanguage(id, name));
                }
            }
        }

        List<VoiceInfo> voices = [];
        if (root.TryGetProperty("voices", out JsonElement vs) && vs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement v in vs.EnumerateArray())
            {
                if (Str(v, "id") is not { } id)
                {
                    continue;
                }

                List<string> speaks = [];
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("languages", out JsonElement ls) && ls.ValueKind == JsonValueKind.Array)
                {
                    speaks.AddRange(ls.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                }

                voices.Add(new VoiceInfo(id, Str(v, "name") ?? id, speaks, Str(v, "gender") ?? "", Str(v, "engine") ?? "", Str(v, "license") ?? ""));
            }
        }

        return new VoiceCatalog(languages, voices);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 200 } s
            ? s
            : null;
}
