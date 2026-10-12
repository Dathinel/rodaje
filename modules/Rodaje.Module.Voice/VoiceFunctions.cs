using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Voice;

/// <summary>Las funciones del módulo: <c>voces</c>, <c>voz</c> y <c>entregar</c>.</summary>
internal sealed class VoiceFunctions(string? root) : IDisposable
{
    private const int MaxStored = 4;

    private readonly ConcurrentDictionary<string, (float[] Audio, long Order)> _stored = new(StringComparer.Ordinal);
    private long _order;
    private Synthesizer? _synth;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _busy = new(1, 1);

    public static readonly ModuleInfo Info = new("modulo-voz", "1.0.0", "Texto a voz",
    [
        new FunctionInfo("voces", "Idiomas y voces instaladas", Cancellable: false, ReportsProgress: false),
        new FunctionInfo("voz", "Convertir texto en voz", Outputs: ["audio"]),
        new FunctionInfo("entregar", "Copiar un audio ya generado", Cancellable: false, ReportsProgress: false, Outputs: ["audio"]),
    ], Device: "cpu");

    public ValueTask<ModuleOutput> VoicesAsync(ModuleCall call, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleOutput(Catalog(root)));

    public static JsonElement Catalog(string? root)
    {
        VoiceInfo[] installed = [.. Voices.Installed(root)];
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("languages");
            foreach (var l in Languages.All.Where(l => installed.Any(v => v.Languages.Contains(l.Value))))
            {
                w.WriteStartObject();
                w.WriteString("id", l.Id);
                w.WriteString("name", l.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("voices");
            foreach (VoiceInfo v in installed)
            {
                w.WriteStartObject();
                w.WriteString("id", v.Id);
                w.WriteString("name", v.Name);
                w.WriteStartArray("languages");
                foreach (Language l in v.Languages) w.WriteStringValue(Languages.Id(l));
                w.WriteEndArray();
                w.WriteString("gender", v.Gender.ToString());
                w.WriteString("engine", v.Engine);
                w.WriteString("license", v.License);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    public async ValueTask<ModuleOutput> SpeakAsync(ModuleCall call, CancellationToken cancellationToken)
    {
        SpeechRequest request = ParseRequest(call.Parameters);
        Synthesizer synth = Synth();
        // Kokoro ya usa todos los núcleos que se le dan: una síntesis a la vez; la siguiente espera.
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        SpeechResult result;
        try
        {
            // El avance se manda desde el hilo de la síntesis sin esperarlo.
            result = await Task.Run(() => synth.Speak(request,
                (f, stage) => _ = call.ReportProgressAsync(f, stage, cancellationToken).AsTask(), cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }

        bool written = false;
        string? token = null;
        List<SharedBufferRef> buffers = [];
        long bytes = result.Audio.LongLength * 4;
        if (call.Buffers.Count > 0 && call.Buffers[0].Length >= bytes && result.Audio.Length > 0)
        {
            SharedBufferRef target = call.Buffers[0];
            MemoryMarshal.AsBytes(result.Audio.AsSpan()).CopyTo(call.Map(target));
            buffers.Add(target with { Length = bytes, Video = null, Audio = new AudioLayout(result.SampleRate, 1, result.Audio.Length) });
            written = true;
        }
        else if (result.Audio.Length > 0)
        {
            token = Store(result.Audio);
        }

        JsonElement value = Json(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("sampleRate", result.SampleRate);
            w.WriteNumber("frames", result.Audio.Length);
            w.WriteBoolean("written", written);
            if (token is not null) w.WriteString("audio", token);
            WriteTimed(w, "segments", result.Segments);
            WriteTimed(w, "words", result.Words);
            w.WriteEndObject();
        });
        return new ModuleOutput(value, buffers);
    }

    public ValueTask<ModuleOutput> DeliverAsync(ModuleCall call, CancellationToken cancellationToken)
    {
        string token = String(call.Parameters, "audio") ?? throw Invalid("Falta «audio».");
        if (!_stored.TryRemove(token, out var stored)) throw Invalid("Ese audio ya no está: vuelve a generarlo.");
        long bytes = stored.Audio.LongLength * 4;
        if (call.Buffers.Count == 0 || call.Buffers[0].Length < bytes)
        {
            _stored[token] = stored;
            throw Invalid($"El búfer tiene que tener {bytes} bytes.");
        }
        SharedBufferRef target = call.Buffers[0];
        MemoryMarshal.AsBytes(stored.Audio.AsSpan()).CopyTo(call.Map(target));
        return ValueTask.FromResult(new ModuleOutput(Buffers:
            [target with { Length = bytes, Video = null, Audio = new AudioLayout(KokoroEngine.SampleRate, 1, stored.Audio.Length) }]));
    }

    internal static SpeechRequest ParseRequest(JsonElement? parameters)
    {
        string text = String(parameters, "text") ?? throw Invalid("Falta el texto.");
        if (string.IsNullOrWhiteSpace(text)) throw Invalid("El texto está vacío.");
        if (text.Length > Synthesizer.MaxTextLength) throw Invalid($"El texto pasa de {Synthesizer.MaxTextLength} caracteres; pártelo en varios clips.");
        string languageId = String(parameters, "language") ?? "es";
        if (!Languages.TryParse(languageId, out Language language)) throw Invalid($"Idioma desconocido: {languageId}.");
        string voiceId = String(parameters, "voice") ?? Voices.All.First(v => v.Languages.Contains(language)).Id;
        if (Voices.Find(voiceId) is null) throw Invalid($"Voz desconocida: {voiceId}.");
        return new SpeechRequest(text, language, voiceId,
            Number(parameters, "speed", 1, Synthesizer.MinSpeed, Synthesizer.MaxSpeed),
            Number(parameters, "pitch", 0, -Synthesizer.MaxPitch, Synthesizer.MaxPitch),
            Number(parameters, "sentencePause", 0.4, 0, Script.MaxPause),
            Number(parameters, "commaPause", 0, 0, Script.MaxPause));
    }

    private Synthesizer Synth()
    {
        lock (_gate)
        {
            if (_synth is not null) return _synth;
            string dir = root ?? throw new ModuleCallException(ErrorCodes.InvalidParameters, "Falta el modelo de voz: reinstala el módulo de voz.");
            return _synth = new Synthesizer(dir);
        }
    }

    private string Store(float[] audio)
    {
        string token = Guid.NewGuid().ToString("N");
        _stored[token] = (audio, Interlocked.Increment(ref _order));
        while (_stored.Count > MaxStored)
        {
            var oldest = _stored.MinBy(kv => kv.Value.Order);
            _stored.TryRemove(oldest.Key, out _);
        }
        return token;
    }

    private static void WriteTimed(Utf8JsonWriter w, string name, IReadOnlyList<TimedText> items)
    {
        w.WriteStartArray(name);
        foreach (TimedText t in items)
        {
            w.WriteStartObject();
            w.WriteNumber("start", Math.Round(t.Start, 3));
            w.WriteNumber("end", Math.Round(t.End, 3));
            w.WriteString("text", t.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static ModuleCallException Invalid(string message) => new(ErrorCodes.InvalidParameters, message);

    private static string? String(JsonElement? p, string name) =>
        p is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Number(JsonElement? p, string name, double fallback, double min, double max)
    {
        if (p is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty(name, out JsonElement v)) return fallback;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d) || !double.IsFinite(d)) throw Invalid($"«{name}» tiene que ser un número.");
        return Math.Clamp(d, min, max);
    }

    internal static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter w = new(ms)) write(w);
        using JsonDocument doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        _synth?.Dispose();
        _busy.Dispose();
    }
}
