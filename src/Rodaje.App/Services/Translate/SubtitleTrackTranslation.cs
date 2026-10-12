using System.Text;
using System.Text.Json;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Translate;

/// <summary>Un idioma de la lista de «Traducir pista»: el código que entiende <c>modulo-traducir</c> y su nombre.</summary>
public sealed record TranslationLanguage(string Code, string Name);

/// <summary>Un clip de texto de la pista que se traduce.</summary>
public sealed record SubtitleText(string ClipId, string Text);

/// <summary>El módulo devolvió algo que no se puede usar como traducción.</summary>
public sealed partial class TranslationException : Exception
{
    public TranslationException() { }

    public TranslationException(string message) : base(message) { }

    public TranslationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// «Traducir pista» sin WinUI: qué pistas se pueden traducir, los parámetros de <c>traducir</c> por tandas, la lectura
/// desconfiada del resultado (mismos ids, nada más) y el comando que crea la pista traducida en un solo paso del
/// historial, con los clips copiados (mismos tiempos, estilo y efectos) y el texto nuevo.
/// </summary>
public static class SubtitleTrackTranslation
{
    public const string ModuleId = "modulo-traducir";
    public const string Function = "traducir";

    /// <summary>Subtítulos por llamada: con textos de hasta 500 caracteres el mensaje queda lejos de 1 MiB.</summary>
    public const int MaxPerCall = 400;

    /// <summary>Lo que acepta el módulo por subtítulo; un texto más largo no se manda.</summary>
    public const int MaxTextLength = 500;

    public const int MaxLineChars = 42;

    /// <summary>Los mismos que ofrece el módulo (<c>modules/Rodaje.Module.Translate/Translation/Languages.cs</c>).</summary>
    public static IReadOnlyList<TranslationLanguage> Languages { get; } =
    [
        new("es", "Español"),
        new("en", "Inglés"),
        new("pt-BR", "Portugués (Brasil)"),
        new("pt-PT", "Portugués (Portugal)"),
        new("fr", "Francés"),
        new("it", "Italiano"),
        new("de", "Alemán"),
        new("nl", "Neerlandés"),
        new("ca", "Catalán"),
        new("gl", "Gallego"),
        new("eu", "Euskera"),
        new("pl", "Polaco"),
        new("ru", "Ruso"),
        new("uk", "Ucraniano"),
        new("tr", "Turco"),
        new("ar", "Árabe"),
        new("hi", "Hindi"),
        new("ja", "Japonés"),
        new("ko", "Coreano"),
        new("zh-Hans", "Chino simplificado"),
    ];

    /// <summary>Las pistas de video con algún clip de texto: primero las de subtítulos, de arriba abajo.</summary>
    public static IReadOnlyList<Track> TranslatableTracks(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return [.. sequence.VideoTracks
            .Select((t, i) => (Track: t, Index: i))
            .Where(x => x.Track.Clips.Any(IsText))
            .OrderByDescending(x => x.Track.Role == TrackRoles.Subtitles)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Track)];
    }

    /// <summary>La pista del primer clip de texto seleccionado; si no hay, la primera de <see cref="TranslatableTracks"/>.</summary>
    public static Track? DefaultTrack(Sequence sequence, IReadOnlyList<string> selection)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selection);
        foreach (string id in selection)
        {
            if (sequence.FindClip(id) is { } found && IsText(found.Clip) && sequence.VideoTracks.Contains(found.Track))
            {
                return found.Track;
            }
        }

        IReadOnlyList<Track> tracks = TranslatableTracks(sequence);
        return tracks.Count > 0 ? tracks[0] : null;
    }

    /// <summary>Los clips de texto de la pista, en orden; los textos largos se recortan a lo que acepta el módulo.</summary>
    public static IReadOnlyList<SubtitleText> Collect(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return [.. track.Clips.Where(IsText).OrderBy(c => c.Start)
            .Select(c => new SubtitleText(c.Id, c.Generator!.Text.Length > MaxTextLength ? c.Generator.Text[..MaxTextLength] : c.Generator.Text))];
    }

    /// <summary>Los parámetros de cada llamada a <c>traducir</c>, de hasta <see cref="MaxPerCall"/> subtítulos.</summary>
    public static IReadOnlyList<JsonElement> Requests(IReadOnlyList<SubtitleText> texts, string? source, string target)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrEmpty(target);
        List<JsonElement> calls = [];
        for (int from = 0; from < texts.Count; from += MaxPerCall)
        {
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("source", string.IsNullOrEmpty(source) ? "auto" : source);
                w.WriteString("target", target);
                w.WriteNumber("maxLineChars", MaxLineChars);
                w.WriteStartArray("segments");
                foreach (SubtitleText t in texts.Skip(from).Take(MaxPerCall))
                {
                    w.WriteStartObject();
                    w.WriteString("id", t.ClipId);
                    w.WriteString("text", t.Text);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
            calls.Add(doc.RootElement.Clone());
        }

        return calls;
    }

    /// <summary>
    /// Lee el resultado de una llamada: tiene que traer exactamente los ids pedidos, cada uno una vez, con texto. El
    /// texto se limpia de caracteres de control (salvo los saltos de línea) para que el título sea válido.
    /// </summary>
    /// <param name="untranslated">Los ids que el módulo no pudo traducir (quedan con el texto original).</param>
    public static Dictionary<string, string> ParseResult(JsonElement? value, IReadOnlyCollection<string> expectedIds, out IReadOnlyList<string> untranslated)
    {
        ArgumentNullException.ThrowIfNull(expectedIds);
        if (value is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("segments", out JsonElement segments) || segments.ValueKind != JsonValueKind.Array)
        {
            throw new TranslationException("El módulo de traducción no devolvió los subtítulos.");
        }

        var expected = new HashSet<string>(expectedIds, StringComparer.Ordinal);
        Dictionary<string, string> texts = new(StringComparer.Ordinal);
        foreach (JsonElement e in segments.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String
                || !e.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String)
            {
                throw new TranslationException("El módulo de traducción devolvió un subtítulo sin id o sin texto.");
            }

            string id = idElement.GetString()!;
            if (!expected.Contains(id) || !texts.TryAdd(id, Clean(textElement.GetString()!)))
            {
                throw new TranslationException("El módulo de traducción devolvió subtítulos que no se le pidieron.");
            }
        }

        if (texts.Count != expected.Count)
        {
            throw new TranslationException($"Faltan {expected.Count - texts.Count} subtítulos en la traducción.");
        }

        List<string> missing = [];
        if (root.TryGetProperty("untranslated", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            missing.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(expected.Contains).Distinct());
        }

        untranslated = missing;
        return texts;
    }

    /// <summary>«Subtítulos (Inglés)», o con 2, 3… si ya existe.</summary>
    public static string TrackName(Sequence sequence, Track source, TranslationLanguage target)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        string baseName = $"{(source.Name.Length > 0 ? source.Name : "Subtítulos")} ({target.Name})";
        var used = new HashSet<string>(sequence.AllTracks.Select(t => t.Name), StringComparer.CurrentCultureIgnoreCase);
        string name = baseName;
        for (int n = 2; used.Contains(name); n++)
        {
            name = FormattableString.Invariant($"{baseName} {n}");
        }

        return name;
    }

    /// <summary>
    /// Crea una pista de subtítulos justo encima de <paramref name="sourceTrackId"/> con una copia de cada clip de
    /// texto (mismos tiempos, estilo, caja y efectos; sin enlace) y su texto traducido. La pista original no cambia.
    /// </summary>
    /// <returns>El comando; en <see cref="SequenceEditCommand.CreatedIds"/>, los clips nuevos y al final la pista.</returns>
    public static SequenceEditCommand Command(string sequenceId, string sourceTrackId, IReadOnlyDictionary<string, string> texts, string trackName, IIdGenerator? ids = null)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);
        ArgumentNullException.ThrowIfNull(sourceTrackId);
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrEmpty(trackName);
        return new SequenceEditCommand("Traducir subtítulos", sequenceId, ctx =>
        {
            Track source = ctx.Sequence.FindTrack(sourceTrackId) ?? throw new EditException("La pista que se traducía ya no está.");
            int index = ctx.Sequence.VideoTracks.IndexOf(source);
            if (index < 0)
            {
                throw new EditException("Solo se traducen pistas de video con texto.");
            }

            List<Clip> copies = [];
            foreach (Clip clip in source.Clips.Where(c => IsText(c) && texts.ContainsKey(c.Id)).OrderBy(c => c.Start))
            {
                Clip copy = clip.Clone();
                copy.Id = ctx.Ids.NewId("clip");
                copy.LinkId = null;
                copy.Generator!.Text = texts[clip.Id];
                copies.Add(copy);
            }

            if (copies.Count == 0)
            {
                throw new EditException("La pista no tiene subtítulos que traducir.");
            }

            string trackId = TimelineEdits.AddTrack(ctx, TrackKind.Video, index + 1, trackName);
            Track track = ctx.Sequence.GetTrack(trackId);
            track.Role = TrackRoles.Subtitles;
            track.Clips.AddRange(copies);
            track.SortClips();
            ctx.EnsureValid();
            return [.. copies.Select(c => c.Id), trackId];
        }, ids);
    }

    private static bool IsText(Clip clip) => clip.Generator is { Kind: GeneratorKinds.Text };

    /// <summary>Sin caracteres de control salvo <c>\n</c>, sin <c>\r</c> y sin espacios al final de cada línea.</summary>
    private static string Clean(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            sb.Append(c == '\n' ? '\n' : char.IsControl(c) ? ' ' : c);
        }

        return string.Join('\n', sb.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}
