using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.App.Services.Modules;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Services.Voice;

/// <summary>Una palabra o frase con su tramo en segundos del audio generado (aproximado).</summary>
public sealed record VoiceSpan(double Start, double End, string Text);

/// <summary>Audio mono f32 que devolvió el módulo, con sus frases y palabras.</summary>
public sealed record SynthesizedVoice(float[] Samples, int SampleRate, IReadOnlyList<VoiceSpan> Segments, IReadOnlyList<VoiceSpan> Words)
{
    public double Seconds => Samples.Length / (double)SampleRate;
}

/// <summary>El módulo devolvió algo que no se puede usar como audio.</summary>
public sealed class VoiceException : Exception
{
    public VoiceException() { }

    public VoiceException(string message) : base(message) { }

    public VoiceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Lo que la síntesis necesita de una llamada de módulo (así se prueba sin un proceso).</summary>
public interface IVoiceModuleCall
{
    /// <summary>Un búfer nuevo de audio f32 mono para <paramref name="frames"/> muestras.</summary>
    SharedBufferRef CreateAudioBuffer(long frames, int sampleRate);

    Span<byte> Map(SharedBufferRef buffer);

    Task<ModuleResult> InvokeAsync(string functionId, JsonElement? parameters, IReadOnlyList<SharedBufferRef>? buffers, double progressFrom, double progressTo);
}

/// <summary>
/// El diálogo con <c>modulo-voz</c>: pide <c>voz</c> con un búfer del tamaño estimado; si el audio no cupo, lo trae con
/// <c>entregar</c> en un búfer exacto. Desconfía de lo que vuelve: tasa, largo y búferes se comprueban.
/// </summary>
public static class VoiceSynthesis
{
    public const string ModuleId = "modulo-voz";
    public const string VoicesFunction = "voces";
    public const string SpeakFunction = "voz";
    public const string DeliverFunction = "entregar";
    public const int SampleRate = 24_000;

    /// <summary>El búfer de la primera llamada no pasa de 10 min; lo más largo se trae con <c>entregar</c>.</summary>
    public const long MaxFirstBufferFrames = SampleRate * 600L;

    /// <summary>Tope de lo que se acepta del módulo: una hora de audio.</summary>
    public const long MaxFrames = 192_000L * 3600;

    public static async Task<SynthesizedVoice> SynthesizeAsync(IVoiceModuleCall call, VoiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(settings);
        VoiceSettings s = settings.Clamped();
        if (string.IsNullOrWhiteSpace(s.Text))
        {
            throw new VoiceException("Escribe el texto que se va a leer.");
        }

        long guess = Math.Clamp((long)Math.Ceiling(s.EstimateSeconds() * SampleRate), SampleRate, MaxFirstBufferFrames);
        SharedBufferRef output = call.CreateAudioBuffer(guess, SampleRate);
        ModuleResult result = await call.InvokeAsync(SpeakFunction, s.ToParameters(), [output], 0, 0.95).ConfigureAwait(false);
        JsonElement value = result.Value is { ValueKind: JsonValueKind.Object } v ? v : throw new VoiceException("El módulo de voz no devolvió el audio.");

        int rate = Int(value, "sampleRate") is int r and >= 8_000 and <= 192_000 ? r : throw new VoiceException("Tasa de muestreo inválida.");
        long frames = Long(value, "frames") is long f and > 0 and <= MaxFrames ? f : throw new VoiceException("El módulo no generó audio.");
        bool written = value.TryGetProperty("written", out JsonElement w) && w.ValueKind == JsonValueKind.True;

        float[] samples;
        if (written)
        {
            SharedBufferRef returned = result.Buffers.Count == 1 ? result.Buffers[0] : output;
            samples = Read(call, returned, frames);
        }
        else
        {
            string token = value.TryGetProperty("audio", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tok
                ? tok
                : throw new VoiceException("El módulo no dijo dónde está el audio.");
            SharedBufferRef exact = call.CreateAudioBuffer(frames, rate);
            ModuleResult delivered = await call.InvokeAsync(DeliverFunction, TokenParameters(token), [exact], 0.95, 1).ConfigureAwait(false);
            samples = Read(call, delivered.Buffers.Count == 1 ? delivered.Buffers[0] : exact, frames);
        }

        return new SynthesizedVoice(samples, rate, Spans(value, "segments"), Spans(value, "words"));
    }

    private static JsonElement TokenParameters(string token)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("audio", token);
            w.WriteEndObject();
        }

        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    private static float[] Read(IVoiceModuleCall call, SharedBufferRef buffer, long frames)
    {
        Span<byte> bytes = call.Map(buffer);
        if (bytes.Length < frames * sizeof(float))
        {
            throw new VoiceException("El audio no cabe en el búfer que devolvió el módulo.");
        }

        float[] samples = MemoryMarshal.Cast<byte, float>(bytes[..checked((int)(frames * sizeof(float)))]).ToArray();
        for (int i = 0; i < samples.Length; i++)
        {
            float x = samples[i];
            samples[i] = float.IsFinite(x) ? Math.Clamp(x, -1f, 1f) : 0f;
        }

        return samples;
    }

    private static List<VoiceSpan> Spans(JsonElement root, string name)
    {
        List<VoiceSpan> list = [];
        if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement e in array.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("start", out JsonElement a) && a.TryGetDouble(out double start)
                && e.TryGetProperty("end", out JsonElement b) && b.TryGetDouble(out double end)
                && e.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String
                && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end >= start)
            {
                list.Add(new VoiceSpan(start, end, t.GetString()!));
            }
        }

        return list;
    }

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : null;

    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt64(out long n) ? n : null;
}

/// <summary>Quien genera la voz: el módulo de verdad o uno falso en las pruebas.</summary>
public interface IVoiceSynthesizer
{
    bool IsInstalled { get; }

    Task<VoiceCatalog> GetVoicesAsync(CancellationToken cancellationToken);

    /// <param name="title">La fila de Trabajos.</param>
    Task<SynthesizedVoice> SynthesizeAsync(VoiceSettings settings, string title, CancellationToken cancellationToken);
}

/// <summary><see cref="IVoiceSynthesizer"/> sobre el <see cref="ModuleManager"/>.</summary>
public sealed class ModuleVoiceSynthesizer(ModuleManager modules) : IVoiceSynthesizer
{
    public const string NotInstalled = "Instala el módulo de voz (Complementos → IA) para generar voz a partir de texto.";

    public bool IsInstalled => modules.Find(VoiceSynthesis.ModuleId) is not null;

    public async Task<VoiceCatalog> GetVoicesAsync(CancellationToken cancellationToken)
    {
        EnsureInstalled();
        ModuleResult r = await modules.InvokeAsync(VoiceSynthesis.ModuleId, VoiceSynthesis.VoicesFunction, null, "Voces disponibles", cancellationToken).ConfigureAwait(false);
        return VoiceCatalog.Parse(r.Value);
    }

    public Task<SynthesizedVoice> SynthesizeAsync(VoiceSettings settings, string title, CancellationToken cancellationToken)
    {
        EnsureInstalled();
        return modules.RunAsync(VoiceSynthesis.ModuleId, title, call => VoiceSynthesis.SynthesizeAsync(new Adapter(call), settings), cancellationToken);
    }

    private void EnsureInstalled()
    {
        if (!IsInstalled)
        {
            throw new VoiceException(NotInstalled);
        }
    }

    private sealed class Adapter(Rodaje.App.Services.Modules.ModuleCall call) : IVoiceModuleCall
    {
        public SharedBufferRef CreateAudioBuffer(long frames, int sampleRate) =>
            call.CreateRegion(frames * sizeof(float)).Describe(0, frames * sizeof(float), audio: new AudioLayout(sampleRate, 1, frames));

        public Span<byte> Map(SharedBufferRef buffer) => call.Map(buffer);

        public Task<ModuleResult> InvokeAsync(string functionId, JsonElement? parameters, IReadOnlyList<SharedBufferRef>? buffers, double progressFrom, double progressTo) =>
            call.InvokeAsync(functionId, parameters, buffers, progressFrom, progressTo);
    }
}
