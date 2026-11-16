using System.Globalization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Edl;

/// <summary>
/// Lee EDL CMX 3600. Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe; un archivo roto o
/// que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/>.
/// </summary>
public static class EdlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);
        EdlList list = Parse(text, options, report, out bool dropFrame);
        Rational rate = options.FrameRate ?? (dropFrame ? Rational.Ntsc30 : Rational.Fps25);
        if (!Model.Sequence.IsSupportedFrameRate(rate))
        {
            throw new InterchangeException($"La tasa {rate} no vale para leer la EDL.");
        }

        return list.ToDraft(rate, baseDirectory, options.Limits, report);
    }

    internal static EdlList Parse(string text, InterchangeOptions options, InterchangeReport report, out bool dropFrame)
    {
        var list = new EdlList();
        dropFrame = false;
        Rational? rate = options.FrameRate;
        EdlEvent? last = null;
        (string Reel, long Source)? outgoing = null;
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            if (raw.Length > options.Limits.MaxTextLength)
            {
                throw new InterchangeException($"La línea {lineNumber} de la EDL pasa de {options.Limits.MaxTextLength} caracteres.");
            }

            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                list.Title = EdlList.Clean(line[6..]);
                continue;
            }

            if (line.StartsWith("FCM:", StringComparison.OrdinalIgnoreCase))
            {
                list.DropFrame = dropFrame = line.Contains("NON", StringComparison.OrdinalIgnoreCase) is false && line.Contains("DROP", StringComparison.OrdinalIgnoreCase);
                rate ??= dropFrame ? Rational.Ntsc30 : Rational.Fps25;
                continue;
            }

            rate ??= Rational.Fps25;
            if (line.StartsWith('*'))
            {
                Comment(line.TrimStart('*').Trim(), last, rate.Value, report);
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "M2" && parts.Length >= 3)
            {
                if (last is not null && parts[1] == last.Reel && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && double.IsFinite(fps) && Math.Abs(fps) < 100_000)
                {
                    last.SpeedTenths = (int)Math.Round(fps * 10);
                }
                else
                {
                    report.Add(IssueSeverity.Lost, IssueKind.Speed, $"Línea {lineNumber}: M2 que no se entiende.", line);
                }

                continue;
            }

            if (parts[0].Length is >= 3 and <= 6 && parts[0].All(char.IsAsciiDigit))
            {
                EdlEvent? edlEvent = Event(parts, rate.Value, list.DropFrame, lineNumber, report);
                if (edlEvent is null)
                {
                    continue;
                }

                if (list.Events.Count >= options.Limits.MaxObjects)
                {
                    throw new InterchangeException($"La EDL pasa de {options.Limits.MaxObjects} eventos.");
                }

                // La línea de duración cero de una transición dice de dónde sale; el evento siguiente con el mismo número entra.
                if (edlEvent.Transition == EdlTransition.Cut && edlEvent.RecordIn == edlEvent.RecordOut)
                {
                    outgoing = (edlEvent.Reel, edlEvent.SourceIn);
                    last = edlEvent;
                    continue;
                }

                if (edlEvent.Transition != EdlTransition.Cut && outgoing is { } from)
                {
                    edlEvent.OutgoingReel = from.Reel;
                    edlEvent.OutgoingSource = from.Source;
                }

                outgoing = null;
                list.Events.Add(edlEvent);
                last = edlEvent;
                continue;
            }

            report.Add(IssueSeverity.Info, IssueKind.Unknown, $"Línea {lineNumber} de la EDL que no se entiende: se salta.", line.Length > 60 ? line[..60] : line);
        }

        return list;
    }

    private static EdlEvent? Event(string[] parts, Rational rate, bool dropFrame, int lineNumber, InterchangeReport report)
    {
        if (parts.Length < 8)
        {
            report.Add(IssueSeverity.Lost, IssueKind.Unknown, $"Línea {lineNumber}: evento incompleto.", string.Join(' ', parts));
            return null;
        }

        string type = parts[3].ToUpperInvariant();
        int next = 4;
        long frames = 0;
        EdlTransition transition = EdlTransition.Cut;
        if (type != "C")
        {
            transition = type == "D" ? EdlTransition.Dissolve : EdlTransition.Wipe;
            if (parts.Length < 9 || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out frames))
            {
                report.Add(IssueSeverity.Lost, IssueKind.Transition, $"Línea {lineNumber}: transición sin duración.", string.Join(' ', parts));
                return null;
            }

            next = 5;
            if (type != "D" && !type.StartsWith('W'))
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Transition, $"Línea {lineNumber}: transición «{type}» desconocida: va como fundido.", parts[1]);
            }
        }

        if (parts.Length < next + 4)
        {
            report.Add(IssueSeverity.Lost, IssueKind.Unknown, $"Línea {lineNumber}: faltan códigos de tiempo.", string.Join(' ', parts));
            return null;
        }

        long[] tc = new long[4];
        for (int i = 0; i < 4; i++)
        {
            string value = parts[next + i];
            if (!SmpteTimecode.TryParse(value, rate, out SmpteTimecode parsed)
                && !(dropFrame && SmpteTimecode.TryParse(value.Replace(';', ':'), rate, out parsed)))
            {
                report.Add(IssueSeverity.Lost, IssueKind.Rounding, $"Línea {lineNumber}: código de tiempo «{value}» que no vale a {rate}.", parts[1]);
                return null;
            }

            tc[i] = parsed.Frame;
        }

        if (tc[1] < tc[0] || tc[3] < tc[2])
        {
            report.Add(IssueSeverity.Lost, IssueKind.Unknown, $"Línea {lineNumber}: la salida va antes de la entrada.", parts[1]);
            return null;
        }

        return new EdlEvent
        {
            Number = int.Parse(parts[0], CultureInfo.InvariantCulture),
            Reel = parts[1],
            Channel = parts[2].ToUpperInvariant(),
            Transition = transition,
            TransitionFrames = frames,
            SourceIn = tc[0],
            SourceOut = tc[1],
            RecordIn = tc[2],
            RecordOut = tc[3],
        };
    }

    private static void Comment(string comment, EdlEvent? last, Rational rate, InterchangeReport report)
    {
        if (last is null)
        {
            return;
        }

        if (Value(comment, "FROM CLIP NAME:") is { } name)
        {
            last.ClipName = name;
        }
        else if (Value(comment, "SOURCE FILE:") is { } file)
        {
            last.SourceFile = file;
        }
        else if (Value(comment, "LOC:") is { } loc)
        {
            string[] parts = loc.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && (SmpteTimecode.TryParse(parts[0], rate, out SmpteTimecode at) || SmpteTimecode.TryParse(parts[0].Replace(';', ':'), rate, out at)))
            {
                last.Markers.Add((at.Frame, parts.Length == 3 ? parts[2].Trim() : ""));
            }
            else
            {
                report.Add(IssueSeverity.Lost, IssueKind.Marker, "Un marcador con un TC que no se entiende.", loc);
            }
        }
        else if (Value(comment, "TITLE:") is { } title)
        {
            report.Add(IssueSeverity.Lost, IssueKind.Title, "La EDL nombra un título que no lleva.", title);
        }
    }

    private static string? Value(string comment, string key) =>
        comment.StartsWith(key, StringComparison.OrdinalIgnoreCase) ? EdlList.Clean(comment[key.Length..]) : null;
}
