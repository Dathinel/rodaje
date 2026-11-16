using System.Globalization;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Escribe FCPXML 1.10 con lo que declara <see cref="Fidelity.Project"/> para este formato (D-062).</summary>
public static class FcpxmlWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);

        var writer = new XmlLiteWriter("fcpxml");
        writer.Start("fcpxml").Attr("version", "1.10");

        List<Sequence> compounds = DraftCopy.CompoundsOf(project, sequence, options.Limits);
        var allSequences = compounds.Append(sequence).ToList();

        // 1. Gather formats
        var formatMap = new Dictionary<(Rational Rate, int Width, int Height), string>();
        int resourceCounter = 1;

        string GetFormatId(Rational rate, int width, int height)
        {
            var key = (rate, width, height);
            if (!formatMap.TryGetValue(key, out string? id))
            {
                id = $"r{resourceCounter++}";
                formatMap[key] = id;
            }

            return id;
        }

        // Main sequence format
        string mainFormatId = GetFormatId(sequence.FrameRate, sequence.Width, sequence.Height);

        // Compound sequences formats
        foreach (Sequence comp in compounds)
        {
            GetFormatId(comp.FrameRate, comp.Width, comp.Height);
        }

        // Media formats
        foreach (MediaRef media in project.Media)
        {
            if (media.HasVideo && media.FrameRate.HasValue)
            {
                GetFormatId(media.FrameRate.Value, media.Width, media.Height);
            }
        }

        // Asset IDs
        var assetMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MediaRef media in project.Media)
        {
            assetMap[media.Id] = $"r{resourceCounter++}";
        }

        // Compound IDs
        var compoundMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Sequence comp in compounds)
        {
            compoundMap[comp.Id] = $"r{resourceCounter++}";
        }

        // Check if any transition exists on spine of any sequence
        bool hasTransitions = allSequences.Any(s => s.VideoTracks.Count > 0 && s.VideoTracks[0].Transitions.Count > 0);

        // 2. Write resources
        writer.Start("resources");

        foreach (var (key, id) in formatMap)
        {
            writer.Start("format")
                .Attr("id", id)
                .Attr("frameDuration", FcpxmlTime.FormatFrameDuration(key.Rate))
                .Attr("width", key.Width.ToString(CultureInfo.InvariantCulture))
                .Attr("height", key.Height.ToString(CultureInfo.InvariantCulture))
                .End();
        }

        if (hasTransitions)
        {
            writer.Start("effect")
                .Attr("id", "r-fade")
                .Attr("name", "Cross Dissolve")
                .Attr("uid", ".../Transitions.localized/Dissolves.localized/Cross Dissolve.localized/Cross Dissolve.motr")
                .End();
        }

        foreach (MediaRef media in project.Media)
        {
            string assetId = assetMap[media.Id];
            string? mediaFormatId = media.HasVideo && media.FrameRate.HasValue
                ? formatMap.GetValueOrDefault((media.FrameRate.Value, media.Width, media.Height))
                : null;

            Time startTime = Time.Zero;
            if (media.Timecode is not null && SmpteTimecode.TryParse(media.Timecode, media.FrameRate ?? sequence.FrameRate, out SmpteTimecode tc))
            {
                startTime = tc.ToTime(media.FrameRate ?? sequence.FrameRate);
            }

            writer.Start("asset")
                .Attr("id", assetId)
                .Attr("name", media.Name)
                .Attr("start", FcpxmlTime.FormatTime(startTime))
                .Attr("duration", FcpxmlTime.FormatTime(media.Duration));

            if (mediaFormatId is not null)
            {
                writer.Attr("format", mediaFormatId);
            }

            if (media.HasVideo)
            {
                writer.Attr("hasVideo", "1");
            }

            if (media.HasAudio)
            {
                writer.Attr("hasAudio", "1");
                writer.Attr("audioSources", "1");
                writer.Attr("audioChannels", (media.AudioChannels > 0 ? media.AudioChannels : 2).ToString(CultureInfo.InvariantCulture));
                writer.Attr("audioRate", (media.SampleRate > 0 ? media.SampleRate : 48000).ToString(CultureInfo.InvariantCulture));
            }

            writer.Start("media-rep")
                .Attr("kind", "original-media")
                .Attr("src", ToUri(media.Path))
                .End();

            writer.End(); // asset
        }

        foreach (Sequence comp in compounds)
        {
            string compId = compoundMap[comp.Id];
            string compFormatId = formatMap[(comp.FrameRate, comp.Width, comp.Height)];

            writer.Start("media")
                .Attr("id", compId)
                .Attr("name", comp.Name);

            writer.Start("sequence")
                .Attr("format", compFormatId)
                .Attr("duration", FcpxmlTime.FormatTime(comp.Duration))
                .Attr("tcStart", "0s")
                .Attr("tcFormat", "NDF");

            writer.Start("spine");
            WriteSpine(writer, comp, Time.Zero, project, formatMap, assetMap, compoundMap, hasTransitions);
            writer.End(); // spine

            writer.End(); // sequence
            writer.End(); // media
        }

        writer.End(); // resources

        // 3. Library > Event > Project > Sequence
        writer.Start("library");
        writer.Start("event").Attr("name", "Rodaje");
        writer.Start("project").Attr("name", sequence.Name);

        Time mainTcStart = Time.Zero;
        string mainTcFormat = "NDF";
        if (sequence.StartTimecode is not null && SmpteTimecode.TryParse(sequence.StartTimecode, sequence.FrameRate, out SmpteTimecode mainTc))
        {
            mainTcStart = mainTc.ToTime(sequence.FrameRate);
            mainTcFormat = mainTc.DropFrame ? "DF" : "NDF";
        }

        writer.Start("sequence")
            .Attr("format", mainFormatId)
            .Attr("duration", FcpxmlTime.FormatTime(sequence.Duration))
            .Attr("tcStart", FcpxmlTime.FormatTime(mainTcStart))
            .Attr("tcFormat", mainTcFormat);

        writer.Start("spine");
        WriteSpine(writer, sequence, mainTcStart, project, formatMap, assetMap, compoundMap, hasTransitions);
        writer.End(); // spine

        writer.End(); // sequence
        writer.End(); // project
        writer.End(); // event
        writer.End(); // library
        writer.End(); // fcpxml

        return [new InterchangeOutput("", writer.ToString())];
    }

    private static void WriteSpine(
        XmlLiteWriter writer,
        Sequence sequence,
        Time tcStart,
        Project project,
        Dictionary<(Rational Rate, int Width, int Height), string> formatMap,
        Dictionary<string, string> assetMap,
        Dictionary<string, string> compoundMap,
        bool hasTransitions)
    {
        Track? baseTrack = sequence.VideoTracks.Count > 0 ? sequence.VideoTracks[0] : null;
        var spine = new List<SpineElement>();
        Time cursor = Time.Zero;

        if (baseTrack is not null && baseTrack.Clips.Count > 0)
        {
            List<Clip> sortedClips = baseTrack.Clips.OrderBy(c => c.Start).ToList();
            foreach (Clip clip in sortedClips)
            {
                if (clip.Start > cursor)
                {
                    spine.Add(new SpineGap(cursor, clip.Start - cursor));
                    cursor = clip.Start;
                }

                if (clip.Generator is not null)
                {
                    spine.Add(new SpineGap(cursor, clip.Duration, clip.Generator.Text));
                }
                else
                {
                    spine.Add(new SpineClip(cursor, clip.Duration, clip));
                }

                cursor += clip.Duration;
            }

            if (cursor < sequence.Duration)
            {
                spine.Add(new SpineGap(cursor, sequence.Duration - cursor));
                cursor = sequence.Duration;
            }
        }
        else
        {
            spine.Add(new SpineGap(Time.Zero, sequence.Duration));
        }

        // Attach connected clips from video tracks > 0
        for (int v = 1; v < sequence.VideoTracks.Count; v++)
        {
            Track track = sequence.VideoTracks[v];
            int lane = v;
            foreach (Clip clip in track.Clips)
            {
                SpineElement target = FindSpineElement(spine, clip.Start);
                target.Connected.Add(new ConnectedItem(clip, lane));
            }
        }

        // Attach connected clips from audio tracks
        for (int a = 0; a < sequence.AudioTracks.Count; a++)
        {
            Track track = sequence.AudioTracks[a];
            int lane = -(a + 1);
            foreach (Clip clip in track.Clips)
            {
                SpineElement target = FindSpineElement(spine, clip.Start);
                target.Connected.Add(new ConnectedItem(clip, lane));
            }
        }

        // Attach markers
        foreach (Marker marker in sequence.Markers)
        {
            SpineElement target = FindSpineElement(spine, marker.Time);
            target.Markers.Add(marker);
        }

        // Attach regions
        foreach (Region region in sequence.Regions)
        {
            SpineElement target = FindSpineElement(spine, region.Start);
            target.Regions.Add(region);
        }

        // Write spine items
        for (int i = 0; i < spine.Count; i++)
        {
            SpineElement item = spine[i];
            if (item is SpineGap gap)
            {
                writer.Start("gap")
                    .Attr("name", gap.TitleText is not null ? "Título" : "Hueco")
                    .Attr("offset", FcpxmlTime.FormatTime(tcStart + gap.Start))
                    .Attr("duration", FcpxmlTime.FormatTime(gap.Duration))
                    .Attr("start", "0s");

                if (gap.TitleText is not null)
                {
                    writer.Start("marker")
                        .Attr("start", "0s")
                        .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                        .Attr("value", gap.TitleText)
                        .End();
                }

                WriteElementChildren(writer, gap, tcStart, sequence, project, formatMap, assetMap, compoundMap);
                writer.End(); // gap
            }
            else if (item is SpineClip sc)
            {
                Clip clip = sc.Clip;
                if (clip.IsCompound)
                {
                    string refId = compoundMap.GetValueOrDefault(clip.SequenceId ?? "", "c1");
                    writer.Start("ref-clip")
                        .Attr("ref", refId)
                        .Attr("offset", FcpxmlTime.FormatTime(tcStart + sc.Start))
                        .Attr("name", project.FindSequence(clip.SequenceId!)?.Name ?? "Compuesto")
                        .Attr("start", FcpxmlTime.FormatTime(clip.SourceIn))
                        .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                    if (!clip.Enabled) writer.Attr("enabled", "0");
                    WriteClipAdjustments(writer, clip, sequence);
                    WriteElementChildren(writer, sc, tcStart, sequence, project, formatMap, assetMap, compoundMap);
                    writer.End(); // ref-clip
                }
                else
                {
                    string refId = assetMap.GetValueOrDefault(clip.MediaId, "a1");
                    MediaRef? media = project.FindMedia(clip.MediaId);
                    string? mediaFmtId = media?.HasVideo == true && media.FrameRate.HasValue
                        ? formatMap.GetValueOrDefault((media.FrameRate.Value, media.Width, media.Height))
                        : null;

                    writer.Start("asset-clip")
                        .Attr("ref", refId)
                        .Attr("offset", FcpxmlTime.FormatTime(tcStart + sc.Start))
                        .Attr("name", media?.Name ?? clip.MediaId)
                        .Attr("start", FcpxmlTime.FormatTime(clip.SourceIn))
                        .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                    if (mediaFmtId is not null) writer.Attr("format", mediaFmtId);
                    if (!clip.Enabled) writer.Attr("enabled", "0");

                    WriteClipAdjustments(writer, clip, sequence);
                    WriteElementChildren(writer, sc, tcStart, sequence, project, formatMap, assetMap, compoundMap);
                    writer.End(); // asset-clip
                }
            }

            // Check transition on baseTrack
            if (baseTrack is not null && item is SpineClip currentClip && i + 1 < spine.Count && spine[i + 1] is SpineClip nextClip)
            {
                TransitionInstance? trans = baseTrack.Transitions.Find(t => t.From == currentClip.Clip.Id && t.To == nextClip.Clip.Id);
                if (trans is not null)
                {
                    Time cutTime = tcStart + currentClip.Clip.Start + currentClip.Clip.Duration;
                    Time transOffset = cutTime - trans.Before;

                    writer.Start("transition")
                        .Attr("name", "Cross Dissolve")
                        .Attr("offset", FcpxmlTime.FormatTime(transOffset))
                        .Attr("duration", FcpxmlTime.FormatTime(trans.Duration));

                    writer.Start("filter-video")
                        .Attr("ref", "r-fade")
                        .Attr("name", "Cross Dissolve")
                        .End();

                    writer.End(); // transition
                }
            }
        }
    }

    private static SpineElement FindSpineElement(List<SpineElement> spine, Time time)
    {
        foreach (SpineElement item in spine)
        {
            if (time >= item.Start && time < item.End)
            {
                return item;
            }
        }

        return spine[^1];
    }

    private static void WriteElementChildren(
        XmlLiteWriter writer,
        SpineElement element,
        Time tcStart,
        Sequence sequence,
        Project project,
        Dictionary<(Rational Rate, int Width, int Height), string> formatMap,
        Dictionary<string, string> assetMap,
        Dictionary<string, string> compoundMap)
    {
        foreach (ConnectedItem conn in element.Connected)
        {
            Clip clip = conn.Clip;
            int lane = conn.Lane;
            Time offset = tcStart + clip.Start;

            if (clip.Generator is not null)
            {
                writer.Start("gap")
                    .Attr("lane", lane.ToString(CultureInfo.InvariantCulture))
                    .Attr("offset", FcpxmlTime.FormatTime(offset))
                    .Attr("duration", FcpxmlTime.FormatTime(clip.Duration))
                    .Attr("start", "0s");

                writer.Start("marker")
                    .Attr("start", "0s")
                    .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                    .Attr("value", clip.Generator.Text)
                    .End();

                writer.End(); // gap
            }
            else if (clip.IsCompound)
            {
                string refId = compoundMap.GetValueOrDefault(clip.SequenceId ?? "", "c1");
                writer.Start("ref-clip")
                    .Attr("ref", refId)
                    .Attr("lane", lane.ToString(CultureInfo.InvariantCulture))
                    .Attr("offset", FcpxmlTime.FormatTime(offset))
                    .Attr("name", project.FindSequence(clip.SequenceId!)?.Name ?? "Compuesto")
                    .Attr("start", FcpxmlTime.FormatTime(clip.SourceIn))
                    .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                if (!clip.Enabled) writer.Attr("enabled", "0");
                WriteClipAdjustments(writer, clip, sequence);
                writer.End(); // ref-clip
            }
            else
            {
                string refId = assetMap.GetValueOrDefault(clip.MediaId, "a1");
                MediaRef? media = project.FindMedia(clip.MediaId);
                string? mediaFmtId = media?.HasVideo == true && media.FrameRate.HasValue
                    ? formatMap.GetValueOrDefault((media.FrameRate.Value, media.Width, media.Height))
                    : null;

                writer.Start("asset-clip")
                    .Attr("ref", refId)
                    .Attr("lane", lane.ToString(CultureInfo.InvariantCulture))
                    .Attr("offset", FcpxmlTime.FormatTime(offset))
                    .Attr("name", media?.Name ?? clip.MediaId)
                    .Attr("start", FcpxmlTime.FormatTime(clip.SourceIn))
                    .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                if (mediaFmtId is not null) writer.Attr("format", mediaFmtId);
                if (!clip.Enabled) writer.Attr("enabled", "0");

                WriteClipAdjustments(writer, clip, sequence);
                writer.End(); // asset-clip
            }
        }

        foreach (Marker marker in element.Markers)
        {
            Time start = marker.Time - element.Start;
            writer.Start("marker")
                .Attr("start", FcpxmlTime.FormatTime(start))
                .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                .Attr("value", marker.Name)
                .End();
        }

        foreach (Region region in element.Regions)
        {
            Time start = region.Start - element.Start;
            writer.Start("chapter-marker")
                .Attr("start", FcpxmlTime.FormatTime(start))
                .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                .Attr("value", region.Name)
                .End();
        }
    }

    private static void WriteClipAdjustments(XmlLiteWriter writer, Clip clip, Sequence sequence)
    {
        EffectInstance? transformEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        EffectInstance? audioEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Audio);
        EffectInstance? cropEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Crop);

        if (transformEffect is not null)
        {
            writer.Start("adjust-conform").Attr("type", "fit").End();

            double x = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.X)?.Value ?? 0;
            double y = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Y)?.Value ?? 0;
            double scale = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Scale)?.Value ?? 1;
            double scaleX = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleX)?.Value ?? 1;
            double scaleY = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleY)?.Value ?? 1;
            double rotation = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Rotation)?.Value ?? 0;
            double anchorX = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorX)?.Value ?? 0;
            double anchorY = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorY)?.Value ?? 0;
            double opacity = transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Opacity)?.Value ?? 1;

            double alto = sequence.Height;
            double posX = x * 100.0 / alto;
            double posY = -y * 100.0 / alto;
            double sx = scale * scaleX;
            double sy = scale * scaleY;
            double rot = -rotation;
            double ancX = anchorX * 100.0 / alto;
            double ancY = -anchorY * 100.0 / alto;

            writer.Start("adjust-transform")
                .Attr("position", $"{FormatDouble(posX)} {FormatDouble(posY)}")
                .Attr("scale", $"{FormatDouble(sx)} {FormatDouble(sy)}")
                .Attr("rotation", FormatDouble(rot))
                .Attr("anchor", $"{FormatDouble(ancX)} {FormatDouble(ancY)}")
                .End();

            if (Math.Abs(opacity - 1.0) > 1e-6)
            {
                writer.Start("adjust-blend")
                    .Attr("amount", FormatDouble(opacity))
                    .End();
            }
        }

        if (audioEffect is not null)
        {
            double gainDb = audioEffect.Parameters.GetValueOrDefault(BuiltInEffects.AudioParameters.GainDb)?.Value ?? 0;
            if (Math.Abs(gainDb) > 1e-4)
            {
                writer.Start("adjust-volume")
                    .Attr("amount", $"{FormatDouble(gainDb)}dB")
                    .End();
            }
        }

        if (cropEffect is not null)
        {
            double left = cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Left)?.Value ?? 0;
            double top = cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Top)?.Value ?? 0;
            double right = cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Right)?.Value ?? 0;
            double bottom = cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Bottom)?.Value ?? 0;

            writer.Start("adjust-crop").Attr("mode", "trim");
            writer.Start("trim-rect")
                .Attr("left", FormatDouble(left))
                .Attr("top", FormatDouble(top))
                .Attr("right", FormatDouble(right))
                .Attr("bottom", FormatDouble(bottom))
                .End();
            writer.End(); // adjust-crop
        }

        if (clip.Speed is not null)
        {
            writer.Start("timeMap");
            if (clip.Speed.Freeze)
            {
                writer.Start("timept").Attr("start", "0s").Attr("value", "0s").Attr("interp", "linear").End();
                writer.Start("timept").Attr("start", FcpxmlTime.FormatTime(clip.Duration)).Attr("value", "0s").Attr("interp", "linear").End();
            }
            else if (clip.Speed.Reverse)
            {
                Time span = clip.SourceDuration;
                writer.Start("timept").Attr("start", "0s").Attr("value", FcpxmlTime.FormatTime(span)).Attr("interp", "linear").End();
                writer.Start("timept").Attr("start", FcpxmlTime.FormatTime(clip.Duration)).Attr("value", "0s").Attr("interp", "linear").End();
            }
            else
            {
                Time span = clip.SourceDuration;
                writer.Start("timept").Attr("start", "0s").Attr("value", "0s").Attr("interp", "linear").End();
                writer.Start("timept").Attr("start", FcpxmlTime.FormatTime(clip.Duration)).Attr("value", FcpxmlTime.FormatTime(span)).Attr("interp", "linear").End();
            }
            writer.End(); // timeMap
        }
    }

    private static string FormatDouble(double value)
    {
        double clean = Math.Abs(value) < 1e-12 ? 0.0 : value;
        return clean.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToUri(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "file:///";
        }

        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                return uri.AbsoluteUri;
            }

            string full = Path.GetFullPath(path);
            return new Uri(full).AbsoluteUri;
        }
        catch
        {
            return "file:///" + Uri.EscapeDataString(path.Replace('\\', '/'));
        }
    }

    private abstract class SpineElement(Time start, Time duration)
    {
        public Time Start { get; } = start;
        public Time Duration { get; } = duration;
        public Time End => Start + Duration;
        public List<ConnectedItem> Connected { get; } = [];
        public List<Marker> Markers { get; } = [];
        public List<Region> Regions { get; } = [];
    }

    private sealed class SpineGap(Time start, Time duration, string? titleText = null)
        : SpineElement(start, duration)
    {
        public string? TitleText { get; } = titleText;
    }

    private sealed class SpineClip(Time start, Time duration, Clip clip)
        : SpineElement(start, duration)
    {
        public Clip Clip { get; } = clip;
    }

    private sealed record ConnectedItem(Clip Clip, int Lane);
}
