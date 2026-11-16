using System.Text;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>Escribe OpenTimelineIO (.otio) con metadatos de Rodaje y huellas SHA-256 (D-062).</summary>
public static class OtioWriter
{
    public static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);

        // La huella firma lo que el lector leerá de la parte estándar: se escribe una vez sin ella y se relee, así no depende
        // de que la regla sin meta acierte con cada detalle del proyecto.
        string unsigned = Write(project, sequence, projectDirectory, options, "")[0].Text;
        TimelineDraft standardDraft = OtioReader.Read(unsigned, null, options, new InterchangeReport(), standardOnly: true);
        return Write(project, sequence, projectDirectory, options, OtioFingerprint.TimelineFingerprint(standardDraft));
    }

    private static IReadOnlyList<InterchangeOutput> Write(Project project, Sequence sequence, string? projectDirectory, InterchangeOptions options, string timelineHuella)
    {

        List<Sequence> compounds = DraftCopy.CompoundsOf(project, sequence, options.Limits);
        var usedMediaIds = sequence.AllTracks.SelectMany(t => t.Clips)
            .Concat(compounds.SelectMany(c => c.AllTracks.SelectMany(t => t.Clips)))
            .Where(c => c.Generator is null && c.SequenceId is null)
            .Select(c => c.MediaId)
            .ToHashSet(StringComparer.Ordinal);
        List<MediaRef> usedMedia = [.. project.Media.Where(m => usedMediaIds.Contains(m.Id))];

        var fullProj = new Project
        {
            SchemaVersion = 16,
            Id = project.Id,
            Media = usedMedia,
            Sequences = [sequence, .. compounds],
        };
        string projJson = ProjectSerializer.Serialize(fullProj);
        using var projDoc = JsonDocument.Parse(projJson);
        JsonElement rootProj = projDoc.RootElement;
        JsonElement mainSeqElem = rootProj.GetProperty("sequences")[0];
        JsonElement mediaArrayElem = rootProj.GetProperty("media");

        var clipJsonElements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonElement seqElem in rootProj.GetProperty("sequences").EnumerateArray())
        {
            foreach (string trackProp in new[] { "videoTracks", "audioTracks" })
            {
                if (seqElem.TryGetProperty(trackProp, out JsonElement tracksElem))
                {
                    foreach (JsonElement trackElem in tracksElem.EnumerateArray())
                    {
                        if (trackElem.TryGetProperty("clips", out JsonElement clipsElem))
                        {
                            foreach (JsonElement clipElem in clipsElem.EnumerateArray())
                            {
                                if (clipElem.TryGetProperty("id", out JsonElement idElem) && idElem.GetString() is { } id)
                                {
                                    clipJsonElements[id] = clipElem.Clone();
                                }
                            }
                        }
                    }
                }
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("OTIO_SCHEMA", "Timeline.1");
            writer.WriteString("name", sequence.Name);

            writer.WriteStartObject("global_start_time");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            string tcStr = sequence.StartTimecode ?? options.DefaultStartTimecode;
            long tcFrame = SmpteTimecode.TryParse(tcStr, sequence.FrameRate, out SmpteTimecode sTc) ? sTc.Frame : 0;
            writer.WriteNumber("value", tcFrame);
            writer.WriteNumber("rate", sequence.FrameRate.ToDouble());
            writer.WriteEndObject();

            writer.WriteStartObject("metadata");
            writer.WriteStartObject("rodaje");
            writer.WriteNumber("v", 1);
            writer.WritePropertyName("sequence");
            mainSeqElem.WriteTo(writer);

            writer.WriteStartArray("sequences");
            bool isFirst = true;
            foreach (JsonElement seqElem in rootProj.GetProperty("sequences").EnumerateArray())
            {
                if (isFirst)
                {
                    isFirst = false;
                    continue;
                }

                seqElem.WriteTo(writer);
            }

            writer.WriteEndArray();

            writer.WritePropertyName("media");
            mediaArrayElem.WriteTo(writer);

            writer.WriteString("huella", timelineHuella);
            writer.WriteEndObject(); // rodaje
            writer.WriteEndObject(); // metadata

            writer.WriteStartObject("tracks");
            writer.WriteString("OTIO_SCHEMA", "Stack.1");
            writer.WriteString("name", "tracks");
            writer.WriteStartObject("metadata");
            writer.WriteEndObject();

            writer.WriteStartArray("markers");
            WriteMarkersAndRegions(writer, sequence, sequence.FrameRate);
            writer.WriteEndArray();

            writer.WriteStartArray("children");
            IReadOnlyList<WalkTrack> walkTracks = TimelineWalk.Of(project, sequence, flattenCompounds: false, options.Limits, new InterchangeReport());
            if (options.VideoTracks is not null)
            {
                var sel = new HashSet<int>(options.VideoTracks);
                walkTracks = [.. walkTracks.Where(w => w.Kind != TrackKind.Video || sel.Contains(w.Index))];
            }

            if (!options.IncludeAudio)
            {
                walkTracks = [.. walkTracks.Where(w => w.Kind != TrackKind.Audio)];
            }

            foreach (WalkTrack walkTrack in walkTracks)
            {
                WriteTrack(writer, project, walkTrack, sequence.FrameRate, projectDirectory, options, clipJsonElements);
            }

            writer.WriteEndArray(); // tracks children
            writer.WriteEndObject(); // tracks
            writer.WriteEndObject(); // Timeline.1
        }

        string jsonOutput = Encoding.UTF8.GetString(stream.ToArray());
        return [new InterchangeOutput("", jsonOutput)];
    }

    private static void WriteTrack(
        Utf8JsonWriter writer, Project project, WalkTrack walkTrack, Rational rate, string? projectDirectory, InterchangeOptions options, Dictionary<string, JsonElement> clipJsonElements)
    {
        writer.WriteStartObject();
        writer.WriteString("OTIO_SCHEMA", "Track.1");
        writer.WriteString("name", walkTrack.Track.Name);
        writer.WriteString("kind", walkTrack.Kind == TrackKind.Video ? "Video" : "Audio");
        writer.WriteStartObject("metadata");
        writer.WriteEndObject();

        writer.WriteStartArray("children");
        foreach (WalkItem item in walkTrack.Items)
        {
            if (item is WalkGap gap)
            {
                writer.WriteStartObject();
                writer.WriteString("OTIO_SCHEMA", "Gap.1");
                writer.WriteString("name", "Hueco");
                writer.WriteStartObject("source_range");
                writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
                writer.WriteStartObject("start_time");
                writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
                writer.WriteNumber("value", 0);
                writer.WriteNumber("rate", rate.ToDouble());
                writer.WriteEndObject();
                writer.WriteStartObject("duration");
                writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
                (double durVal, double durRate) = OtioTime.TimeToRationalTime(gap.Duration, rate);
                writer.WriteNumber("value", durVal);
                writer.WriteNumber("rate", durRate);
                writer.WriteEndObject();
                writer.WriteEndObject(); // source_range
                writer.WriteStartArray("effects");
                writer.WriteEndArray();
                writer.WriteStartArray("markers");
                writer.WriteEndArray();
                writer.WriteStartObject("metadata");
                writer.WriteEndObject();
                writer.WriteEndObject(); // Gap.1
            }
            else if (item is WalkClip walkClip)
            {
                if (walkClip.In is { } transition)
                {
                    WriteTransition(writer, transition, rate);
                }

                if (walkClip.Compound is { } inner)
                {
                    WriteCompoundStack(writer, project, walkClip, inner, rate, projectDirectory, options, clipJsonElements);
                }
                else
                {
                    WriteClip(writer, walkClip, rate, projectDirectory, clipJsonElements);
                }
            }
        }

        writer.WriteEndArray(); // children
        writer.WriteEndObject(); // Track.1
    }

    private static void WriteTransition(Utf8JsonWriter writer, TransitionInstance transition, Rational rate)
    {
        writer.WriteStartObject();
        writer.WriteString("OTIO_SCHEMA", "Transition.1");
        bool isDissolve = transition.Type == BuiltInTransitions.Crossfade;
        writer.WriteString("name", isDissolve ? "SMPTE_Dissolve" : "Custom_Transition");
        writer.WriteString("transition_type", isDissolve ? "SMPTE_Dissolve" : "Custom_Transition");

        writer.WriteStartObject("in_offset");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double inVal, double inRate) = OtioTime.TimeToRationalTime(transition.Before, rate);
        writer.WriteNumber("value", inVal);
        writer.WriteNumber("rate", inRate);
        writer.WriteEndObject();

        writer.WriteStartObject("out_offset");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double outVal, double outRate) = OtioTime.TimeToRationalTime(transition.After, rate);
        writer.WriteNumber("value", outVal);
        writer.WriteNumber("rate", outRate);
        writer.WriteEndObject();

        writer.WriteStartObject("metadata");
        if (!isDissolve || transition.Curve is not null || transition.Parameters.Count > 0)
        {
            writer.WriteStartObject("rodaje");
            writer.WriteString("type", transition.Type);
            if (transition.Curve is not null)
            {
                writer.WriteString("curve", transition.Curve.Value.ToString());
            }

            foreach ((string key, double paramVal) in transition.Parameters)
            {
                writer.WriteNumber(key, paramVal);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject(); // metadata
        writer.WriteEndObject(); // Transition.1
    }

    private static void WriteCompoundStack(
        Utf8JsonWriter writer, Project project, WalkClip walkClip, Sequence inner, Rational rate, string? projectDirectory, InterchangeOptions options, Dictionary<string, JsonElement> clipJsonElements)
    {
        writer.WriteStartObject();
        writer.WriteString("OTIO_SCHEMA", "Stack.1");
        writer.WriteString("name", inner.Name);

        writer.WriteStartObject("source_range");
        writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
        writer.WriteStartObject("start_time");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double stVal, double stRate) = OtioTime.TimeToRationalTime(walkClip.Clip.SourceIn, rate);
        writer.WriteNumber("value", stVal);
        writer.WriteNumber("rate", stRate);
        writer.WriteEndObject();
        writer.WriteStartObject("duration");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double durVal, double durRate) = OtioTime.TimeToRationalTime(walkClip.Duration, rate);
        writer.WriteNumber("value", durVal);
        writer.WriteNumber("rate", durRate);
        writer.WriteEndObject();
        writer.WriteEndObject(); // source_range

        writer.WriteStartObject("metadata");
        writer.WriteEndObject();
        writer.WriteStartArray("markers");
        writer.WriteEndArray();

        writer.WriteStartArray("children");
        IReadOnlyList<WalkTrack> innerWalkTracks = TimelineWalk.Of(project, inner, flattenCompounds: false, options.Limits, new InterchangeReport());
        foreach (WalkTrack innerWalkTrack in innerWalkTracks)
        {
            WriteTrack(writer, project, innerWalkTrack, inner.FrameRate, projectDirectory, options, clipJsonElements);
        }

        writer.WriteEndArray(); // children
        writer.WriteEndObject(); // Stack.1
    }

    private static void WriteClip(
        Utf8JsonWriter writer, WalkClip walkClip, Rational rate, string? projectDirectory, Dictionary<string, JsonElement> clipJsonElements)
    {
        Clip clip = walkClip.Clip;
        writer.WriteStartObject();
        writer.WriteString("OTIO_SCHEMA", "Clip.2");
        string clipName = walkClip.Media?.Name ?? (clip.Generator is not null ? "Título" : "Clip");
        writer.WriteString("name", clipName);

        writer.WriteStartObject("source_range");
        writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
        writer.WriteStartObject("start_time");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double stVal, double stRate) = OtioTime.TimeToRationalTime(clip.SourceIn, rate);
        writer.WriteNumber("value", stVal);
        writer.WriteNumber("rate", stRate);
        writer.WriteEndObject();
        writer.WriteStartObject("duration");
        writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
        (double durVal, double durRate) = OtioTime.TimeToRationalTime(walkClip.Duration, rate);
        writer.WriteNumber("value", durVal);
        writer.WriteNumber("rate", durRate);
        writer.WriteEndObject();
        writer.WriteEndObject(); // source_range

        writer.WriteString("active_media_reference_key", "DEFAULT_MEDIA");
        writer.WriteStartObject("media_references");
        writer.WriteStartObject("DEFAULT_MEDIA");

        if (clip.Generator is not null)
        {
            writer.WriteString("OTIO_SCHEMA", "GeneratorReference.1");
            writer.WriteString("name", clipName);
            writer.WriteString("generator_kind", "SMPTEBars");
            writer.WriteStartObject("parameters");
            writer.WriteString("text", clip.Generator.Text);
            writer.WriteEndObject();
            writer.WriteStartObject("metadata");
            writer.WriteEndObject();
        }
        else if (walkClip.Media is null || string.IsNullOrEmpty(walkClip.Media.Path))
        {
            writer.WriteString("OTIO_SCHEMA", "MissingReference.1");
            writer.WriteString("name", walkClip.Media?.Name ?? "Missing");
            writer.WriteStartObject("metadata");
            writer.WriteEndObject();
        }
        else
        {
            MediaRef media = walkClip.Media;
            writer.WriteString("OTIO_SCHEMA", "ExternalReference.1");
            writer.WriteString("name", media.Name);
            string resolved = projectDirectory is null || media.Path.Length == 0 ? media.Path : media.ResolvePath(projectDirectory);
            writer.WriteString("target_url", SequenceBuilder.ToFileUri(resolved));

            writer.WriteStartObject("available_range");
            writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
            writer.WriteStartObject("start_time");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            Rational mediaRate = media.FrameRate ?? rate;
            long mediaStartFrame = 0;
            if (media.Timecode is not null && SmpteTimecode.TryParse(media.Timecode, mediaRate, out SmpteTimecode mTc))
            {
                mediaStartFrame = mTc.Frame;
            }

            writer.WriteNumber("value", mediaStartFrame);
            writer.WriteNumber("rate", mediaRate.ToDouble());
            writer.WriteEndObject();

            writer.WriteStartObject("duration");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            (double mDurVal, double mDurRate) = OtioTime.TimeToRationalTime(media.Duration, mediaRate);
            writer.WriteNumber("value", mDurVal);
            writer.WriteNumber("rate", mDurRate);
            writer.WriteEndObject();
            writer.WriteEndObject(); // available_range

            writer.WriteStartObject("metadata");
            writer.WriteEndObject();
        }

        writer.WriteEndObject(); // DEFAULT_MEDIA
        writer.WriteEndObject(); // media_references

        writer.WriteStartArray("effects");
        if (clip.Speed is not null)
        {
            if (clip.Speed.Freeze)
            {
                writer.WriteStartObject();
                writer.WriteString("OTIO_SCHEMA", "FreezeFrame.1");
                writer.WriteString("name", "FreezeFrame");
                writer.WriteStartObject("metadata");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("OTIO_SCHEMA", "LinearTimeWarp.1");
                writer.WriteString("name", "LinearTimeWarp");
                double scalar;
                if (clip.Speed.Curve is not null)
                {
                    scalar = (double)(clip.SourceOut.Ticks - clip.SourceIn.Ticks) / clip.Duration.Ticks;
                }
                else
                {
                    scalar = (double)clip.Speed.Rate.Num / clip.Speed.Rate.Den;
                    if (clip.Speed.Reverse)
                    {
                        scalar = -scalar;
                    }
                }

                writer.WriteNumber("time_scalar", scalar);
                writer.WriteStartObject("metadata");
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray(); // effects

        writer.WriteStartArray("markers");
        writer.WriteEndArray();

        writer.WriteStartObject("metadata");
        writer.WriteStartObject("rodaje");
        string clipHuella = OtioFingerprint.ClipFingerprint(clip, walkClip.Media);
        writer.WriteString("huella", clipHuella);
        if (clipJsonElements.TryGetValue(clip.Id, out JsonElement clipElem))
        {
            writer.WritePropertyName("clip");
            clipElem.WriteTo(writer);
        }

        writer.WriteEndObject(); // rodaje
        writer.WriteEndObject(); // metadata

        writer.WriteEndObject(); // Clip.2
    }

    private static void WriteMarkersAndRegions(Utf8JsonWriter writer, Sequence sequence, Rational rate)
    {
        foreach (Marker marker in sequence.Markers)
        {
            writer.WriteStartObject();
            writer.WriteString("OTIO_SCHEMA", "Marker.2");
            writer.WriteString("name", marker.Name);
            writer.WriteStartObject("marked_range");
            writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
            writer.WriteStartObject("start_time");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            (double mVal, double mRate) = OtioTime.TimeToRationalTime(marker.Time, rate);
            writer.WriteNumber("value", mVal);
            writer.WriteNumber("rate", mRate);
            writer.WriteEndObject();
            writer.WriteStartObject("duration");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            writer.WriteNumber("value", 0);
            writer.WriteNumber("rate", rate.ToDouble());
            writer.WriteEndObject();
            writer.WriteEndObject(); // marked_range
            writer.WriteString("color", OtioColor.ToOtioColor(marker.Color));
            writer.WriteStartObject("metadata");
            if (!string.IsNullOrEmpty(marker.Color))
            {
                writer.WriteStartObject("rodaje");
                writer.WriteString("color", marker.Color);
                writer.WriteEndObject();
            }

            writer.WriteEndObject(); // metadata
            writer.WriteEndObject(); // Marker.2
        }

        foreach (Region region in sequence.Regions)
        {
            writer.WriteStartObject();
            writer.WriteString("OTIO_SCHEMA", "Marker.2");
            writer.WriteString("name", region.Name);
            writer.WriteStartObject("marked_range");
            writer.WriteString("OTIO_SCHEMA", "TimeRange.1");
            writer.WriteStartObject("start_time");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            (double rVal, double rRate) = OtioTime.TimeToRationalTime(region.Start, rate);
            writer.WriteNumber("value", rVal);
            writer.WriteNumber("rate", rRate);
            writer.WriteEndObject();
            writer.WriteStartObject("duration");
            writer.WriteString("OTIO_SCHEMA", "RationalTime.1");
            Time rDur = region.End - region.Start;
            (double rDurVal, double rDurRate) = OtioTime.TimeToRationalTime(rDur, rate);
            writer.WriteNumber("value", rDurVal);
            writer.WriteNumber("rate", rDurRate);
            writer.WriteEndObject();
            writer.WriteEndObject(); // marked_range
            writer.WriteString("color", OtioColor.ToOtioColor(region.Color));
            writer.WriteStartObject("metadata");
            if (!string.IsNullOrEmpty(region.Color))
            {
                writer.WriteStartObject("rodaje");
                writer.WriteString("color", region.Color);
                writer.WriteEndObject();
            }

            writer.WriteEndObject(); // metadata
            writer.WriteEndObject(); // Marker.2
        }
    }
}
