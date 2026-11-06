using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Rodaje.Core.Editing;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Serialization;

public enum SubtitleFormat
{
    Srt,
    WebVtt,
}

/// <summary>Lo leído de un archivo de subtítulos y cuántos bloques no se entendieron.</summary>
public sealed record SubtitleFileContent(IReadOnlyList<SubtitleCue> Cues, int Skipped);

/// <summary>
/// Lee y escribe SubRip (<c>.srt</c>) y WebVTT (<c>.vtt</c>). Al leer quita las etiquetas de estilo (<c>&lt;i&gt;</c>,
/// <c>{\an8}</c>, <c>&lt;c.amarillo&gt;</c>…) y conserva los saltos de línea; los tiempos van al milisegundo.
/// </summary>
public static partial class SubtitleFiles
{
    /// <summary>Tamaño máximo de un archivo de subtítulos al leerlo (un .srt de película entera pesa unos cientos de KB).</summary>
    public const long MaxFileBytes = 32L << 20;

    private const long TicksPerMillisecond = Time.TicksPerSecond / 1000;

    public static SubtitleFormat FormatOf(string path) =>
        string.Equals(Path.GetExtension(path), ".vtt", StringComparison.OrdinalIgnoreCase) ? SubtitleFormat.WebVtt : SubtitleFormat.Srt;

    /// <summary>«Proyecto - Pista.srt», sin los caracteres que Windows no admite en un nombre de archivo.</summary>
    public static string SuggestedFileName(string? project, string track, SubtitleFormat format = SubtitleFormat.Srt)
    {
        string name = string.IsNullOrWhiteSpace(project) ? track : $"{project} - {track}";
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name + (format == SubtitleFormat.WebVtt ? ".vtt" : ".srt");
    }

    /// <summary>Lee el archivo: UTF-8 o UTF-16 con BOM, UTF-8 sin BOM y, si no es UTF-8 válido, Windows Latin 1.</summary>
    public static SubtitleFileContent Read(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
        {
            throw new InvalidDataException($"«{Path.GetFileName(path)}» pesa más de {MaxFileBytes >> 20} MB, demasiado para ser un archivo de subtítulos.");
        }

        byte[] bytes = File.ReadAllBytes(path);
        return Parse(Decode(bytes));
    }

    public static void Write(string path, IEnumerable<SubtitleCue> cues, SubtitleFormat? format = null) =>
        File.WriteAllText(path, Format(cues, format ?? FormatOf(path)), new UTF8Encoding(false));

    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.AsSpan().StartsWith((byte[])[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Lee SRT o WebVTT (lo reconoce por la cabecera <c>WEBVTT</c>); los subtítulos salen en orden de inicio.</summary>
    public static SubtitleFileContent Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        bool vtt = lines.Length > 0 && lines[0].TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal);
        var cues = new List<SubtitleCue>();
        int skipped = 0;
        int i = vtt ? SkipBlock(lines, 0) : 0;
        while (i < lines.Length)
        {
            if (lines[i].Trim().Length == 0)
            {
                i++;
                continue;
            }

            int end = SkipBlock(lines, i);
            if (vtt && IsVttMetaBlock(lines[i]))
            {
                i = end;
                continue;
            }

            int timing = lines[i].Contains("-->", StringComparison.Ordinal) ? i : i + 1;
            if (timing < end && TryParseTiming(lines[timing], out Time start, out Time stop) && stop > start)
            {
                string body = string.Join('\n', lines[(timing + 1)..end].Select(l => CleanLine(l, vtt)).Where(l => l.Length > 0));
                if (body.Length > 0)
                {
                    cues.Add(new SubtitleCue(start, stop, body));
                }
                else
                {
                    skipped++;
                }
            }
            else
            {
                skipped++;
            }

            i = end;
        }

        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new SubtitleFileContent(cues, skipped);
    }

    /// <summary>
    /// Escribe los subtítulos. Los tiempos se redondean al milisegundo (al menos uno de duración); una línea vacía en el
    /// texto se quita, porque en los dos formatos termina el subtítulo, y en WebVTT se escapan <c>&amp;</c>, <c>&lt;</c>
    /// y <c>&gt;</c>.
    /// </summary>
    public static string Format(IEnumerable<SubtitleCue> cues, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var text = new StringBuilder();
        if (format == SubtitleFormat.WebVtt)
        {
            text.Append("WEBVTT\n\n");
        }

        int index = 1;
        foreach (SubtitleCue cue in cues.OrderBy(c => c.Start))
        {
            string[] body = cue.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
                .Select(l => l.Trim().Replace("-->", "->", StringComparison.Ordinal))
                .Where(l => l.Length > 0)
                .Select(l => format == SubtitleFormat.WebVtt ? EscapeVtt(l) : l)
                .ToArray();
            if (body.Length == 0)
            {
                continue;
            }

            long start = Milliseconds(cue.Start);
            long end = Math.Max(Milliseconds(cue.End), start + 1);
            char separator = format == SubtitleFormat.WebVtt ? '.' : ',';
            if (format == SubtitleFormat.Srt)
            {
                text.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            text.Append(Stamp(start, separator)).Append(" --> ").Append(Stamp(end, separator)).Append('\n');
            foreach (string line in body)
            {
                text.Append(line).Append('\n');
            }

            text.Append('\n');
            index++;
        }

        return format == SubtitleFormat.Srt ? text.ToString().Replace("\n", "\r\n", StringComparison.Ordinal) : text.ToString();
    }

    /// <summary>Lee «hh:mm:ss,mmm --> hh:mm:ss,mmm» (también con punto, sin horas o con ajustes de WebVTT detrás).</summary>
    public static bool TryParseTiming(string line, out Time start, out Time end)
    {
        start = end = Time.Zero;
        int arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return false;
        }

        string right = line[(arrow + 3)..].Trim();
        int space = right.IndexOfAny([' ', '\t']);
        return TryParseStamp(line[..arrow].Trim(), out start) && TryParseStamp(space < 0 ? right : right[..space], out end);
    }

    public static bool TryParseStamp(string stamp, out Time time)
    {
        time = Time.Zero;
        Match match = StampPattern().Match(stamp);
        if (!match.Success)
        {
            return false;
        }

        long hours = match.Groups[1].Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        long minutes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        long seconds = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        string fraction = match.Groups[4].Value;
        long millis = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        if (minutes > 59 || seconds > 59 || hours > 9999)
        {
            return false;
        }

        time = Time.FromTicks((((hours * 60 + minutes) * 60 + seconds) * 1000 + millis) * TicksPerMillisecond);
        return true;
    }

    private static long Milliseconds(Time time) =>
        (long)Math.Round(Math.Max(0, time.Ticks) / (double)TicksPerMillisecond, MidpointRounding.AwayFromZero);

    private static string Stamp(long milliseconds, char separator)
    {
        long hours = milliseconds / 3_600_000;
        long minutes = milliseconds / 60_000 % 60;
        long seconds = milliseconds / 1000 % 60;
        long millis = milliseconds % 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{minutes:00}:{seconds:00}{separator}{millis:000}");
    }

    private static int SkipBlock(string[] lines, int from)
    {
        int i = from;
        while (i < lines.Length && lines[i].Trim().Length > 0)
        {
            i++;
        }

        return i;
    }

    private static bool IsVttMetaBlock(string first) =>
        !first.Contains("-->", StringComparison.Ordinal)
        && (first.StartsWith("NOTE", StringComparison.Ordinal) || first.StartsWith("STYLE", StringComparison.Ordinal)
            || first.StartsWith("REGION", StringComparison.Ordinal));

    private static string CleanLine(string line, bool vtt)
    {
        string text = TagPattern().Replace(line, string.Empty);
        text = AssOverridePattern().Replace(text, string.Empty);
        if (vtt || text.Contains('&', StringComparison.Ordinal))
        {
            text = WebUtility.HtmlDecode(text);
        }

        return text.Replace(' ', ' ').Trim();
    }

    private static string EscapeVtt(string line) =>
        line.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    // Cifras ASCII y no \d: en .NET \d también casa con cifras de otras escrituras («٠١», «０１») y long.Parse las rechaza.
    [GeneratedRegex(@"^(?:([0-9]{1,4}):)?([0-9]{1,2}):([0-9]{1,2})(?:[,.]([0-9]{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"</?[A-Za-z][^<>]*>|<[0-9][0-9:.,]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\{\\[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex AssOverridePattern();
}
