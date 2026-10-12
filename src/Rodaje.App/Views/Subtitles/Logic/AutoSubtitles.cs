using System.Text;
using System.Text.Json;
using Rodaje.Analysis;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Modules;
using Rodaje.App.Views.Analysis;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Modules.Protocol;

namespace Rodaje.App.Views.Subtitles;

/// <summary>Una palabra del resultado de <c>subtitulos</c>, en segundos del audio entregado al módulo.</summary>
internal sealed record TranscriptWord(double Start, double End, string Text);

/// <summary>Un segmento del resultado de <c>subtitulos</c>; <see cref="Words"/> puede venir vacío.</summary>
internal sealed record TranscriptSegment(double Start, double End, string Text, IReadOnlyList<TranscriptWord> Words);

/// <summary>Lo que devuelve modulo-subtitulos (formato en <c>modules/Rodaje.Module.Subtitles/README.md</c>).</summary>
internal sealed record Transcript(string? Language, IReadOnlyList<TranscriptSegment> Segments);

/// <summary>Un idioma que se puede pedir al módulo (<c>auto</c> lo detecta).</summary>
internal sealed record SubtitleLanguage(string Id, string Name);

/// <summary>
/// «Subtítulos automáticos» sin interfaz: elige el clip, baja su audio a f32 mono de 16 kHz con <see cref="ModuleMedia"/>,
/// pide <c>subtitulos</c> a modulo-subtitulos por el <see cref="ModuleManager"/>, lleva los segundos del módulo a la
/// secuencia con el mapa de tiempo del clip (velocidad y reversa incluidas) y arma el paso del historial que pone los
/// subtítulos con <see cref="SubtitleEdits.AddSubtitles"/> y guarda en cada uno sus palabras (<c>generator.words</c>).
/// </summary>
internal static class AutoSubtitles
{
    public const string Function = "subtitulos";
    public const int SampleRate = 16_000;
    public const string Description = "Subtítulos automáticos";

    /// <summary>Lo que ocupa el resultado como mucho: 1 MiB más 4 KiB por segundo (unas 30 palabras con sus tiempos).</summary>
    public const long BaseResultBytes = 1 << 20;
    public const long ResultBytesPerSecond = 4096;

    public static IReadOnlyList<SubtitleLanguage> Languages { get; } =
    [
        new("auto", "Detectar el idioma"),
        new("es", "Español"),
        new("en", "Inglés"),
        new("pt", "Portugués"),
    ];

    /// <summary>
    /// El clip cuyo audio se transcribe: el de audio que elegiría «Reducir ruido» (el seleccionado o el audio enlazado de un
    /// video) o, si no hay, el primer clip seleccionado con sonido (un video sin su audio separado).
    /// </summary>
    public static Clip? TargetClip(Sequence sequence, IReadOnlyList<string> selection, Func<Clip, bool> hasAudio)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(hasAudio);
        if (NoiseReduction.TargetClip(sequence, selection, hasAudio) is { } audio)
        {
            return audio;
        }

        foreach (string id in selection)
        {
            if (sequence.FindClip(id) is { Clip: { Generator: null } clip } && hasAudio(clip))
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>Tramo del medio que se entrega: el que usa el clip.</summary>
    public static (Time Start, Time End) Window(Clip clip) => ClipAudioMap.SourceWindow(clip);

    /// <summary>Tamaño del búfer de salida para un tramo de <paramref name="span"/>.</summary>
    public static long ResultCapacity(Time span) => BaseResultBytes + (long)Math.Ceiling(Math.Max(0, span.ToSeconds())) * ResultBytesPerSecond;

    /// <summary>Los parámetros de la llamada: <c>{"language": "es"}</c>.</summary>
    public static JsonDocument Parameters(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("language", language);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    /// <summary>
    /// Transcribe <paramref name="window"/> del medio. Es un trabajo de Trabajos (avance, cancelar); el resultado va al
    /// búfer de salida (<c>value</c> = <c>{"bytes": n}</c>) o, si el módulo lo devuelve en línea, en <c>value</c>.
    /// </summary>
    public static Task<Transcript> RunAsync(ModuleManager modules, ModuleDescriptor module, string sourcePath, (Time Start, Time End) window, string language,
        string title, CancellationToken cancellationToken, Action<ModuleJob>? started = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(module);
        return modules.RunAsync(module.Id, title, async call =>
        {
            started?.Invoke(call.Job);
            SharedBufferRef audio = await ModuleMedia.WriteAudioAsync(call, sourcePath, window.Start, window.End, SampleRate, 1, 0, 0.1).ConfigureAwait(false);
            long capacity = ResultCapacity(window.End - window.Start);
            SharedBufferRef output = call.CreateRegion(capacity).Describe(0, capacity);
            using JsonDocument parameters = Parameters(language);
            call.Report(0.1, "Transcribiendo");
            ModuleResult result = await call.InvokeAsync(Function, parameters.RootElement, [audio, output], 0.1, 1).ConfigureAwait(false);
            return result.Buffers.Count > 0 ? FromResult(result.Value, call.Map(result.Buffers[0])) : FromResult(result.Value, []);
        }, cancellationToken);
    }

    /// <summary>Lee el resultado: el JSON de los primeros <c>bytes</c> de <paramref name="buffer"/> o el de <paramref name="value"/>.</summary>
    public static Transcript FromResult(JsonElement? value, ReadOnlySpan<byte> buffer)
    {
        if (value is not { ValueKind: JsonValueKind.Object } v)
        {
            throw new ModuleProtocolException("«subtitulos» no devolvió un resultado.");
        }

        if (v.TryGetProperty("bytes", out JsonElement size))
        {
            if (!size.TryGetInt64(out long n) || n < 0 || n > buffer.Length)
            {
                throw new ModuleProtocolException("«subtitulos» devolvió un tamaño de resultado inesperado.");
            }

            return Parse(buffer[..(int)n]);
        }

        return Parse(v);
    }

    public static Transcript Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ModuleProtocolException("«subtitulos» devolvió un JSON que no se puede leer: " + ex.Message);
        }
    }

    /// <summary>Lee la transcripción; se salta los segmentos y palabras sin tiempos válidos o sin texto.</summary>
    public static Transcript Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("segments", out JsonElement segments) || segments.ValueKind != JsonValueKind.Array)
        {
            throw new ModuleProtocolException("«subtitulos» devolvió un resultado sin segmentos.");
        }

        string? language = root.TryGetProperty("language", out JsonElement l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        List<TranscriptSegment> list = [];
        foreach (JsonElement s in segments.EnumerateArray())
        {
            if (!TryTimes(s, out double start, out double end) || Text(s) is not { } text)
            {
                continue;
            }

            List<TranscriptWord> words = [];
            if (s.TryGetProperty("words", out JsonElement ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement w in ws.EnumerateArray())
                {
                    if (TryTimes(w, out double ws0, out double we0) && Text(w) is { } wt)
                    {
                        words.Add(new TranscriptWord(ws0, we0, wt));
                    }
                }
            }

            list.Add(new TranscriptSegment(start, end, text, words));
        }

        return new Transcript(language, list);

        static bool TryTimes(JsonElement e, out double start, out double end)
        {
            start = end = 0;
            return e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("start", out JsonElement a) && a.ValueKind == JsonValueKind.Number && a.TryGetDouble(out start) && double.IsFinite(start)
                && e.TryGetProperty("end", out JsonElement b) && b.ValueKind == JsonValueKind.Number && b.TryGetDouble(out end) && double.IsFinite(end);
        }

        static string? Text(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    }

    /// <summary>
    /// Lleva la transcripción a la secuencia: cada palabra (las que faltan se reparten por largo en su segmento) pasa de los
    /// segundos del búfer, que empezaba en el punto de entrada de origen del clip, a la línea de tiempo con su mapa (sin
    /// ajustar al cuadro: eso lo hace la partición en subtítulos). Lo que cae después del final del tramo se descarta; una
    /// palabra con espacios se parte en varias, y una de más de <see cref="ModelValidator.MaxWordLength"/> caracteres se acorta.
    /// </summary>
    public static IReadOnlyList<SpeechSegment> ToSequence(Clip clip, Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(transcript);
        ClipTimeMap map = clip.TimeMap;
        double span = (map.SourceOut - map.SourceIn).ToSeconds();
        List<SpeechSegment> result = [];
        foreach (TranscriptSegment segment in transcript.Segments)
        {
            IEnumerable<TranscriptWord> source = segment.Words.Count > 0 ? segment.Words : Spread(segment);
            List<SpeechWord> words = [];
            foreach (TranscriptWord word in source)
            {
                double start = Math.Max(0, word.Start);
                if (start >= span)
                {
                    continue;
                }

                double end = Math.Clamp(word.End, start, span);
                foreach ((double a, double b, string text) in Split(start, end, word.Text))
                {
                    Time x = ClipAudioMap.ToTimeline(clip, a);
                    Time y = ClipAudioMap.ToTimeline(clip, b);
                    words.Add(new SpeechWord(Time.Min(x, y), Time.Max(x, y), text));
                }
            }

            if (words.Count == 0)
            {
                continue;
            }

            words = [.. words.OrderBy(w => w.Start)];
            result.Add(new SpeechSegment(words[0].Start, words.Max(w => w.End), string.Join(' ', words.Select(w => w.Text)), words));
        }

        return [.. result.OrderBy(s => s.Start)];
    }

    /// <summary>Los subtítulos como quedarán, para la vista previa.</summary>
    public static IReadOnlyList<SubtitleCue> Cues(IReadOnlyList<SpeechSegment> segments, Rational frameRate) =>
        segments.Count == 0 ? [] : SubtitleEdits.Layout(segments, SubtitleLayout.Default, frameRate);

    /// <summary>Un paso del historial con todos los subtítulos y sus palabras (un Ctrl+Z los quita).</summary>
    public static SequenceEditCommand Command(string sequenceId, IReadOnlyList<SpeechSegment> segments, ClipGenerator template, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(template);
        List<SpeechSegment> copy = [.. segments];
        ClipGenerator style = template.Clone();
        style.Words = null;
        return new SequenceEditCommand(Description, sequenceId, ctx => Apply(ctx, copy, style), ids);
    }

    /// <summary>
    /// Pone los subtítulos con <see cref="SubtitleEdits.AddSubtitles"/> y reparte las palabras entre los clips en el mismo
    /// orden en que la partición las agrupó: cada subtítulo se queda con tantas como tiene su texto, con los tiempos pasados
    /// al origen del clip.
    /// </summary>
    internal static IReadOnlyList<string> Apply(EditContext ctx, IReadOnlyList<SpeechSegment> segments, ClipGenerator template)
    {
        IReadOnlyList<string> ids = SubtitleEdits.AddSubtitles(ctx, segments, template);
        IReadOnlyList<SubtitleCue> cues = SubtitleEdits.Layout(segments, SubtitleLayout.Default, ctx.Sequence.FrameRate);
        List<SpeechWord> words = [.. segments.OrderBy(s => s.Start).SelectMany(s => s.Words ?? [])];
        int next = 0;
        for (int i = 0; i < cues.Count && i < ids.Count; i++)
        {
            int count = cues[i].Text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
            if (next + count > words.Count || ctx.Sequence.FindClip(ids[i]) is not { Clip.Generator: { } generator } found)
            {
                break;
            }

            Clip clip = found.Clip;
            generator.Words = [.. words.Skip(next).Take(count).Select(w => new GeneratorWord
            {
                Start = w.Start - clip.Start + clip.SourceIn,
                End = w.End - clip.Start + clip.SourceIn,
                Text = w.Text,
            })];
            next += count;
        }

        ctx.EnsureValid();
        return ids;
    }

    /// <summary>«0:01,2–0:03,4», con coma decimal.</summary>
    public static string Range(Time start, Time end) => $"{Clock(start)}–{Clock(end)}";

    private static string Clock(Time t)
    {
        // En décimas enteras para que 59,96 s no salga «0:60,0».
        long tenths = (long)Math.Round(Math.Max(0, t.ToSeconds()) * 10, MidpointRounding.AwayFromZero);
        long minutes = tenths / 600;
        return string.Create(SpanishNumbers.Format, $"{minutes}:{(tenths - (minutes * 600)) / 10.0:00.0}");
    }

    /// <summary>Un segmento sin palabras: su texto repartido por largo en su tramo.</summary>
    private static IEnumerable<TranscriptWord> Spread(TranscriptSegment segment)
    {
        string[] parts = Clean(segment.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = Math.Max(1, parts.Sum(p => p.Length + 1));
        double length = Math.Max(0, segment.End - segment.Start);
        double chars = 0;
        foreach (string part in parts)
        {
            double from = segment.Start + (length * chars / total);
            chars += part.Length + 1;
            yield return new TranscriptWord(from, segment.Start + (length * chars / total), part);
        }
    }

    /// <summary>Una palabra limpia: sin caracteres de control, partida en los espacios (el tramo se reparte por largo) y acotada.</summary>
    private static IEnumerable<(double Start, double End, string Text)> Split(double start, double end, string text)
    {
        string[] parts = Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double total = Math.Max(1, parts.Sum(p => p.Length));
        double chars = 0;
        foreach (string part in parts)
        {
            double from = start + ((end - start) * chars / total);
            chars += part.Length;
            string cut = part.Length > ModelValidator.MaxWordLength ? part[..ModelValidator.MaxWordLength] : part;
            yield return (from, start + ((end - start) * chars / total), cut);
        }
    }

    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(char.IsWhiteSpace(c) || char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
