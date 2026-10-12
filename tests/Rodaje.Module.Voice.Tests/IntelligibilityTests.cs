using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;
using Rodaje.Modules.Protocol;
using Xunit.Abstractions;

namespace Rodaje.Module.Voice.Tests;

/// <summary>Solo corre con los modelos de voz y de Whisper (<c>herramientas\preparar-modelos.ps1</c>).</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public WhisperFactAttribute()
    {
        if (Models.Root is null || Intelligibility.WhisperModel is null)
            Skip = "Omitida: faltan los modelos de voz o de Whisper; ejecuta herramientas\\preparar-modelos.ps1.";
    }
}

internal static class Intelligibility
{
    public static readonly string? WhisperModel = Models.Root is { } voz && Path.Combine(Path.GetDirectoryName(voz)!, "ggml-large-v3-turbo-q5_0.bin") is { } m && File.Exists(m) ? m : null;

    public static string Normalize(string s)
    {
        StringBuilder sb = new();
        foreach (char c in s.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString();
    }

    public static double Wer(string reference, string hypothesis)
    {
        string[] r = Normalize(reference).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] h = Normalize(hypothesis).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int[,] d = new int[r.Length + 1, h.Length + 1];
        for (int i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= h.Length; j++) d[0, j] = j;
        for (int i = 1; i <= r.Length; i++)
            for (int j = 1; j <= h.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
        return (double)d[r.Length, h.Length] / r.Length;
    }
}

/// <summary>La voz generada, transcrita por modulo-subtitulos (Whisper large-v3-turbo): tasa de error por palabra.</summary>
[Collection("Kokoro")]
public sealed class IntelligibilityTests(ITestOutputHelper output)
{
    private static readonly (Language L, string Whisper, string Voice, string Text)[] Corpus =
    [
        (Language.SpanishLatam, "es", "ef_dora", "El editor corta el video, ajusta el color y exporta el resultado en pocos segundos. Mañana grabaremos una entrevista en la ciudad."),
        (Language.Spanish, "es", "em_alex", "Los niños jugaban en el parque mientras su abuela leía el periódico junto a la fuente."),
        (Language.PortugueseBrazil, "pt", "pf_dora", "O editor corta o vídeo, ajusta a cor e exporta o resultado em poucos segundos. Amanhã vamos gravar uma entrevista na cidade."),
        (Language.PortugueseBrazil, "pt", "pm_alex", "As crianças brincavam no parque enquanto a avó lia o jornal perto da fonte."),
        (Language.EnglishUs, "en", "af_heart", "The editor cuts the video, adjusts the color and exports the result in a few seconds. Tomorrow we will record an interview in the city."),
    ];

    [WhisperFact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Whisper_understands_the_generated_voice()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Subtitles.exe");
        Environment.SetEnvironmentVariable("RODAJE_MODELOS", Path.GetDirectoryName(Intelligibility.WhisperModel));
        await using ModuleProcess whisper = await ModuleProcess.StartAsync(exe);
        double worst = 0;
        foreach (var (l, lang, voice, text) in Corpus)
        {
            SpeechResult speech = Models.Synth.Speak(new SpeechRequest(text, l, voice), null, CancellationToken.None);
            float[] audio16 = AudioOps.Resample(speech.Audio, 24_000.0 / 16_000);
            SharedMemoryRegion region = whisper.Client.CreateRegion(audio16.Length * 4L);
            MemoryMarshal.AsBytes(audio16.AsSpan()).CopyTo(region.GetSpan(0, audio16.Length * 4L));
            using JsonDocument p = JsonDocument.Parse($$"""{"language":"{{lang}}"}""");
            ModuleResult r = await whisper.Client.InvokeAsync("subtitulos", p.RootElement.Clone(),
                [region.Describe(0, audio16.Length * 4L, audio: new AudioLayout(16_000, 1, audio16.Length))]);
            string heard = string.Join(" ", r.Value!.Value.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("text").GetString()));
            whisper.Client.ReleaseRegion(region);
            double wer = Intelligibility.Wer(text, heard);
            worst = Math.Max(worst, wer);
            output.WriteLine($"{Languages.Id(l)} {voice}: WER {wer:P0} — «{heard.Trim()}»");
        }
        Assert.True(worst <= 0.2, $"peor WER {worst:P0}");
    }
}
