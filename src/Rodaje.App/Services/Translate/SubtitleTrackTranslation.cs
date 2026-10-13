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

    /// <summary>Lo que acepta el módulo por subtítulo; un texto más largo se manda partido (<see cref="Collect"/>).</summary>
    public const int MaxTextLength = 500;

    /// <summary>
    /// Separa el id del clip del número de parte en los ids de las partes de un texto largo: la primera parte lleva el
    /// id del clip y las siguientes <c>clip#2</c>, <c>clip#3</c>…
    /// </summary>
    public const char PartSeparator = '#';

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

    /// <summary>
    /// Los clips de texto de la pista, en orden. Un texto de más de <see cref="MaxTextLength"/> caracteres no se recorta
    /// (la traducción reemplaza el texto entero y se perdería el resto): se parte en trozos con ids de parte
    /// (<see cref="PartId"/>) que <see cref="Command"/> vuelve a juntar.
    /// </summary>
    public static IReadOnlyList<SubtitleText> Collect(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        List<SubtitleText> texts = [];
        foreach (Clip clip in track.Clips.Where(IsText).OrderBy(c => c.Start))
        {
            IReadOnlyList<string> parts = Split(clip.Generator!.Text, MaxTextLength);
            texts.Add(new SubtitleText(clip.Id, parts[0]));
            for (int k = 1; k < parts.Count; k++)
            {
                texts.Add(new SubtitleText(PartId(clip.Id, k + 1), parts[k]));
            }
        }

        return texts;
    }

    /// <summary>El id de la parte <paramref name="part"/> (desde 1) del texto de <paramref name="clipId"/>; la 1 es el id del clip.</summary>
    public static string PartId(string clipId, int part) =>
        part <= 1 ? clipId : FormattableString.Invariant($"{clipId}{PartSeparator}{part}");

    /// <summary>
    /// Parte <paramref name="text"/> en trozos de hasta <paramref name="maxLength"/> caracteres: tras un fin de frase o un
    /// salto de línea si hay uno en la segunda mitad del trozo, si no en un espacio, y si no a la fuerza; nunca entre
    /// las dos mitades de un par sustituto (un emoji partido no se puede escribir en JSON). Siempre da al menos un trozo.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        List<string> parts = [];
        string rest = text;
        while (rest.Length > maxLength)
        {
            int cut = BreakAt(rest, maxLength);
            string part = rest[..cut].Trim();
            if (part.Length > 0 || parts.Count == 0)
            {
                parts.Add(part);
            }

            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0 || parts.Count == 0)
        {
            parts.Add(rest);
        }

        return parts;
    }

    private static int BreakAt(string text, int maxLength)
    {
        int sentence = -1;
        int space = -1;
        for (int i = maxLength; i > maxLength / 2; i--)
        {
            char before = text[i - 1];
            bool spaceAfter = char.IsWhiteSpace(text[i]);
            if (before == '\n' || before is '。' or '！' or '？' || (spaceAfter && before is '.' or '!' or '?' or '…' or '؟' or '।'))
            {
                sentence = i;
                break;
            }

            if (space < 0 && (spaceAfter || char.IsWhiteSpace(before)))
            {
                space = i;
            }
        }

        int cut = sentence > 0 ? sentence : space > 0 ? space : maxLength;
        return char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]) ? cut - 1 : cut;
    }

    /// <summary>Los parámetros de cada llamada a <c>traducir</c>, de hasta <see cref="MaxPerCall"/> subtítulos.</summary>
    public static IReadOnlyList<JsonElement> Requests(IReadOnlyList<SubtitleText> texts, string? source, string target, Action<Utf8JsonWriter>? extra = null)
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
                // Los campos del perfil de IA (dispositivo, capas en la GPU, hilos), si los hay.
                extra?.Invoke(w);

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
            // Una parte sin traducir de un texto largo cuenta como su clip.
            missing.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(expected.Contains)
                .Select(id => ClipIdOf(id, expected)).Distinct());
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
        var used = new HashSet<string>(sequence.AllTracks.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        string name = baseName;
        for (int n = 2; used.Contains(name); n++)
        {
            name = FormattableString.Invariant($"{baseName} {n}");
        }

        return name;
    }

    /// <summary>
    /// Crea una pista de subtítulos justo encima de <paramref name="sourceTrackId"/> con una copia de cada clip de
    /// texto (mismos tiempos, estilo, caja y efectos; sin enlace) y su texto traducido, con las partes de un texto
    /// largo (<see cref="PartId"/>) juntas en orden. La pista original no cambia.
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
                copy.Generator!.Text = Joined(texts, clip.Id);
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

    /// <summary>El clip de un id de parte (<c>clip#2</c> → <c>clip</c>) si ese clip también se pidió; si no, el mismo id.</summary>
    private static string ClipIdOf(string id, HashSet<string> expected)
    {
        int at = id.LastIndexOf(PartSeparator);
        return at > 0 && at < id.Length - 1 && !id.AsSpan(at + 1).ContainsAnyExceptInRange('0', '9') && expected.Contains(id[..at])
            ? id[..at]
            : id;
    }

    /// <summary>
    /// La traducción del clip con sus partes en orden. Si junta más líneas o caracteres de los que admite un texto
    /// (<see cref="ModelValidator.MaxTextLines"/>, <see cref="ModelValidator.MaxTextLength"/>), las líneas se unen con
    /// espacios y, como último recurso, se recorta sin partir un par sustituto, para que la pista nueva sea válida.
    /// </summary>
    private static string Joined(IReadOnlyDictionary<string, string> texts, string clipId)
    {
        List<string> parts = [texts[clipId]];
        for (int k = 2; texts.TryGetValue(PartId(clipId, k), out string? part); k++)
        {
            parts.Add(part);
        }

        string text = string.Join('\n', parts.Where(p => p.Length > 0));
        if (text.Count(c => c == '\n') >= ModelValidator.MaxTextLines)
        {
            text = text.Replace('\n', ' ');
        }

        if (text.Length > ModelValidator.MaxTextLength)
        {
            int cut = ModelValidator.MaxTextLength;
            text = text[..(char.IsHighSurrogate(text[cut - 1]) ? cut - 1 : cut)];
        }

        return text;
    }

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
