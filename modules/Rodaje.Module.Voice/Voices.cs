using Rodaje.Module.Voice.Text;

namespace Rodaje.Module.Voice;

internal sealed record VoiceInfo(string Id, string Name, Language[] Languages, char Gender, string Engine = "kokoro", string License = "Apache-2.0");

/// <summary>Las voces que conoce el módulo y dónde están sus archivos (modelos\voz).</summary>
internal static class Voices
{
    public const string ModelFile = "kokoro-v1.0-uint8.onnx";

    private static readonly Language[] Es = [Language.Spanish, Language.SpanishLatam];
    private static readonly Language[] Pt = [Language.PortugueseBrazil];
    private static readonly Language[] Us = [Language.EnglishUs];
    private static readonly Language[] Gb = [Language.EnglishUk];

    public static readonly VoiceInfo[] All =
    [
        new("ef_dora", "Dora", Es, 'f'),
        new("em_alex", "Alex", Es, 'm'),
        new("em_santa", "Santa", Es, 'm'),
        new("pf_dora", "Dora", Pt, 'f'),
        new("pm_alex", "Alex", Pt, 'm'),
        new("pm_santa", "Santa", Pt, 'm'),
        new("af_heart", "Heart", Us, 'f'),
        new("af_bella", "Bella", Us, 'f'),
        new("af_nicole", "Nicole", Us, 'f'),
        new("af_sarah", "Sarah", Us, 'f'),
        new("af_sky", "Sky", Us, 'f'),
        new("af_alloy", "Alloy", Us, 'f'),
        new("af_aoede", "Aoede", Us, 'f'),
        new("af_jessica", "Jessica", Us, 'f'),
        new("af_kore", "Kore", Us, 'f'),
        new("af_nova", "Nova", Us, 'f'),
        new("af_river", "River", Us, 'f'),
        new("am_michael", "Michael", Us, 'm'),
        new("am_fenrir", "Fenrir", Us, 'm'),
        new("am_puck", "Puck", Us, 'm'),
        new("am_adam", "Adam", Us, 'm'),
        new("am_echo", "Echo", Us, 'm'),
        new("am_eric", "Eric", Us, 'm'),
        new("am_liam", "Liam", Us, 'm'),
        new("am_onyx", "Onyx", Us, 'm'),
        new("am_santa", "Santa", Us, 'm'),
        new("bf_emma", "Emma", Gb, 'f'),
        new("bf_isabella", "Isabella", Gb, 'f'),
        new("bf_alice", "Alice", Gb, 'f'),
        new("bf_lily", "Lily", Gb, 'f'),
        new("bm_george", "George", Gb, 'm'),
        new("bm_fable", "Fable", Gb, 'm'),
        new("bm_lewis", "Lewis", Gb, 'm'),
        new("bm_daniel", "Daniel", Gb, 'm'),
    ];

    public static VoiceInfo? Find(string? id) => All.FirstOrDefault(v => v.Id == id);

    public static string VoiceFile(string root, string id) => Path.Combine(root, "voces", id + ".bin");

    public static string LexiconFile(string root, string name) => Path.Combine(root, "misaki", name + ".json");

    /// <summary>
    /// La carpeta <c>voz</c> con el modelo: junto al exe (<c>modelos\voz</c>), en <c>RODAJE_MODELOS\voz</c> o, al correr
    /// desde el repositorio, en <c>.entorno\modelos\voz</c> de algún directorio superior.
    /// </summary>
    public static string? FindRoot(string? requested = null)
    {
        List<string> candidates = [];
        if (!string.IsNullOrWhiteSpace(requested)) candidates.Add(requested);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "modelos", "voz"));
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } env) candidates.Add(Path.Combine(env, "voz"));
        if (Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } entorno) candidates.Add(Path.Combine(entorno, "modelos", "voz"));
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            candidates.Add(Path.Combine(d.FullName, ".entorno", "modelos", "voz"));
            // Un worktree de .claude\worktrees usa el .entorno del repositorio principal.
            if (d.Name == "worktrees" && d.Parent?.Name == ".claude" && d.Parent.Parent is { } repo)
                candidates.Add(Path.Combine(repo.FullName, ".entorno", "modelos", "voz"));
        }
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, ModelFile)));
    }

    public static IEnumerable<VoiceInfo> Installed(string? root) =>
        root is null ? [] : All.Where(v => File.Exists(VoiceFile(root, v.Id)) && (!v.Languages.Any(l => l.IsEnglish()) || EnglishLexiconPresent(root, v.Languages[0])));

    public static bool EnglishLexiconPresent(string root, Language l) =>
        File.Exists(LexiconFile(root, l == Language.EnglishUk ? "gb_gold" : "us_gold"));
}
