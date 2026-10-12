using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Rodaje.App.Services.Voice;

/// <summary>Lo que se pide al módulo de voz. Es también el archivo <c>.json</c> que acompaña a cada WAV generado.</summary>
public sealed record VoiceSettings(
    string Text,
    string Language = "es-419",
    string Voice = "ef_dora",
    double Speed = 1,
    double Pitch = 0,
    double SentencePause = 0.4,
    double CommaPause = 0.15)
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2;
    public const double MaxPitch = 6;
    public const double MaxPause = 3;

    /// <summary>Marca que identifica el JSON como de este generador.</summary>
    public const string Generator = "modulo-voz";

    /// <summary>Los valores dentro de lo que acepta el módulo.</summary>
    public VoiceSettings Clamped() => this with
    {
        Speed = Clamp(Speed, MinSpeed, MaxSpeed, 1),
        Pitch = Clamp(Pitch, -MaxPitch, MaxPitch, 0),
        SentencePause = Clamp(SentencePause, 0, MaxPause, 0.4),
        CommaPause = Clamp(CommaPause, 0, MaxPause, 0.15),
    };

    /// <summary>Cambió algo que se oye (no cuenta un espacio de más al final del texto).</summary>
    public bool SoundsDifferent(VoiceSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Clamped() with { Text = Text.Trim() } != other.Clamped() with { Text = other.Text.Trim() };
    }

    /// <summary>Los parámetros de la función <c>voz</c>.</summary>
    public JsonElement ToParameters() => Json(includeGenerator: false);

    /// <summary>El contenido del <c>.json</c> que va junto al WAV.</summary>
    public string ToSidecar() => Json(includeGenerator: true).GetRawText();

    /// <summary>Lee un <c>.json</c> de voz; <c>null</c> si no es de este generador o está dañado.</summary>
    public static VoiceSettings? FromSidecar(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("generator", out JsonElement g) || g.GetString() != Generator
                || !root.TryGetProperty("text", out JsonElement text) || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var defaults = new VoiceSettings("");
            return new VoiceSettings(
                text.GetString()!,
                Str(root, "language") ?? defaults.Language,
                Str(root, "voice") ?? defaults.Voice,
                Num(root, "speed") ?? defaults.Speed,
                Num(root, "pitch") ?? defaults.Pitch,
                Num(root, "sentencePause") ?? defaults.SentencePause,
                Num(root, "commaPause") ?? defaults.CommaPause).Clamped();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Duración aproximada por encima (para reservar el búfer de salida): unos 13 caracteres por segundo más pausas.</summary>
    public double EstimateSeconds()
    {
        VoiceSettings s = Clamped();
        int sentences = 0, commas = 0;
        foreach (char c in s.Text)
        {
            if (c is '.' or '!' or '?' or '\n')
            {
                sentences++;
            }
            else if (c is ',' or ';' or ':')
            {
                commas++;
            }
        }

        double explicitPauses = 0;
        int at = 0;
        while ((at = s.Text.IndexOf("[pausa", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = s.Text.IndexOf(']', at);
            string inner = end > at ? s.Text[(at + 6)..end].Trim().Replace(',', '.') : "";
            explicitPauses += double.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, 0, 30) : 0.5;
            at += 6;
        }

        return 2 + (s.Text.Length / 11.0 / s.Speed) + (sentences * s.SentencePause) + (commas * s.CommaPause) + explicitPauses;
    }

    private JsonElement Json(bool includeGenerator)
    {
        VoiceSettings s = Clamped();
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = includeGenerator, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            if (includeGenerator)
            {
                w.WriteString("generator", Generator);
            }

            w.WriteString("text", s.Text);
            w.WriteString("language", s.Language);
            w.WriteString("voice", s.Voice);
            w.WriteNumber("speed", Math.Round(s.Speed, 3));
            w.WriteNumber("pitch", Math.Round(s.Pitch, 2));
            w.WriteNumber("sentencePause", Math.Round(s.SentencePause, 3));
            w.WriteNumber("commaPause", Math.Round(s.CommaPause, 3));
            w.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        return doc.RootElement.Clone();
    }

    private static double Clamp(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
