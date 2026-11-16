using System.Globalization;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Edl;

/// <summary>Escribe EDL CMX 3600 con lo que declara <see cref="Fidelity.Project"/> para este formato: una por pista de video.</summary>
public static class EdlWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        Project resolved = project.Clone();
        // Sin carpeta del proyecto las rutas ya son las de la app (absolutas) y se escriben tal cual.
        foreach (MediaRef media in resolved.Media.Where(m => m.Path.Length > 0 && projectDirectory is not null))
        {
            media.Path = media.ResolvePath(projectDirectory!);
        }

        Sequence copy = resolved.FindSequence(sequence.Id) ?? sequence;
        IReadOnlyList<int> tracks = options.VideoTracks is { Count: > 0 } chosen ? chosen : [.. Enumerable.Range(0, Math.Max(1, sequence.VideoTracks.Count))];
        var outputs = new List<InterchangeOutput>();
        foreach (int index in tracks)
        {
            var single = new InterchangeOptions
            {
                FrameRate = options.FrameRate,
                DefaultStartTimecode = options.DefaultStartTimecode,
                VideoTracks = [index],
                IncludeAudio = options.IncludeAudio && index == tracks[0],
                LongReelNames = options.LongReelNames,
                Limits = options.Limits,
            };
            EdlList list = EdlList.FromSequence(resolved, copy, single, new InterchangeReport());
            outputs.Add(new InterchangeOutput(outputs.Count == 0 ? "" : $"_V{index + 1}", Format(list, copy.FrameRate)));
        }

        return outputs;
    }

    internal static string Format(EdlList list, Rational rate)
    {
        var text = new StringBuilder();
        text.Append("TITLE: ").Append(list.Title.Length > 0 ? list.Title : "Rodaje").Append("\r\n");
        text.Append(list.DropFrame ? "FCM: DROP FRAME" : "FCM: NON-DROP FRAME").Append("\r\n\r\n");
        int digits = list.Events.Count > 999 ? 4 : 3;
        string Tc(long frame) => SmpteTimecode.FromFrame(frame, rate, list.DropFrame).Format();
        string Number(int n) => n.ToString(new string('0', digits), CultureInfo.InvariantCulture);
        foreach (EdlEvent e in list.Events)
        {
            string reel = e.Reel.PadRight(8);
            string channel = e.Channel.PadRight(5);
            if (e.Transition != EdlTransition.Cut)
            {
                string outgoing = (e.OutgoingReel ?? e.Reel).PadRight(8);
                text.Append(CultureInfo.InvariantCulture, $"{Number(e.Number)}  {outgoing} {channel} C        {Tc(e.OutgoingSource)} {Tc(e.OutgoingSource)} {Tc(e.RecordIn)} {Tc(e.RecordIn)}\r\n");
            }

            string kind = e.Transition switch
            {
                EdlTransition.Dissolve => "D    " + e.TransitionFrames.ToString("000", CultureInfo.InvariantCulture),
                EdlTransition.Wipe => "W001 " + e.TransitionFrames.ToString("000", CultureInfo.InvariantCulture),
                _ => "C       ",
            };
            text.Append(CultureInfo.InvariantCulture, $"{Number(e.Number)}  {reel} {channel} {kind} {Tc(e.SourceIn)} {Tc(e.SourceOut)} {Tc(e.RecordIn)} {Tc(e.RecordOut)}\r\n");
            if (e.SpeedTenths is { } tenths)
            {
                string fps = (tenths / 10.0).ToString("000.0;-00.0", CultureInfo.InvariantCulture);
                text.Append(CultureInfo.InvariantCulture, $"M2   {reel} {fps,6}                {Tc(e.SourceIn)}\r\n");
            }

            if (e.ClipName is { } name)
            {
                text.Append("* FROM CLIP NAME: ").Append(name).Append("\r\n");
            }

            if (e.SourceFile is { } file)
            {
                text.Append("* SOURCE FILE: ").Append(file).Append("\r\n");
            }

            foreach ((long frame, string marker) in e.Markers)
            {
                text.Append(CultureInfo.InvariantCulture, $"* LOC: {Tc(frame)} RED     {marker}\r\n");
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }
}
