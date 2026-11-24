using System.Globalization;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>
/// Lee FCPXML 1.10. Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe; un archivo roto o
/// que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/> (D-062).
/// </summary>
public static class FcpxmlReader
{
    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);

        XmlNode root = XmlLite.Parse(text, options.Limits);
        if (root.Name != "fcpxml")
        {
            throw new InterchangeException("El XML no es un documento FCPXML válido.");
        }

        string version = root["version"] ?? "1.10";
        if (!IsSupportedVersion(version))
        {
            report.Add(IssueSeverity.Info, IssueKind.Unknown, $"Versión de FCPXML no probada: «{version}».");
        }

        var project = new Project { Id = "project", Name = "Proyecto" };
        var formats = new Dictionary<string, (Rational? Rate, int Width, int Height, SequenceColorSpace Color)>(StringComparer.Ordinal);
        var assetStarts = new Dictionary<string, Time>(StringComparer.Ordinal);
        var compoundNodes = new Dictionary<string, (string Name, XmlNode SequenceNode)>(StringComparer.Ordinal);

        XmlNode? resourcesNode = root.Element("resources");
        if (resourcesNode is not null)
        {
            foreach (XmlNode fmtNode in resourcesNode.Elements("format"))
            {
                string? id = fmtNode["id"];
                if (id is null) continue;

                Rational? rate = fmtNode["frameDuration"] is null ? null : FcpxmlTime.ParseFrameDuration(fmtNode["frameDuration"]);
                int width = int.TryParse(fmtNode["width"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : 1920;
                int height = int.TryParse(fmtNode["height"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ? h : 1080;
                SequenceColorSpace color = fmtNode["colorSpace"] switch
                {
                    { } value when value.StartsWith("9-16-9", StringComparison.Ordinal) => SequenceColorSpace.HdrPq,
                    { } value when value.StartsWith("9-18-9", StringComparison.Ordinal) => SequenceColorSpace.HdrHlg,
                    _ => SequenceColorSpace.Sdr709,
                };
                formats[id] = (rate, width, height, color);
            }

            foreach (XmlNode assetNode in resourcesNode.Elements("asset"))
            {
                string? id = assetNode["id"];
                if (id is null) continue;

                string name = assetNode["name"] ?? id;
                XmlNode? mediaRep = assetNode.Element("media-rep");
                string src = mediaRep?["src"] ?? "";
                string? path = SequenceBuilder.NormalizePath(src, baseDirectory);
                if (path is null)
                {
                    path = "";
                    report.Add(IssueSeverity.Info, IssueKind.Media, $"Ruta de red o no válida omitida: «{src}».");
                }

                bool hasVideo = assetNode["hasVideo"] == "1";
                bool hasAudio = assetNode["hasAudio"] == "1";
                Rational? mediaRate = null;
                int width = 1920;
                int height = 1080;

                string? fmtRef = assetNode["format"];
                if (fmtRef is not null && formats.TryGetValue(fmtRef, out var fmt))
                {
                    mediaRate = fmt.Rate;
                    width = fmt.Width;
                    height = fmt.Height;
                }

                Time duration = FcpxmlTime.ParseDuration(assetNode["duration"]);
                Time startTime = FcpxmlTime.ParseTime(assetNode["start"]);
                assetStarts[id] = startTime;
                bool still = hasVideo && !hasAudio && duration == Time.Zero && mediaRate is null;
                int channels = int.TryParse(assetNode["audioChannels"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch) ? ch : 2;
                int sampleRate = int.TryParse(assetNode["audioRate"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sr) ? sr : 48000;

                var media = new MediaRef
                {
                    Id = id,
                    Name = name,
                    Path = path,
                    Duration = duration,
                    IsStillImage = still,
                    HasVideo = hasVideo,
                    HasAudio = hasAudio,
                    FrameRate = hasVideo && !still ? mediaRate ?? new Rational(25, 1) : null,
                    Width = width,
                    Height = height,
                    AudioChannels = still ? 0 : channels,
                    SampleRate = still ? 0 : sampleRate,
                };

                if (startTime > Time.Zero && media.FrameRate.HasValue)
                {
                    media.Timecode = SmpteTimecode.FromTime(startTime, media.FrameRate.Value, dropFrame: false).Format();
                }

                project.Media.Add(media);
            }

            foreach (XmlNode mediaNode in resourcesNode.Elements("media"))
            {
                string? id = mediaNode["id"];
                XmlNode? seqNode = mediaNode.Element("sequence");
                if (id is not null && seqNode is not null)
                {
                    string name = mediaNode["name"] ?? id;
                    compoundNodes[id] = (name, seqNode);
                }
            }
        }

        XmlNode? mainSequenceNode = FindMainSequence(root);
        if (mainSequenceNode is null)
        {
            throw new InterchangeException("No se encontró la secuencia principal en el archivo FCPXML.");
        }

        int nextId = 1;
        var compoundSequences = new Dictionary<string, Sequence>(StringComparer.Ordinal);
        foreach (var (compMediaId, (compName, compSeqNode)) in compoundNodes)
        {
            Sequence compSeq = CreateSequence(compMediaId, compName, compSeqNode, formats, assetStarts, report, ref nextId, isCompound: true);
            compoundSequences[compMediaId] = compSeq;
            project.Sequences.Add(compSeq);
        }

        string mainName = FindProjectName(root) ?? mainSequenceNode["name"] ?? "Secuencia";
        Sequence mainSequence = CreateSequence("principal", mainName, mainSequenceNode, formats, assetStarts, report, ref nextId, isCompound: false);
        project.Sequences.Add(mainSequence);

        DraftCopy.CompoundsOf(project, mainSequence, options.Limits);

        return new TimelineDraft(project, mainSequence);
    }

    private static bool IsSupportedVersion(string version) =>
        version is "1.8" or "1.9" or "1.10" or "1.11" or "1.12" or "1.13";

    private static XmlNode? FindMainSequence(XmlNode root)
    {
        XmlNode? seq = root.Element("library")?.Element("event")?.Element("project")?.Element("sequence");
        if (seq is not null) return seq;

        seq = root.Element("event")?.Element("project")?.Element("sequence");
        if (seq is not null) return seq;

        seq = root.Element("project")?.Element("sequence");
        if (seq is not null) return seq;

        seq = root.Element("sequence");
        if (seq is not null) return seq;

        return FindDescendant(root, "sequence");
    }

    private static string? FindProjectName(XmlNode root)
    {
        XmlNode? proj = root.Element("library")?.Element("event")?.Element("project")
            ?? root.Element("event")?.Element("project")
            ?? root.Element("project")
            ?? FindDescendant(root, "project");

        return proj?["name"];
    }

    private static XmlNode? FindDescendant(XmlNode node, string name)
    {
        foreach (XmlNode child in node.Children)
        {
            if (child.Name == name) return child;
            XmlNode? found = FindDescendant(child, name);
            if (found is not null) return found;
        }

        return null;
    }

    private static Sequence CreateSequence(
        string id,
        string name,
        XmlNode sequenceNode,
        Dictionary<string, (Rational? Rate, int Width, int Height, SequenceColorSpace Color)> formats,
        Dictionary<string, Time> assetStarts,
        InterchangeReport report,
        ref int nextId,
        bool isCompound)
    {
        string? fmtId = sequenceNode["format"];
        Rational rate = new(25, 1);
        int width = 1920;
        int height = 1080;
        SequenceColorSpace color = SequenceColorSpace.Sdr709;
        if (fmtId is not null && formats.TryGetValue(fmtId, out var fmt))
        {
            rate = fmt.Rate ?? rate;
            width = fmt.Width;
            height = fmt.Height;
            color = fmt.Color;
        }

        string? tcStartRaw = sequenceNode["tcStart"];
        string tcFormat = sequenceNode["tcFormat"] ?? "NDF";
        bool isDropFrame = string.Equals(tcFormat, "DF", StringComparison.OrdinalIgnoreCase);

        Time tcStartTime = Time.Zero;
        if (!string.IsNullOrWhiteSpace(tcStartRaw))
        {
            try
            {
                tcStartTime = FcpxmlTime.ParseTime(tcStartRaw);
            }
            catch (InterchangeException)
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Rounding, $"tcStart no válido en FCPXML: «{tcStartRaw}».");
            }
        }

        string? startTimecode = null;
        if (!string.IsNullOrWhiteSpace(tcStartRaw))
        {
            if (tcStartRaw.Contains(':'))
            {
                if (SmpteTimecode.TryParse(tcStartRaw, rate, out SmpteTimecode parsed))
                {
                    startTimecode = parsed.Format();
                }
                else
                {
                    report.Add(IssueSeverity.Approximate, IssueKind.Rounding, $"tcStart no válido como código de tiempo: «{tcStartRaw}».");
                }
            }
            else
            {
                if (SmpteTimecode.CanDropFrame(rate) || !isDropFrame)
                {
                    startTimecode = SmpteTimecode.FromTime(tcStartTime, rate, isDropFrame && SmpteTimecode.CanDropFrame(rate)).Format();
                }
            }
        }

        var seq = new Sequence
        {
            Id = id,
            Name = name,
            FrameRate = rate,
            Width = width,
            Height = height,
            ColorSpace = color,
            StartTimecode = isCompound ? null : startTimecode,
        };

        XmlNode? spineNode = sequenceNode.Element("spine");
        if (spineNode is not null)
        {
            ParseSpine(spineNode, seq, tcStartTime, assetStarts, report, ref nextId);
        }

        return seq;
    }

    private static void ParseSpine(
        XmlNode spineNode,
        Sequence sequence,
        Time seqTcStart,
        Dictionary<string, Time> assetStarts,
        InterchangeReport report,
        ref int nextId)
    {
        var videoTrackMap = new SortedDictionary<int, Track>();
        var audioTrackMap = new SortedDictionary<int, Track>(Comparer<int>.Create((x, y) => Math.Abs(x).CompareTo(Math.Abs(y))));

        Clip? lastSpineClip = null;
        (Time Offset, Time Duration)? pendingTransition = null;
        Time cursor = Time.Zero;

        foreach (XmlNode node in spineNode.Children)
        {
            if (node.Name == "transition")
            {
                Time transOffset = FcpxmlTime.ParseTime(node["offset"]);
                Time transDuration = FcpxmlTime.ParseDuration(node["duration"]);
                if (lastSpineClip is not null)
                {
                    pendingTransition = (transOffset, transDuration);
                }
                continue;
            }

            if (node.Name == "gap")
            {
                Time gapDuration = FcpxmlTime.ParseDuration(node["duration"]);
                Time gapOffset = node["offset"] is not null ? FcpxmlTime.ParseTime(node["offset"]) : cursor;
                cursor = gapOffset + gapDuration;
                Time gapStart = gapOffset - seqTcStart;

                ProcessChildren(node, sequence, gapStart, videoTrackMap, audioTrackMap, assetStarts, report, ref nextId);
                lastSpineClip = null;
                pendingTransition = null;
                continue;
            }

            if (IsClipNode(node.Name))
            {
                Time clipDuration = FcpxmlTime.ParseDuration(node["duration"]);
                Time clipOffset = node["offset"] is not null ? FcpxmlTime.ParseTime(node["offset"]) : cursor;
                cursor = clipOffset + clipDuration;
                Time clipStart = clipOffset - seqTcStart;

                int lane = 0;
                if (node["lane"] is not null && int.TryParse(node["lane"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l))
                {
                    lane = l;
                }
                else if (node.Name == "audio")
                {
                    lane = -1;
                }

                Track track = GetOrCreateTrack(lane, videoTrackMap, audioTrackMap);
                Clip clip = ParseClip(node, clipStart, clipDuration, sequence, assetStarts, report, ref nextId);
                track.Clips.Add(clip);

                if (lane == 0)
                {
                    if (pendingTransition is not null && lastSpineClip is not null)
                    {
                        Time cutTime = lastSpineClip.Start + lastSpineClip.Duration;
                        Time transOffsetFromSeq = pendingTransition.Value.Offset - seqTcStart;
                        Time before = cutTime - transOffsetFromSeq;
                        Time transDuration = pendingTransition.Value.Duration;

                        TransitionAlignment alignment;
                        if (Math.Abs((before - transDuration).Ticks) <= 100)
                        {
                            alignment = TransitionAlignment.Before;
                        }
                        else if (Math.Abs(before.Ticks) <= 100)
                        {
                            alignment = TransitionAlignment.After;
                        }
                        else
                        {
                            alignment = TransitionAlignment.Center;
                        }

                        track.Transitions.Add(new TransitionInstance
                        {
                            Id = $"t_{nextId++}",
                            Type = BuiltInTransitions.Crossfade,
                            From = lastSpineClip.Id,
                            To = clip.Id,
                            Duration = transDuration,
                            Alignment = alignment,
                        });
                        pendingTransition = null;
                    }

                    lastSpineClip = clip;
                }

                ProcessChildren(node, sequence, clipStart, videoTrackMap, audioTrackMap, assetStarts, report, ref nextId);
            }
            else
            {
                report.Add(IssueSeverity.Info, IssueKind.Unknown, $"Elemento «{node.Name}» en spine omitido.");
            }
        }

        sequence.VideoTracks.Clear();
        int vIndex = 1;
        foreach (Track t in videoTrackMap.Values)
        {
            t.Name = $"V{vIndex++}";
            t.SortClips();
            sequence.VideoTracks.Add(t);
        }

        sequence.AudioTracks.Clear();
        int aIndex = 1;
        foreach (Track t in audioTrackMap.Values)
        {
            t.Name = $"A{aIndex++}";
            t.SortClips();
            sequence.AudioTracks.Add(t);
        }

        sequence.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static void ProcessChildren(
        XmlNode parentNode,
        Sequence sequence,
        Time parentStartOnTimeline,
        SortedDictionary<int, Track> videoTrackMap,
        SortedDictionary<int, Track> audioTrackMap,
        Dictionary<string, Time> assetStarts,
        InterchangeReport report,
        ref int nextId)
    {
        XmlNode? primary = PrimaryClip(parentNode);
        foreach (XmlNode child in parentNode.Children)
        {
            if (ReferenceEquals(child, primary))
            {
                ProcessChildren(child, sequence, parentStartOnTimeline, videoTrackMap, audioTrackMap, assetStarts, report, ref nextId);
                continue;
            }
            if (child.Name == "marker")
            {
                Time start = FcpxmlTime.ParseTime(child["start"]);
                sequence.Markers.Add(new Marker
                {
                    Id = $"mk_{nextId++}",
                    Name = child["value"] ?? "",
                    Time = parentStartOnTimeline + FcpxmlTime.ParentElapsed(parentNode, start),
                });
                continue;
            }

            if (child.Name == "chapter-marker")
            {
                Time start = FcpxmlTime.ParseTime(child["start"]);
                sequence.Markers.Add(new Marker
                {
                    Id = $"mk_{nextId++}",
                    Name = child["value"] ?? "",
                    Time = parentStartOnTimeline + FcpxmlTime.ParentElapsed(parentNode, start),
                });
                report.Add(IssueSeverity.Approximate, IssueKind.Marker, "Marcador de capítulo convertido en marcador.");
                continue;
            }

            if (child.Name == "gap")
            {
                Time gapDuration = FcpxmlTime.ParseDuration(child["duration"]);
                Time gapOffset = FcpxmlTime.ParseTime(child["offset"] ?? parentNode["start"]);
                Time gapStart = parentStartOnTimeline + FcpxmlTime.ParentElapsed(parentNode, gapOffset, child["lane"] is null or "0");
                ProcessChildren(child, sequence, gapStart, videoTrackMap, audioTrackMap, assetStarts, report, ref nextId);
                continue;
            }

            if (IsClipNode(child.Name))
            {
                Time childDuration = FcpxmlTime.ParseDuration(child["duration"]);
                Time childOffset = FcpxmlTime.ParseTime(child["offset"] ?? parentNode["start"]);
                Time childStart = parentStartOnTimeline + FcpxmlTime.ParentElapsed(parentNode, childOffset, child["lane"] == "0");

                int lane = 1;
                if (child["lane"] is not null && int.TryParse(child["lane"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l))
                {
                    lane = l;
                }
                else if (child.Name == "audio")
                {
                    lane = -1;
                }

                Track track = GetOrCreateTrack(lane, videoTrackMap, audioTrackMap);
                Clip childClip = ParseClip(child, childStart, childDuration, sequence, assetStarts, report, ref nextId);
                track.Clips.Add(childClip);

                ProcessChildren(child, sequence, childStart, videoTrackMap, audioTrackMap, assetStarts, report, ref nextId);
            }
        }
    }

    private static Track GetOrCreateTrack(
        int lane,
        SortedDictionary<int, Track> videoTrackMap,
        SortedDictionary<int, Track> audioTrackMap)
    {
        if (lane >= 0)
        {
            if (!videoTrackMap.TryGetValue(lane, out Track? t))
            {
                t = new Track { Id = $"track_v{lane}", Kind = TrackKind.Video };
                videoTrackMap[lane] = t;
            }

            return t;
        }
        else
        {
            if (!audioTrackMap.TryGetValue(lane, out Track? t))
            {
                t = new Track { Id = $"track_a{lane}", Kind = TrackKind.Audio };
                audioTrackMap[lane] = t;
            }

            return t;
        }
    }

    private static bool IsClipNode(string name) =>
        name is "asset-clip" or "ref-clip" or "clip" or "video" or "audio" or "sync-clip" or "mc-clip";

    private static Clip ParseClip(
        XmlNode node,
        Time start,
        Time duration,
        Sequence sequence,
        Dictionary<string, Time> assetStarts,
        InterchangeReport report,
        ref int nextId)
    {
        if (PrimaryClip(node) is { } primary)
            return ParseClip(primary, start, duration, sequence, assetStarts, report, ref nextId);

        Time sourceIn = node["start"] is not null ? FcpxmlTime.ParseTime(node["start"]) : Time.Zero;
        var clip = new Clip
        {
            Id = $"c_{nextId++}",
            Start = start,
            SourceIn = sourceIn,
            SourceOut = sourceIn + duration,
            Duration = duration,
        };

        if (node["enabled"] == "0")
        {
            clip.Enabled = false;
        }

        if (node.Name == "ref-clip")
        {
            clip.SequenceId = node["ref"];
        }
        else
        {
            string? refId = node["ref"];
            if (refId is null)
            {
                XmlNode? childRef = node.Element("video") ?? node.Element("audio");
                refId = childRef?["ref"];
            }

            clip.MediaId = refId ?? "";
        }

        Time assetStart = assetStarts.GetValueOrDefault(clip.MediaId);
        clip.SourceIn -= assetStart;
        clip.SourceOut -= assetStart;

        XmlNode? timeMap = node.Element("timeMap");
        if (timeMap is not null)
        {
            var timepts = timeMap.Elements("timept").ToList();
            if (timepts.Count > 0)
            {
                Time v0 = FcpxmlTime.MapSource(node, sourceIn) - assetStart;
                Time v1 = FcpxmlTime.MapSource(node, sourceIn + duration) - assetStart;
                clip.SourceIn = Time.Min(v0, v1);

                if (v0 == v1)
                {
                    clip.Speed = new ClipSpeed { Freeze = true };
                    clip.SourceOut = clip.SourceIn + Time.FromTicks(1);
                }
                else if (v0 < v1)
                {
                    Time sourceSpan = v1 - v0;
                    Time timeSpan = duration;
                    Rational rate = FcpxmlTime.RationalFromTicks(sourceSpan.Ticks, timeSpan.Ticks);
                    clip.Speed = new ClipSpeed { Rate = rate };
                    clip.SourceOut = clip.SourceIn + sourceSpan;
                }
                else
                {
                    Time sourceSpan = v0 - v1;
                    Time timeSpan = duration;
                    Rational rate = FcpxmlTime.RationalFromTicks(sourceSpan.Ticks, timeSpan.Ticks);
                    clip.Speed = new ClipSpeed { Rate = rate, Reverse = true };
                    clip.SourceOut = clip.SourceIn + sourceSpan;
                }

                clip.Duration = duration;

                if (timepts.Count > 2 || timepts.Any(p => p["interp"] != "linear"))
                {
                    report.Add(IssueSeverity.Approximate, IssueKind.Speed, "En FCPXML la curva se cambia por su velocidad media.", node["name"] ?? clip.Id, clip.Id, start);
                }
            }
        }

        XmlNode? adjustTransform = node.Element("adjust-transform");
        XmlNode? adjustBlend = node.Element("adjust-blend");
        if (adjustTransform is not null || adjustBlend is not null)
        {
            double x_px = 0, y_px = 0, scale = 1.0, scaleX = 1.0, scaleY = 1.0, rotation = 0, anchorX = 0, anchorY = 0, opacity = 1.0;

            if (adjustTransform is not null)
            {
                if (adjustTransform["position"] is { } posStr)
                {
                    string[] parts = posStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double posX)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double posY))
                    {
                        x_px = posX * sequence.Height / 100.0;
                        y_px = posY == 0 ? 0 : -posY * sequence.Height / 100.0;
                    }
                }

                if (adjustTransform["scale"] is { } scaleStr)
                {
                    string[] parts = scaleStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sx)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sy))
                    {
                        if (Math.Abs(sx - sy) < 1e-9)
                        {
                            scale = sx;
                            scaleX = 1.0;
                            scaleY = 1.0;
                        }
                        else
                        {
                            scale = 1.0;
                            scaleX = sx;
                            scaleY = sy;
                        }
                    }
                }

                if (adjustTransform["rotation"] is { } rotStr && double.TryParse(rotStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double rot))
                {
                    rotation = rot == 0 ? 0 : -rot;
                }

                if (adjustTransform["anchor"] is { } ancStr)
                {
                    string[] parts = ancStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ancX)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ancY))
                    {
                        anchorX = ancX * sequence.Height / 100.0;
                        anchorY = ancY == 0 ? 0 : -ancY * sequence.Height / 100.0;
                    }
                }
            }

            if (adjustBlend is not null && adjustBlend["amount"] is { } opStr && double.TryParse(opStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double op))
            {
                opacity = op;
            }

            var effect = new EffectInstance
            {
                Id = $"eff_{nextId++}",
                Type = BuiltInEffects.Transform,
                Parameters = new Dictionary<string, Parameter>(StringComparer.Ordinal)
                {
                    [BuiltInEffects.TransformParameters.X] = new Parameter { Value = x_px },
                    [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = y_px },
                    [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = scale },
                    [BuiltInEffects.TransformParameters.ScaleX] = new Parameter { Value = scaleX },
                    [BuiltInEffects.TransformParameters.ScaleY] = new Parameter { Value = scaleY },
                    [BuiltInEffects.TransformParameters.Rotation] = new Parameter { Value = rotation },
                    [BuiltInEffects.TransformParameters.AnchorX] = new Parameter { Value = anchorX },
                    [BuiltInEffects.TransformParameters.AnchorY] = new Parameter { Value = anchorY },
                    [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = opacity },
                }
            };
            clip.Effects.Add(effect);
        }

        XmlNode? adjustVolume = node.Element("adjust-volume");
        if (adjustVolume is not null && adjustVolume["amount"] is { } volStr)
        {
            string volClean = volStr.Trim().TrimEnd('d', 'B', 'D', 'b');
            if (double.TryParse(volClean, NumberStyles.Float, CultureInfo.InvariantCulture, out double gainDb))
            {
                var audioEffect = new EffectInstance
                {
                    Id = $"eff_{nextId++}",
                    Type = BuiltInEffects.Audio,
                    Parameters = new Dictionary<string, Parameter>(StringComparer.Ordinal)
                    {
                        [BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = gainDb }
                    }
                };
                clip.Effects.Add(audioEffect);
            }
        }

        XmlNode? adjustCrop = node.Element("adjust-crop");
        if (adjustCrop is not null)
        {
            XmlNode? trimRect = adjustCrop.Element("trim-rect") ?? adjustCrop.Element("crop-rect");
            double left = 0, top = 0, right = 0, bottom = 0;
            if (trimRect is not null)
            {
                if (trimRect["left"] is { } lStr) double.TryParse(lStr, NumberStyles.Float, CultureInfo.InvariantCulture, out left);
                if (trimRect["top"] is { } tStr) double.TryParse(tStr, NumberStyles.Float, CultureInfo.InvariantCulture, out top);
                if (trimRect["right"] is { } rStr) double.TryParse(rStr, NumberStyles.Float, CultureInfo.InvariantCulture, out right);
                if (trimRect["bottom"] is { } bStr) double.TryParse(bStr, NumberStyles.Float, CultureInfo.InvariantCulture, out bottom);
            }

            var cropEffect = new EffectInstance
            {
                Id = $"eff_{nextId++}",
                Type = BuiltInEffects.Crop,
                Parameters = new Dictionary<string, Parameter>(StringComparer.Ordinal)
                {
                    [BuiltInEffects.CropParameters.Left] = new Parameter { Value = left },
                    [BuiltInEffects.CropParameters.Top] = new Parameter { Value = top },
                    [BuiltInEffects.CropParameters.Right] = new Parameter { Value = right },
                    [BuiltInEffects.CropParameters.Bottom] = new Parameter { Value = bottom },
                }
            };
            clip.Effects.Add(cropEffect);
        }

        return clip;
    }

    private static XmlNode? PrimaryClip(XmlNode node) => node.Name == "clip"
        && node.Element("timeMap") is null
        && node["enabled"] != "0"
        && !node.Children.Any(c => c.Name.StartsWith("adjust-", StringComparison.Ordinal))
        && FcpxmlTime.ParseTime(node["start"]) == Time.Zero
            ? node.Elements("asset-clip").FirstOrDefault(c => c["lane"] is null or "0"
                && FcpxmlTime.ParseTime(c["offset"]) == Time.Zero && c["duration"] == node["duration"])
            : null;
}
