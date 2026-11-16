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

        project = project.Clone();
        sequence = project.FindSequence(sequence.Id) ?? throw new ArgumentException("La secuencia principal no está en el proyecto.", nameof(sequence));
        if (options.VideoTracks is { } selected)
            sequence.VideoTracks = sequence.VideoTracks.Where((_, i) => selected.Contains(i)).ToList();
        sequence.StartTimecode ??= options.DefaultStartTimecode;

        var writer = new XmlLiteWriter("fcpxml");
        writer.Start("fcpxml").Attr("version", "1.10");

        List<Sequence> compounds = DraftCopy.CompoundsOf(project, sequence, options.Limits);
        var allSequences = compounds.Append(sequence).ToList();
        if (!options.IncludeAudio)
            foreach (Sequence seq in allSequences) seq.AudioTracks.Clear();
        var formatMap = new Dictionary<(Rational? Rate, int Width, int Height, SequenceColorSpace Color), string>();
        int resourceCounter = 1;

        string GetFormatId(Rational? rate, int width, int height, SequenceColorSpace color = SequenceColorSpace.Sdr709)
        {
            var key = (rate, width, height, color);
            if (!formatMap.TryGetValue(key, out string? id))
            {
                id = $"r{resourceCounter++}";
                formatMap[key] = id;
            }

            return id;
        }

        string mainFormatId = GetFormatId(sequence.FrameRate, sequence.Width, sequence.Height, sequence.ColorSpace);
        foreach (Sequence comp in compounds)
        {
            GetFormatId(comp.FrameRate, comp.Width, comp.Height, comp.ColorSpace);
        }

        foreach (MediaRef media in project.Media)
        {
            if (media.HasVideo && (media.FrameRate.HasValue || media.IsStillImage))
            {
                GetFormatId(media.FrameRate, media.Width, media.Height);
            }
        }

        var assetMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MediaRef media in project.Media)
        {
            assetMap[media.Id] = $"r{resourceCounter++}";
        }

        var compoundMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Sequence comp in compounds)
        {
            compoundMap[comp.Id] = $"r{resourceCounter++}";
        }

        bool hasTransitions = allSequences.Any(s => s.VideoTracks.Count > 0 && s.VideoTracks[0].Transitions.Count > 0);
        writer.Start("resources");

        foreach (var (key, id) in formatMap)
        {
            writer.Start("format")
                .Attr("id", id)
                .Attr("width", key.Width.ToString(CultureInfo.InvariantCulture))
                .Attr("height", key.Height.ToString(CultureInfo.InvariantCulture))
                .Attr("colorSpace", key.Color switch
                {
                    SequenceColorSpace.HdrPq => "9-16-9 (Rec. 2020 PQ)",
                    SequenceColorSpace.HdrHlg => "9-18-9 (Rec. 2020 HLG)",
                    _ => "1-1-1 (Rec. 709)",
                });
            if (key.Rate is { } rate) writer.Attr("frameDuration", FcpxmlTime.FormatFrameDuration(rate));
            writer.End();
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
            string? mediaFormatId = media.HasVideo && (media.FrameRate.HasValue || media.IsStillImage)
                ? formatMap.GetValueOrDefault((media.FrameRate, media.Width, media.Height, SequenceColorSpace.Sdr709))
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
                .Attr("src", ToUri(projectDirectory is null || string.IsNullOrWhiteSpace(media.Path) ? media.Path : media.ResolvePath(projectDirectory)))
                .End();

            writer.End();
        }

        foreach (Sequence comp in compounds)
        {
            string compId = compoundMap[comp.Id];
            string compFormatId = formatMap[(comp.FrameRate, comp.Width, comp.Height, comp.ColorSpace)];

            writer.Start("media")
                .Attr("id", compId)
                .Attr("name", comp.Name);

            writer.Start("sequence")
                .Attr("format", compFormatId)
                .Attr("duration", FcpxmlTime.FormatTime(comp.Duration))
                .Attr("tcStart", "0s")
                .Attr("tcFormat", "NDF");

            writer.Start("spine");
            WriteSpine(writer, comp, Time.Zero, project, formatMap, assetMap, compoundMap, options.IncludeAudio);
            writer.End();

            writer.End();
            writer.End();
        }

        writer.End();
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
        WriteSpine(writer, sequence, mainTcStart, project, formatMap, assetMap, compoundMap, options.IncludeAudio);
        writer.End();

        writer.End();
        writer.End();
        writer.End();
        writer.End();
        writer.End();

        return [new InterchangeOutput("", writer.ToString())];
    }

    private static void WriteSpine(
        XmlLiteWriter writer,
        Sequence sequence,
        Time tcStart,
        Project project,
        Dictionary<(Rational? Rate, int Width, int Height, SequenceColorSpace Color), string> formatMap,
        Dictionary<string, string> assetMap,
        Dictionary<string, string> compoundMap,
        bool includeAudio)
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
        foreach (Marker marker in sequence.Markers)
        {
            SpineElement target = FindSpineElement(spine, marker.Time);
            target.Markers.Add(marker);
        }
        foreach (Region region in sequence.Regions)
        {
            SpineElement target = FindSpineElement(spine, region.Start);
            target.Regions.Add(region);
        }
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

                WriteElementChildren(writer, gap, tcStart, sequence, project, formatMap, assetMap, compoundMap, includeAudio);
                writer.End();
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
                        .Attr("start", FcpxmlTime.FormatTime(FcpxmlTime.LocalStart(clip, project, sequence.FrameRate)))
                        .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                    if (!clip.Enabled) writer.Attr("enabled", "0");
                    WriteClipAdjustments(writer, clip, sequence, project, includeAudio);
                    WriteElementChildren(writer, sc, tcStart, sequence, project, formatMap, assetMap, compoundMap, includeAudio);
                    writer.End();
                }
                else
                {
                    // Un congelado no tiene mapa inverso: los anclajes van en un contenedor sin retimar.
                    bool container = clip.Speed?.Freeze == true && (sc.Connected.Count > 0 || sc.Markers.Count > 0 || sc.Regions.Count > 0);
                    if (container)
                    {
                        writer.Start("clip")
                            .Attr("offset", FcpxmlTime.FormatTime(tcStart + sc.Start))
                            .Attr("start", "0s")
                            .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));
                    }
                    string refId = assetMap.GetValueOrDefault(clip.MediaId, "a1");
                    MediaRef? media = project.FindMedia(clip.MediaId);
                    string? mediaFmtId = media?.HasVideo == true && (media.FrameRate.HasValue || media.IsStillImage)
                        ? formatMap.GetValueOrDefault((media.FrameRate, media.Width, media.Height, SequenceColorSpace.Sdr709))
                        : null;

                    writer.Start("asset-clip")
                        .Attr("ref", refId)
                        .Attr("offset", FcpxmlTime.FormatTime(container ? Time.Zero : tcStart + sc.Start))
                        .Attr("name", media?.Name ?? clip.MediaId)
                        .Attr("start", FcpxmlTime.FormatTime(FcpxmlTime.LocalStart(clip, project, sequence.FrameRate)))
                        .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                    if (mediaFmtId is not null) writer.Attr("format", mediaFmtId);
                    if (!clip.Enabled) writer.Attr("enabled", "0");

                    WriteClipAdjustments(writer, clip, sequence, project, includeAudio);
                    if (!container) WriteElementChildren(writer, sc, tcStart, sequence, project, formatMap, assetMap, compoundMap, includeAudio);
                    writer.End();
                    if (container)
                    {
                        WriteElementChildren(writer, sc, tcStart, sequence, project, formatMap, assetMap, compoundMap, includeAudio, unretimedContainer: true);
                        writer.End();
                    }
                }
            }
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

                    writer.End();
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
        Dictionary<(Rational? Rate, int Width, int Height, SequenceColorSpace Color), string> formatMap,
        Dictionary<string, string> assetMap,
        Dictionary<string, string> compoundMap,
        bool includeAudio,
        bool unretimedContainer = false)
    {
        foreach (ConnectedItem conn in element.Connected)
        {
            Clip clip = conn.Clip;
            int lane = conn.Lane;
            Time offset = unretimedContainer ? clip.Start - element.Start : ElementLocalTime(element, clip.Start, project, sequence.FrameRate);

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

                writer.End();
            }
            else if (clip.IsCompound)
            {
                string refId = compoundMap.GetValueOrDefault(clip.SequenceId ?? "", "c1");
                writer.Start("ref-clip")
                    .Attr("ref", refId)
                    .Attr("lane", lane.ToString(CultureInfo.InvariantCulture))
                    .Attr("offset", FcpxmlTime.FormatTime(offset))
                    .Attr("name", project.FindSequence(clip.SequenceId!)?.Name ?? "Compuesto")
                    .Attr("start", FcpxmlTime.FormatTime(FcpxmlTime.LocalStart(clip, project, sequence.FrameRate)))
                    .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                if (!clip.Enabled) writer.Attr("enabled", "0");
                WriteClipAdjustments(writer, clip, sequence, project, includeAudio);
                writer.End();
            }
            else
            {
                string refId = assetMap.GetValueOrDefault(clip.MediaId, "a1");
                MediaRef? media = project.FindMedia(clip.MediaId);
                string? mediaFmtId = media?.HasVideo == true && (media.FrameRate.HasValue || media.IsStillImage)
                    ? formatMap.GetValueOrDefault((media.FrameRate, media.Width, media.Height, SequenceColorSpace.Sdr709))
                    : null;

                writer.Start("asset-clip")
                    .Attr("ref", refId)
                    .Attr("lane", lane.ToString(CultureInfo.InvariantCulture))
                    .Attr("offset", FcpxmlTime.FormatTime(offset))
                    .Attr("name", media?.Name ?? clip.MediaId)
                    .Attr("start", FcpxmlTime.FormatTime(FcpxmlTime.LocalStart(clip, project, sequence.FrameRate)))
                    .Attr("duration", FcpxmlTime.FormatTime(clip.Duration));

                if (mediaFmtId is not null) writer.Attr("format", mediaFmtId);
                if (!clip.Enabled) writer.Attr("enabled", "0");

                WriteClipAdjustments(writer, clip, sequence, project, includeAudio);
                writer.End();
            }
        }

        foreach (Marker marker in element.Markers)
        {
            Time start = unretimedContainer ? marker.Time - element.Start : ElementLocalTime(element, marker.Time, project, sequence.FrameRate);
            writer.Start("marker")
                .Attr("start", FcpxmlTime.FormatTime(start))
                .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                .Attr("value", marker.Name)
                .End();
        }

        foreach (Region region in element.Regions)
        {
            Time start = unretimedContainer ? region.Start - element.Start : ElementLocalTime(element, region.Start, project, sequence.FrameRate);
            writer.Start("chapter-marker")
                .Attr("start", FcpxmlTime.FormatTime(start))
                .Attr("duration", FcpxmlTime.FormatFrameDuration(sequence.FrameRate))
                .Attr("value", region.Name)
                .End();
        }
    }

    private static Time ElementLocalTime(SpineElement element, Time timeline, Project project, Rational rate) =>
        element is SpineClip sc
            ? FcpxmlTime.SourceAt(sc.Clip, timeline - element.Start, sc.Clip.IsCompound ? Time.Zero : FcpxmlTime.AssetStart(project.FindMedia(sc.Clip.MediaId), rate))
            : timeline - element.Start;

    private static void WriteClipAdjustments(XmlLiteWriter writer, Clip clip, Sequence sequence, Project project, bool includeAudio)
    {
        if (!includeAudio) writer.Attr("srcEnable", "video");

        EffectInstance? transformEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        EffectInstance? audioEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Audio);
        EffectInstance? cropEffect = clip.Effects.Find(e => e.Type == BuiltInEffects.Crop);

        if (clip.Speed is not null)
        {
            Time localStart = FcpxmlTime.LocalStart(clip, project, sequence.FrameRate);
            Time assetStart = clip.IsCompound ? Time.Zero : FcpxmlTime.AssetStart(project.FindMedia(clip.MediaId), sequence.FrameRate);
            writer.Start("timeMap");
            writer.Start("timept").Attr("time", FcpxmlTime.FormatTime(localStart))
                .Attr("value", FcpxmlTime.FormatTime(FcpxmlTime.SourceAt(clip, Time.Zero, assetStart))).Attr("interp", "linear").End();
            writer.Start("timept").Attr("time", FcpxmlTime.FormatTime(localStart + clip.Duration))
                .Attr("value", FcpxmlTime.FormatTime(FcpxmlTime.SourceAt(clip, clip.Duration, assetStart))).Attr("interp", "linear").End();
            writer.End();
        }

        if (cropEffect is not null)
        {
            double left = FirstValue(cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Left), 0);
            double top = FirstValue(cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Top), 0);
            double right = FirstValue(cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Right), 0);
            double bottom = FirstValue(cropEffect.Parameters.GetValueOrDefault(BuiltInEffects.CropParameters.Bottom), 0);

            writer.Start("adjust-crop").Attr("mode", "trim");
            writer.Start("trim-rect")
                .Attr("left", FormatDouble(left))
                .Attr("top", FormatDouble(top))
                .Attr("right", FormatDouble(right))
                .Attr("bottom", FormatDouble(bottom))
                .End();
            writer.End();
        }

        if (transformEffect is not null)
        {
            writer.Start("adjust-conform").Attr("type", "fit").End();

            double x = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.X), 0);
            double y = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Y), 0);
            double scale = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Scale), 1);
            double scaleX = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleX), 1);
            double scaleY = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleY), 1);
            double rotation = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Rotation), 0);
            double anchorX = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorX), 0);
            double anchorY = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorY), 0);
            double opacity = FirstValue(transformEffect.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Opacity), 1);

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
            double gainDb = FirstValue(audioEffect.Parameters.GetValueOrDefault(BuiltInEffects.AudioParameters.GainDb), 0);
            writer.Start("adjust-volume")
                .Attr("amount", $"{FormatDouble(gainDb)}dB")
                .End();
        }
    }

    private static double FirstValue(Parameter? parameter, double fallback) =>
        parameter is null ? fallback : parameter.Keyframes.Count > 0 ? parameter.Keyframes[0].Value : parameter.Value;

    private static string FormatDouble(double value)
    {
        double clean = Math.Abs(value) < 1e-12 ? 0.0 : value;
        return clean.ToString(CultureInfo.InvariantCulture);
    }

    private static string ToUri(string path) => string.IsNullOrWhiteSpace(path) ? "file:///" : SequenceBuilder.ToFileUri(path);

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
