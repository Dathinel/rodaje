using System.Text;
using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>
/// Lee OpenTimelineIO (.otio). Un elemento que no entiende se convierte en hueco o en clip sin medio y va al informe;
/// un archivo roto o que pasa de <see cref="InterchangeLimits"/> lanza <see cref="InterchangeException"/> (D-062).
/// </summary>
public static class OtioReader
{
    private sealed record PendingTransition(JsonElement Element, string Type, Time InOffset, Time OutOffset);

    public static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report) =>
        Read(text, baseDirectory, options, report, standardOnly: false);

    /// <summary>Con <paramref name="standardOnly"/>, lo estándar tal cual, sin mirar <c>metadata.rodaje</c>: lo que firma la huella.</summary>
    internal static TimelineDraft Read(string text, string? baseDirectory, InterchangeOptions options, InterchangeReport report, bool standardOnly)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);

        if (text.Length > options.Limits.MaxFileBytes)
        {
            throw new InterchangeException($"El texto pasa del límite de {options.Limits.MaxFileBytes >> 20} MB.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = options.Limits.MaxDepth * 4 });
        }
        catch (JsonException ex)
        {
            throw new InterchangeException("El archivo JSON pasa del nivel máximo de anidado o está mal formado.", ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InterchangeException("La raíz del documento OTIO debe ser un objeto JSON.");
            }

            string schema = GetSchema(root);
            if (schema != "Timeline.1")
            {
                throw new InterchangeException($"Esquema OTIO raíz no reconocido: «{schema}». Se esperaba «Timeline.1».");
            }

            int objectCount = 1;
            string timelineName = root.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() ?? "Timeline" : "Timeline";

            Rational rate = DetermineRate(root, report);
            string? startTimecode = ParseStartTimecode(root, rate, report);

            var project = new Project { Id = "p", Name = timelineName };
            var sequence = new Sequence
            {
                Id = "principal",
                Name = timelineName,
                FrameRate = rate,
                StartTimecode = startTimecode,
            };
            project.Sequences.Add(sequence);

            var mediaByPath = new Dictionary<string, MediaRef>(StringComparer.OrdinalIgnoreCase);
            var clipElements = new Dictionary<Clip, JsonElement>();
            int clipCounter = 0;
            int compoundCounter = 0;
            int trackCounter = 0;

            if (root.TryGetProperty("tracks", out JsonElement tracksElem) && tracksElem.ValueKind == JsonValueKind.Object)
            {
                SpendObject(ref objectCount, options.Limits);
                string tracksSchema = GetSchema(tracksElem);
                if (tracksSchema == "Stack.1")
                {
                    ReadMarkersFromElement(tracksElem, sequence, rate, Time.Zero);

                    if (tracksElem.TryGetProperty("children", out JsonElement childrenElem) && childrenElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement trackElem in childrenElem.EnumerateArray())
                        {
                            SpendObject(ref objectCount, options.Limits);
                            string childSchema = GetSchema(trackElem);
                            if (childSchema != "Track.1")
                            {
                                report.Add(IssueSeverity.Approximate, IssueKind.Unknown, $"Elemento «{childSchema}» no es una pista y se ignora.");
                                continue;
                            }

                            string trackName = trackElem.TryGetProperty("name", out JsonElement tn) ? tn.GetString() ?? "Pista" : "Pista";
                            string kindStr = trackElem.TryGetProperty("kind", out JsonElement tk) ? tk.GetString() ?? "Video" : "Video";
                            TrackKind kind = string.Equals(kindStr, "Audio", StringComparison.OrdinalIgnoreCase) ? TrackKind.Audio : TrackKind.Video;
                            var track = new Track
                            {
                                Id = FormattableString.Invariant($"t{trackCounter++}"),
                                Kind = kind,
                                Name = trackName,
                            };

                            ParseTrackChildren(
                                trackElem, track, sequence, project, mediaByPath, clipElements, baseDirectory, rate, options, report,
                                ref objectCount, ref clipCounter, ref compoundCounter, depth: 1);

                            if (kind == TrackKind.Video)
                            {
                                sequence.VideoTracks.Add(track);
                            }
                            else
                            {
                                sequence.AudioTracks.Add(track);
                            }
                        }
                    }
                }
            }

            ReadMarkersFromElement(root, sequence, rate, Time.Zero);
            sequence.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
            sequence.Regions.Sort((a, b) => a.Start.CompareTo(b.Start));

            var standardDraft = new TimelineDraft(project, sequence);
            if (standardOnly)
            {
                return standardDraft;
            }

            // Revisar metadata.rodaje en Timeline.1
            if (root.TryGetProperty("metadata", out JsonElement metaElem) && metaElem.ValueKind == JsonValueKind.Object)
            {
                if (metaElem.TryGetProperty("rodaje", out JsonElement rodajeElem) && rodajeElem.ValueKind == JsonValueKind.Object)
                {
                    if (rodajeElem.TryGetProperty("v", out JsonElement vElem) && vElem.GetInt32() == 1)
                    {
                        string metaHuella = rodajeElem.TryGetProperty("huella", out JsonElement hElem) ? hElem.GetString() ?? "" : "";
                        string calcHuella = OtioFingerprint.TimelineFingerprint(standardDraft);

                        if (!string.IsNullOrEmpty(metaHuella) && string.Equals(metaHuella, calcHuella, StringComparison.OrdinalIgnoreCase))
                        {
                            // La huella firma solo lo estándar: un bloque roto o incoherente no tumba la importación.
                            try
                            {
                                return RestoreFullProject(rodajeElem);
                            }
                            catch (Exception ex) when (ex is ProjectFormatException or KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                            {
                                report.Add(IssueSeverity.Lost, IssueKind.Unknown, "El bloque de Rodaje del archivo está dañado: se importa solo lo estándar de OTIO.");
                                return standardDraft;
                            }
                        }

                        RestoreUnchangedClips(standardDraft, clipElements, report);
                    }
                }

                foreach (JsonProperty prop in metaElem.EnumerateObject())
                {
                    if (prop.Name != "rodaje")
                    {
                        report.Add(IssueSeverity.Info, IssueKind.Unknown, "Se descarta la metadata de otros programas.");
                        break;
                    }
                }
            }

            return standardDraft;
        }
    }

    private static void ParseTrackChildren(
        JsonElement trackElem, Track track, Sequence currentSequence, Project project, Dictionary<string, MediaRef> mediaByPath,
        Dictionary<Clip, JsonElement> clipElements, string? baseDirectory, Rational rate, InterchangeOptions options, InterchangeReport report,
        ref int objectCount, ref int clipCounter, ref int compoundCounter, int depth)
    {
        if (depth > options.Limits.MaxDepth)
        {
            throw new InterchangeException($"Nivel de anidado de elementos supera el límite de {options.Limits.MaxDepth}.");
        }

        if (!trackElem.TryGetProperty("children", out JsonElement childrenElem) || childrenElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        Time cursor = Time.Zero;
        Clip? lastClip = null;
        PendingTransition? pendingTransition = null;

        foreach (JsonElement itemElem in childrenElem.EnumerateArray())
        {
            SpendObject(ref objectCount, options.Limits);
            string itemSchema = GetSchema(itemElem);

            if (itemSchema == "Gap.1")
            {
                GetStartTime(itemElem, rate, report);
                Time gapDur = GetDuration(itemElem, rate, report);
                cursor += gapDur;
                lastClip = null;
                pendingTransition = null;
            }
            else if (itemSchema == "Transition.1")
            {
                string transType = itemElem.TryGetProperty("transition_type", out JsonElement tt) ? tt.GetString() ?? "SMPTE_Dissolve" : "SMPTE_Dissolve";
                Time inOff = GetRationalTime(itemElem, "in_offset", rate, report);
                Time outOff = GetRationalTime(itemElem, "out_offset", rate, report);
                pendingTransition = new PendingTransition(itemElem, transType, inOff, outOff);
            }
            else if (itemSchema == "Stack.1")
            {
                Time inTime = GetStartTime(itemElem, rate, report);
                Time durTime = GetDuration(itemElem, rate, report);
                string stackName = itemElem.TryGetProperty("name", out JsonElement sn) ? sn.GetString() ?? "Compuesto" : "Compuesto";

                // Solo la marca de Rodaje dice que dos pilas son el mismo compuesto: el nombre no basta.
                string? sharedId = itemElem.TryGetProperty("metadata", out JsonElement stackMeta) && stackMeta.ValueKind == JsonValueKind.Object
                    && stackMeta.TryGetProperty("rodaje", out JsonElement stackRodaje) && stackRodaje.ValueKind == JsonValueKind.Object
                    && stackRodaje.TryGetProperty("secuencia", out JsonElement sharedElem) && sharedElem.ValueKind == JsonValueKind.String
                    ? "compuesto_r_" + sharedElem.GetString() : null;
                Sequence? existingInner = sharedId is null ? null : project.FindSequence(sharedId);

                Sequence innerSeq;
                if (existingInner is not null)
                {
                    innerSeq = existingInner;
                }
                else
                {
                    innerSeq = new Sequence
                    {
                        Id = sharedId ?? FormattableString.Invariant($"compuesto_{compoundCounter++}"),
                        Name = stackName,
                        FrameRate = rate,
                    };
                    project.Sequences.Add(innerSeq);

                    if (itemElem.TryGetProperty("children", out JsonElement innerTracksElem) && innerTracksElem.ValueKind == JsonValueKind.Array)
                    {
                        int innerTrackIndex = 0;
                        foreach (JsonElement innerTrackElem in innerTracksElem.EnumerateArray())
                        {
                            SpendObject(ref objectCount, options.Limits);
                            string innerTrackSchema = GetSchema(innerTrackElem);
                            if (innerTrackSchema == "Track.1")
                            {
                                string innerTrackName = innerTrackElem.TryGetProperty("name", out JsonElement itn) ? itn.GetString() ?? "Pista" : "Pista";
                                string innerKindStr = innerTrackElem.TryGetProperty("kind", out JsonElement itk) ? itk.GetString() ?? "Video" : "Video";
                                TrackKind innerKind = string.Equals(innerKindStr, "Audio", StringComparison.OrdinalIgnoreCase) ? TrackKind.Audio : TrackKind.Video;
                                var innerTrack = new Track
                                {
                                    Id = FormattableString.Invariant($"it{innerTrackIndex++}"),
                                    Kind = innerKind,
                                    Name = innerTrackName,
                                };

                                ParseTrackChildren(
                                    innerTrackElem, innerTrack, innerSeq, project, mediaByPath, clipElements, baseDirectory, rate, options, report,
                                    ref objectCount, ref clipCounter, ref compoundCounter, depth + 1);

                                if (innerKind == TrackKind.Video)
                                {
                                    innerSeq.VideoTracks.Add(innerTrack);
                                }
                                else
                                {
                                    innerSeq.AudioTracks.Add(innerTrack);
                                }
                            }
                        }
                    }
                }

                var compClip = new Clip
                {
                    Id = FormattableString.Invariant($"c{clipCounter++}"),
                    SequenceId = innerSeq.Id,
                    Start = cursor,
                    Duration = durTime,
                    SourceIn = inTime,
                    SourceOut = inTime + durTime,
                    Enabled = IsEnabled(itemElem),
                };
                track.Clips.Add(compClip);
                clipElements[compClip] = itemElem;

                if (pendingTransition is not null && lastClip is not null)
                {
                    ApplyTransition(track, lastClip, compClip, pendingTransition);
                }

                pendingTransition = null;
                lastClip = compClip;
                cursor += durTime;
            }
            else if (itemSchema is "Clip.1" or "Clip.2")
            {
                Time inTime = GetStartTime(itemElem, rate, report);
                Time durTime = GetDuration(itemElem, rate, report);

                var clip = new Clip
                {
                    Id = FormattableString.Invariant($"c{clipCounter++}"),
                    Start = cursor,
                    Duration = durTime,
                    SourceIn = inTime,
                    Enabled = IsEnabled(itemElem),
                };

                ParseMediaReference(itemElem, clip, project, mediaByPath, baseDirectory, track.Kind, rate, report);
                ParseEffects(itemElem, clip);

                if (clip.Speed?.Freeze == true)
                {
                    clip.SourceOut = clip.SourceIn + Time.FromTicks(1);
                }
                else if (clip.Speed is { } sp)
                {
                    clip.SourceOut = clip.SourceIn + Time.FromTicks(checked((long)Math.Round((double)durTime.Ticks * sp.Rate.Num / sp.Rate.Den)));
                }
                else
                {
                    clip.SourceOut = clip.SourceIn + durTime;
                }

                ReadMarkersFromElement(itemElem, currentSequence, rate, cursor - inTime);

                track.Clips.Add(clip);
                clipElements[clip] = itemElem;

                if (pendingTransition is not null && lastClip is not null)
                {
                    ApplyTransition(track, lastClip, clip, pendingTransition);
                }

                pendingTransition = null;
                lastClip = clip;
                cursor += durTime;
            }
            else
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Unknown, $"Elemento desconocido «{itemSchema}» convertido en hueco.");
                GetStartTime(itemElem, rate, report);
                Time unkDur = GetDuration(itemElem, rate, report);
                cursor += unkDur;
                lastClip = null;
                pendingTransition = null;
            }
        }
    }

    private static void ParseMediaReference(
        JsonElement clipElem, Clip clip, Project project, Dictionary<string, MediaRef> mediaByPath, string? baseDirectory, TrackKind kind, Rational rate, InterchangeReport report)
    {
        JsonElement refElem = default;
        bool hasRef = false;

        if (clipElem.TryGetProperty("media_references", out JsonElement refsElem) && refsElem.ValueKind == JsonValueKind.Object)
        {
            string activeKey = clipElem.TryGetProperty("active_media_reference_key", out JsonElement ak) ? ak.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(activeKey) && refsElem.TryGetProperty(activeKey, out JsonElement activeRef))
            {
                refElem = activeRef;
                hasRef = true;
            }
            else
            {
                foreach (JsonProperty p in refsElem.EnumerateObject())
                {
                    refElem = p.Value;
                    hasRef = true;
                    break;
                }
            }
        }
        else if (clipElem.TryGetProperty("media_reference", out JsonElement directRef) && directRef.ValueKind == JsonValueKind.Object)
        {
            refElem = directRef;
            hasRef = true;
        }

        if (!hasRef)
        {
            clip.MediaId = GetOrCreateMedia(project, mediaByPath, "", "Missing", null, kind, rate);
            return;
        }

        string refSchema = GetSchema(refElem);
        string refName = refElem.TryGetProperty("name", out JsonElement rn) ? rn.GetString() ?? "" : "";

        if (refSchema == "GeneratorReference.1")
        {
            string genKind = refElem.TryGetProperty("generator_kind", out JsonElement gk) ? gk.GetString() ?? "Title" : "Title";
            string text = "";
            if (refElem.TryGetProperty("parameters", out JsonElement paramsElem) && paramsElem.ValueKind == JsonValueKind.Object)
            {
                if (paramsElem.TryGetProperty("text", out JsonElement tElem))
                {
                    text = tElem.GetString() ?? "";
                }
            }

            clip.Generator = new ClipGenerator { Text = text, Style = new TextStyle() };
            clip.MediaId = "";
            report.Add(IssueSeverity.Approximate, IssueKind.Title, $"Generador «{genKind}» importado como título.");
        }
        else if (refSchema == "MissingReference.1")
        {
            clip.MediaId = GetOrCreateMedia(project, mediaByPath, "", string.IsNullOrEmpty(refName) ? "Missing" : refName, null, kind, rate);
        }
        else if (refSchema == "ExternalReference.1")
        {
            string targetUrl = refElem.TryGetProperty("target_url", out JsonElement tu) ? tu.GetString() ?? "" : "";
            string? normPath = SequenceBuilder.NormalizePath(targetUrl, baseDirectory);
            if (normPath is null || normPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                report.Add(IssueSeverity.Lost, IssueKind.Media, $"Ruta de red o no admitida convertida en medio sin archivo: «{targetUrl}».");
                normPath = "";
            }

            JsonElement? availRange = refElem.TryGetProperty("available_range", out JsonElement ar) && ar.ValueKind == JsonValueKind.Object ? ar : null;
            clip.MediaId = GetOrCreateMedia(project, mediaByPath, normPath, refName, availRange, kind, rate);

            // El rango del clip va en el tiempo del medio, que empieza en su TC; en Rodaje, desde el principio del archivo.
            if (availRange is { } range)
            {
                clip.SourceIn -= GetRationalTime(range, "start_time", rate, report);
            }
        }
        else
        {
            report.Add(IssueSeverity.Approximate, IssueKind.Unknown, $"Referencia de medio desconocida «{refSchema}».");
            clip.MediaId = GetOrCreateMedia(project, mediaByPath, "", "Desconocido", null, kind, rate);
        }
    }

    private static void ParseEffects(JsonElement clipElem, Clip clip)
    {
        if (!clipElem.TryGetProperty("effects", out JsonElement effectsElem) || effectsElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement eff in effectsElem.EnumerateArray())
        {
            string effSchema = GetSchema(eff);
            if (effSchema == "FreezeFrame.1")
            {
                clip.Speed = new ClipSpeed { Freeze = true };
                return;
            }

            if (effSchema == "LinearTimeWarp.1")
            {
                double scalar = eff.TryGetProperty("time_scalar", out JsonElement ts) ? ts.GetDouble() : 1.0;
                bool reverse = scalar < 0;
                Rational spRate = OtioTime.RationalFromDouble(Math.Abs(scalar));
                clip.Speed = new ClipSpeed { Rate = spRate, Reverse = reverse };
                return;
            }
        }
    }

    private static void ApplyTransition(Track track, Clip fromClip, Clip toClip, PendingTransition pending)
    {
        Time totalDuration = pending.InOffset + pending.OutOffset;
        if (totalDuration <= Time.Zero)
        {
            return;
        }

        TransitionAlignment alignment;
        if (pending.InOffset == Time.Zero)
        {
            alignment = TransitionAlignment.After;
        }
        else if (pending.OutOffset == Time.Zero)
        {
            alignment = TransitionAlignment.Before;
        }
        else
        {
            alignment = TransitionAlignment.Center;
        }

        string type = BuiltInTransitions.Crossfade;
        if (pending.Element.TryGetProperty("metadata", out JsonElement meta) &&
            meta.TryGetProperty("rodaje", out JsonElement rodaje) &&
            rodaje.TryGetProperty("type", out JsonElement typeElem) &&
            typeElem.GetString() is { } metaType)
        {
            type = metaType;
        }

        track.Transitions.Add(new TransitionInstance
        {
            Id = FormattableString.Invariant($"t_{fromClip.Id}_{toClip.Id}"),
            Type = type,
            From = fromClip.Id,
            To = toClip.Id,
            Duration = totalDuration,
            Alignment = alignment,
        });
    }

    private static string GetOrCreateMedia(
        Project project, Dictionary<string, MediaRef> mediaByPath, string path, string name, JsonElement? availableRange, TrackKind trackKind, Rational defaultRate)
    {
        string key = path.Length > 0 ? path : FormattableString.Invariant($"empty:{name}_{project.Media.Count}");
        if (mediaByPath.TryGetValue(key, out MediaRef? existing))
        {
            if (trackKind == TrackKind.Video)
            {
                existing.HasVideo = true;
            }

            if (trackKind == TrackKind.Audio)
            {
                existing.HasAudio = true;
            }

            return existing.Id;
        }

        Time duration = Time.Zero;
        string? timecode = null;
        Rational mediaRate = defaultRate;

        if (availableRange is { } ar && ar.ValueKind == JsonValueKind.Object)
        {
            if (ar.TryGetProperty("duration", out JsonElement durElem) && durElem.ValueKind == JsonValueKind.Object)
            {
                double dVal = durElem.GetProperty("value").GetDouble();
                double dRate = durElem.GetProperty("rate").GetDouble();
                mediaRate = OtioTime.NormalizeRate(dRate, null);
                duration = OtioTime.RationalTimeToTime(dVal, dRate, null);
            }

            if (ar.TryGetProperty("start_time", out JsonElement stElem) && stElem.ValueKind == JsonValueKind.Object)
            {
                double stVal = stElem.GetProperty("value").GetDouble();
                double stRate = stElem.GetProperty("rate").GetDouble();
                Rational stNormRate = OtioTime.NormalizeRate(stRate, null);
                long frame = (long)Math.Round(stVal);
                if (frame > 0)
                {
                    timecode = SmpteTimecode.FromFrame(frame, stNormRate, SmpteTimecode.CanDropFrame(stNormRate)).Format();
                }
            }
        }

        string mediaName = !string.IsNullOrEmpty(name)
            ? name
            : path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : "Medio";

        var media = new MediaRef
        {
            Id = FormattableString.Invariant($"m{project.Media.Count}"),
            Name = mediaName,
            Path = path,
            Duration = duration,
            Timecode = timecode,
            FrameRate = mediaRate,
            HasVideo = trackKind == TrackKind.Video,
            HasAudio = trackKind == TrackKind.Audio,
            Width = 0,
            Height = 0,
            SampleRate = 0,
            AudioChannels = 0,
        };
        project.Media.Add(media);
        mediaByPath[key] = media;
        return media.Id;
    }

    private static void ReadMarkersFromElement(JsonElement elem, Sequence sequence, Rational rate, Time offset)
    {
        if (!elem.TryGetProperty("markers", out JsonElement markersElem) || markersElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement m in markersElem.EnumerateArray())
        {
            string schema = GetSchema(m);
            if (!schema.StartsWith("Marker.", StringComparison.Ordinal))
            {
                continue;
            }

            string name = m.TryGetProperty("name", out JsonElement np) ? np.GetString() ?? "" : "";
            Time markedStart = GetStartTime(m, rate, null, "marked_range");
            Time markedDur = GetDuration(m, rate, null, "marked_range");

            string? colorHex = null;
            if (m.TryGetProperty("metadata", out JsonElement meta) &&
                meta.TryGetProperty("rodaje", out JsonElement rodaje) &&
                rodaje.TryGetProperty("color", out JsonElement cElem))
            {
                colorHex = cElem.GetString();
            }

            if (string.IsNullOrEmpty(colorHex))
            {
                string otioColor = m.TryGetProperty("color", out JsonElement cp) ? cp.GetString() ?? "GREEN" : "GREEN";
                colorHex = OtioColor.FromOtioColor(otioColor);
            }

            Time targetTime = offset + markedStart;
            if (markedDur > Time.Zero)
            {
                sequence.Regions.Add(new Region
                {
                    Id = FormattableString.Invariant($"reg_{sequence.Regions.Count}"),
                    Name = name,
                    Start = targetTime,
                    End = targetTime + markedDur,
                    Color = colorHex,
                });
            }
            else
            {
                sequence.Markers.Add(new Marker
                {
                    Id = FormattableString.Invariant($"mk_{sequence.Markers.Count}"),
                    Name = name,
                    Time = targetTime,
                    Color = colorHex,
                });
            }
        }
    }

    private static TimelineDraft RestoreFullProject(JsonElement rodajeElem)
    {
        JsonElement seqJson = rodajeElem.GetProperty("sequence");
        JsonElement mediaJson = rodajeElem.GetProperty("media");
        JsonElement seqsJson = rodajeElem.GetProperty("sequences");

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 16);
            w.WriteString("id", "proyecto");
            w.WritePropertyName("media");
            mediaJson.WriteTo(w);
            w.WriteStartArray("sequences");
            seqJson.WriteTo(w);
            foreach (JsonElement innerSeqElem in seqsJson.EnumerateArray())
            {
                innerSeqElem.WriteTo(w);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        string fullJson = Encoding.UTF8.GetString(ms.ToArray());
        Project restored = ProjectSerializer.Deserialize(fullJson);
        Sequence mainRestored = restored.Sequences[0];
        return new TimelineDraft(restored, mainRestored);
    }

    private static void RestoreUnchangedClips(TimelineDraft standardDraft, Dictionary<Clip, JsonElement> clipElements, InterchangeReport report)
    {
        foreach (Track t in standardDraft.Sequence.AllTracks)
        {
            foreach (Clip c in t.Clips)
            {
                if (clipElements.TryGetValue(c, out JsonElement cElem))
                {
                    if (cElem.TryGetProperty("metadata", out JsonElement cMeta) &&
                        cMeta.TryGetProperty("rodaje", out JsonElement cRodaje) &&
                        cRodaje.TryGetProperty("huella", out JsonElement chElem) &&
                        cRodaje.TryGetProperty("clip", out JsonElement cClipElem))
                    {
                        string cMetaHuella = chElem.GetString() ?? "";
                        MediaRef? mRef = standardDraft.Project.FindMedia(c.MediaId);
                        string cCalcHuella = OtioFingerprint.ClipFingerprint(c, mRef);

                        if (!string.IsNullOrEmpty(cMetaHuella) && string.Equals(cMetaHuella, cCalcHuella, StringComparison.OrdinalIgnoreCase))
                        {
                            Clip restoredClip;
                            try
                            {
                                restoredClip = DeserializeClip(cClipElem);
                            }
                            catch (Exception ex) when (ex is ProjectFormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                            {
                                report.Add(IssueSeverity.Lost, IssueKind.Effect, "Los ajustes de Rodaje de este clip están dañados: se descartan.", mRef?.Name ?? c.Id, c.Id, c.Start);
                                continue;
                            }

                            c.Effects = restoredClip.Effects;
                            c.Color = restoredClip.Color;
                            c.LinkId = restoredClip.LinkId;
                            c.GroupId = restoredClip.GroupId;
                            c.Enabled = restoredClip.Enabled;
                        }
                        else
                        {
                            string clipDisplayName = mRef?.Name ?? c.Id;
                            report.Add(IssueSeverity.Info, IssueKind.Effect, "Rodaje descartó sus ajustes porque el clip cambió fuera.", clipDisplayName, c.Id, c.Start);
                        }
                    }
                }
            }
        }
    }

    private static Clip DeserializeClip(JsonElement clipElem)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 16);
            w.WriteString("id", "dummy");
            w.WriteStartArray("sequences");
            w.WriteStartObject();
            w.WriteString("id", "s");
            w.WriteString("name", "s");
            w.WriteStartObject("frameRate");
            w.WriteNumber("num", 25);
            w.WriteNumber("den", 1);
            w.WriteEndObject();
            w.WriteStartArray("videoTracks");
            w.WriteStartObject();
            w.WriteString("id", "t");
            w.WriteString("kind", "video");
            w.WriteString("name", "V");
            w.WriteStartArray("clips");
            clipElem.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        string json = Encoding.UTF8.GetString(ms.ToArray());
        Project p = ProjectSerializer.Deserialize(json, _ => Array.Empty<string>());
        return p.Sequences[0].VideoTracks[0].Clips[0];
    }

    private static Rational DetermineRate(JsonElement root, InterchangeReport report)
    {
        if (root.TryGetProperty("global_start_time", out JsonElement gst) && gst.ValueKind == JsonValueKind.Object)
        {
            if (gst.TryGetProperty("rate", out JsonElement rElem))
            {
                double rVal = rElem.GetDouble();
                return OtioTime.NormalizeRate(rVal, report);
            }
        }

        if (root.TryGetProperty("tracks", out JsonElement tracksElem) && tracksElem.ValueKind == JsonValueKind.Object)
        {
            if (tracksElem.TryGetProperty("children", out JsonElement children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement track in children.EnumerateArray())
                {
                    if (track.TryGetProperty("children", out JsonElement trackItems) && trackItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in trackItems.EnumerateArray())
                        {
                            if (item.TryGetProperty("source_range", out JsonElement sr) && sr.ValueKind == JsonValueKind.Object)
                            {
                                if (sr.TryGetProperty("duration", out JsonElement dur) && dur.TryGetProperty("rate", out JsonElement durRate))
                                {
                                    return OtioTime.NormalizeRate(durRate.GetDouble(), report);
                                }
                            }
                        }
                    }
                }
            }
        }

        return new Rational(24, 1);
    }

    private static string? ParseStartTimecode(JsonElement root, Rational rate, InterchangeReport report)
    {
        if (root.TryGetProperty("metadata", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("timecode", out JsonElement tcElem) && tcElem.GetString() is { } rawTc)
            {
                if (SmpteTimecode.TryParse(rawTc, rate, out SmpteTimecode validTc))
                {
                    return validTc.Format();
                }

                report.Add(IssueSeverity.Approximate, IssueKind.Rounding, $"Código de tiempo no válido: «{rawTc}».");
            }
        }

        if (root.TryGetProperty("global_start_time", out JsonElement gst) && gst.ValueKind == JsonValueKind.Object)
        {
            double val = gst.TryGetProperty("value", out JsonElement vElem) ? vElem.GetDouble() : 0;
            double r = gst.TryGetProperty("rate", out JsonElement rElem) ? rElem.GetDouble() : rate.ToDouble();
            Rational gstRate = OtioTime.NormalizeRate(r, null);
            long frame = checked((long)Math.Round(val));
            bool dropFrame = false;
            if (SmpteTimecode.CanDropFrame(gstRate))
            {
                SmpteTimecode tcDf = SmpteTimecode.FromFrame(frame, gstRate, dropFrame: true);
                SmpteTimecode tcNdf = SmpteTimecode.FromFrame(frame, gstRate, dropFrame: false);
                if (tcDf.Format().EndsWith(";00", StringComparison.Ordinal) && !tcNdf.Format().EndsWith(":00", StringComparison.Ordinal))
                {
                    dropFrame = true;
                }
            }

            return SmpteTimecode.FromFrame(frame, gstRate, dropFrame).Format();
        }

        return null;
    }

    private static Time GetStartTime(JsonElement elem, Rational rate, InterchangeReport? report, string rangeProp = "source_range")
    {
        if (elem.TryGetProperty(rangeProp, out JsonElement sr) && sr.ValueKind == JsonValueKind.Object)
        {
            return GetRationalTime(sr, "start_time", rate, report);
        }

        return Time.Zero;
    }

    private static Time GetDuration(JsonElement elem, Rational rate, InterchangeReport? report, string rangeProp = "source_range")
    {
        if (elem.TryGetProperty(rangeProp, out JsonElement sr) && sr.ValueKind == JsonValueKind.Object)
        {
            return GetRationalTime(sr, "duration", rate, report);
        }

        return Time.Zero;
    }

    private static Time GetRationalTime(JsonElement parent, string propName, Rational rate, InterchangeReport? report)
    {
        if (!parent.TryGetProperty(propName, out JsonElement rt) || rt.ValueKind != JsonValueKind.Object)
        {
            return Time.Zero;
        }

        double val = rt.TryGetProperty("value", out JsonElement v) ? v.GetDouble() : 0.0;
        double r = rt.TryGetProperty("rate", out JsonElement rElem) ? rElem.GetDouble() : rate.ToDouble();
        return OtioTime.RationalTimeToTime(val, r, report);
    }

    private static bool IsEnabled(JsonElement item) =>
        !item.TryGetProperty("enabled", out JsonElement enabled) || enabled.ValueKind != JsonValueKind.False;

    private static string GetSchema(JsonElement elem) =>
        elem.TryGetProperty("OTIO_SCHEMA", out JsonElement s) ? s.GetString() ?? "" : "";

    private static void SpendObject(ref int count, InterchangeLimits limits)
    {
        if (++count > limits.MaxObjects)
        {
            throw new InterchangeException($"El archivo pasa del límite de {limits.MaxObjects} objetos.");
        }
    }
}
