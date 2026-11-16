using System.Globalization;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Edl;

internal enum EdlTransition
{
    Cut,
    Dissolve,
    Wipe,
}

/// <summary>Un evento de la lista, con todos los tiempos en cuadros absolutos (TC) a la tasa de la EDL.</summary>
internal sealed class EdlEvent
{
    public int Number { get; set; }

    public string Reel { get; set; } = "";

    public string Channel { get; set; } = "V";

    public EdlTransition Transition { get; set; }

    public long TransitionFrames { get; set; }

    public long SourceIn { get; set; }

    public long SourceOut { get; set; }

    public long RecordIn { get; set; }

    public long RecordOut { get; set; }

    /// <summary>Velocidad de <c>M2</c> en décimas de cuadro por segundo (25 fps a 2× es 500); nula sin <c>M2</c>.</summary>
    public int? SpeedTenths { get; set; }

    public string? ClipName { get; set; }

    public string? SourceFile { get; set; }

    public List<(long Frame, string Name)> Markers { get; } = [];

    public List<string> Titles { get; } = [];

    /// <summary>En una transición, el carrete y el punto de origen del clip que sale (la línea de duración cero de CMX).</summary>
    public string? OutgoingReel { get; set; }

    public long OutgoingSource { get; set; }

    public bool IsBlack => Reel is "BL" or "BLK" or "BLACK";
}

/// <summary>
/// Una EDL CMX 3600 en memoria. Es el punto común del escritor, el lector y la regla de fidelidad: la regla es
/// <c>ToDraft(FromSequence(s))</c>, el escritor <c>Format(FromSequence(s))</c> y el lector <c>ToDraft(Parse(texto))</c>, así
/// la prueba de ida y vuelta comprueba que el texto lo lleva todo.
/// </summary>
internal sealed class EdlList
{
    public const int MaxReelLength = 8;

    public string Title { get; set; } = "";

    public bool DropFrame { get; set; }

    public List<EdlEvent> Events { get; } = [];

    /// <summary>Lo que lleva la EDL de <paramref name="sequence"/>, con cada pérdida en <paramref name="report"/>.</summary>
    public static EdlList FromSequence(Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report)
    {
        Rational rate = sequence.FrameRate;
        int nominal = NominalBase(rate);
        string start = sequence.StartTimecode ?? options.DefaultStartTimecode;
        if (!SmpteTimecode.TryParse(start, rate, out SmpteTimecode origin))
        {
            origin = SmpteTimecode.Parse("01:00:00:00", rate);
            report.Add(IssueSeverity.Approximate, IssueKind.Rounding, $"El TC de inicio «{start}» no vale a esta tasa: se usa 01:00:00:00.");
        }

        var list = new EdlList { Title = Clean(sequence.Name), DropFrame = origin.DropFrame };
        int videoIndex = options.VideoTracks is { Count: > 0 } chosen ? chosen[0] : 0;
        bool withAudio = options.IncludeAudio && (options.VideoTracks is not { Count: > 0 } c || c[0] == videoIndex);
        IReadOnlyList<WalkTrack> walk = TimelineWalk.Of(project, sequence, flattenCompounds: true, options.Limits, report);
        var reels = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        string ReelOf(MediaRef media)
        {
            if (reels.TryGetValue(media.Id, out string? reel))
            {
                return reel;
            }

            string stem = Path.GetFileNameWithoutExtension(media.Path.Replace('\\', '/').Split('/')[^1]);
            string basis = Sanitize(media.Reel ?? (stem.Length > 0 ? stem : media.Name));
            int limit = options.LongReelNames ? int.MaxValue : MaxReelLength;
            reel = basis.Length > limit ? basis[..limit] : basis;
            for (int n = 2; !used.Add(reel); n++)
            {
                string suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
                reel = (basis.Length + suffix.Length > limit ? basis[..(limit - suffix.Length)] : basis) + suffix;
            }

            reels[media.Id] = reel;
            return reel;
        }

        var tracks = new List<(WalkTrack Track, string Channel)>();
        if (walk.FirstOrDefault(t => t.Kind == TrackKind.Video && t.Index == videoIndex) is { } video)
        {
            tracks.Add((video, "V"));
        }

        foreach (WalkTrack track in walk.Where(t => t.Kind == TrackKind.Audio))
        {
            if (withAudio && track.Index < 2)
            {
                tracks.Add((track, track.Index == 0 ? "A" : "A2"));
            }
            else if (withAudio && track.Items.OfType<WalkClip>().Any())
            {
                report.Add(IssueSeverity.Lost, IssueKind.Audio, "La EDL solo lleva las dos primeras pistas de audio.", track.Track.Name);
            }
        }

        foreach (WalkTrack track in walk.Where(t => t.Kind == TrackKind.Video && t.Index != videoIndex && t.Items.OfType<WalkClip>().Any()))
        {
            report.Add(IssueSeverity.Info, IssueKind.Track, "Cada pista de video va en su propia EDL.", track.Track.Name);
        }

        var events = new List<EdlEvent>();
        foreach ((WalkTrack track, string channel) in tracks)
        {
            EdlEvent? previous = null;
            WalkClip? previousClip = null;
            foreach (WalkClip item in track.Items.OfType<WalkClip>())
            {
                EdlEvent? made = Event(item, channel);
                if (made is null)
                {
                    previous = null;
                    previousClip = null;
                    continue;
                }

                if (item.In is { } transition && previous is not null && previousClip is not null && previous.RecordOut == made.RecordIn)
                {
                    AddTransition(transition, previous, made, previousClip, item);
                }

                events.Add(made);
                previous = made;
                previousClip = item;
            }
        }

        events.Sort((a, b) => a.RecordIn != b.RecordIn ? a.RecordIn.CompareTo(b.RecordIn) : ChannelOrder(a.Channel).CompareTo(ChannelOrder(b.Channel)));
        list.Events.AddRange(events);
        foreach (Marker marker in sequence.Markers)
        {
            long frame = origin.Frame + Frames(marker.Time);
            EdlEvent? cover = list.Events.Find(e => e.Channel == "V" && e.RecordIn <= frame && frame < e.RecordOut);
            if (cover is null)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Marker, "Un marcador fuera de los clips de video no cabe en la EDL.", marker.Name, null, marker.Time);
                continue;
            }

            if (marker.Color is not null || marker.Note is not null)
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Marker, "La EDL no lleva el color ni la nota del marcador.", marker.Name, null, marker.Time);
            }

            cover.Markers.Add((frame, Clean(marker.Name)));
        }

        foreach (Region region in sequence.Regions)
        {
            report.Add(IssueSeverity.Lost, IssueKind.Marker, "La EDL no lleva regiones.", region.Name, null, region.Start);
        }

        Number(list.Events);
        return list;

        long Frames(Time time)
        {
            long frames = time.ToFrames(rate);
            if (Time.FromFrames(frames, rate) != time)
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Rounding, "La EDL trabaja en cuadros enteros: un tiempo se redondeó al cuadro.", "", null, time);
            }

            return frames;
        }

        EdlEvent? Event(WalkClip item, string channel)
        {
            Clip clip = item.Clip;
            if (clip.Generator is { } generator)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Title, "La EDL no lleva títulos.", generator.Text.Split('\n')[0], clip.Id, item.Start);
                return null;
            }

            if (item.Media is not { Path.Length: > 0 } media)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Media, "Un clip sin medio no cabe en la EDL.", "", clip.Id, item.Start);
                return null;
            }

            if (!clip.Enabled)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Unknown, "La EDL no lleva clips desactivados.", media.Name, clip.Id, item.Start);
                return null;
            }

            if (clip.Effects.Count > 0)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Effect, "La EDL no lleva efectos.", media.Name, clip.Id, item.Start);
            }

            if (clip.Color is not null || clip.GroupId is not null || clip.LinkId is not null)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Unknown, "La EDL no lleva el color, el grupo ni el enlace del clip.", media.Name, clip.Id, item.Start);
            }

            long mediaStart = media.Timecode is { } tc && SmpteTimecode.TryParse(tc, rate, out SmpteTimecode mediaTc) ? mediaTc.Frame : 0;
            if (media.FrameRate is { } mediaRate && mediaRate != rate)
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Rounding, "El medio tiene otra tasa: su TC va a la tasa de la secuencia.", media.Name, clip.Id, item.Start);
            }

            long recordIn = origin.Frame + Frames(item.Start);
            long duration = Math.Max(1, Frames(item.Start + item.Duration) - Frames(item.Start));
            ClipSpeed? speed = clip.Speed;
            int? tenths = null;
            Time anchor = clip.SourceIn;
            if (speed is not null && !speed.IsIdentity)
            {
                if (speed.Freeze)
                {
                    tenths = 0;
                }
                else
                {
                    double rateValue = speed.Curve is null ? speed.Rate.ToDouble() : clip.SourceDuration.Ticks / (double)clip.Duration.Ticks;
                    tenths = (int)Math.Round(rateValue * nominal * 10) * (speed.Reverse ? -1 : 1);
                    if (speed.Curve is not null || Math.Abs(Math.Abs(tenths.Value) - (rateValue * nominal * 10)) > 1e-9)
                    {
                        report.Add(IssueSeverity.Approximate, IssueKind.Speed, "La EDL lleva la velocidad en décimas de cuadro por segundo: se redondeó.", media.Name, clip.Id, item.Start);
                    }

                    if (tenths == 0)
                    {
                        tenths = speed.Reverse ? -1 : 1;
                    }

                    anchor = speed.Reverse ? clip.SourceOut : clip.SourceIn;
                }

                if (speed.Offset != Time.Zero)
                {
                    report.Add(IssueSeverity.Approximate, IssueKind.Speed, "La EDL no lleva el desplazamiento de la velocidad.", media.Name, clip.Id, item.Start);
                }
            }

            long sourceIn = mediaStart + Frames(anchor);
            if (sourceIn < 0)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Media, "Un clip que empieza antes del inicio de su medio no cabe en la EDL.", media.Name, clip.Id, item.Start);
                return null;
            }

            return new EdlEvent
            {
                Reel = ReelOf(media),
                Channel = channel,
                SourceIn = sourceIn,
                SourceOut = sourceIn + duration,
                RecordIn = recordIn,
                RecordOut = recordIn + duration,
                SpeedTenths = tenths,
                ClipName = Clean(media.Name),
                SourceFile = media.Path,
            };
        }

        void AddTransition(TransitionInstance transition, EdlEvent outgoing, EdlEvent incoming, WalkClip outgoingClip, WalkClip incomingClip)
        {
            long before = Frames(transition.Before);
            long total = Frames(transition.Duration);
            string name = incomingClip.Media?.Name ?? "";
            if (total <= 0 || outgoing.SpeedTenths is not null || incoming.SpeedTenths is not null
                || outgoing.RecordOut - outgoing.RecordIn <= before || incoming.SourceIn < before)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Transition, "Esta transición no cabe en la EDL (velocidad o sin material antes del clip).", name, incomingClip.Clip.Id, incomingClip.Start);
                return;
            }

            if (transition.Type != BuiltInTransitions.Crossfade || transition.Curve is not null || transition.Parameters.Count > 0)
            {
                report.Add(IssueSeverity.Approximate, IssueKind.Transition, "La EDL solo lleva fundidos y barridos sin ajustes: la transición vuelve como fundido.", name, incomingClip.Clip.Id, incomingClip.Start);
            }

            if (before > 0)
            {
                report.Add(IssueSeverity.Info, IssueKind.Transition, "En la EDL la transición empieza en el corte: el corte se movió a su inicio (la imagen es la misma).", name, incomingClip.Clip.Id, incomingClip.Start);
            }

            outgoing.RecordOut -= before;
            outgoing.SourceOut -= before;
            incoming.RecordIn -= before;
            incoming.SourceIn -= before;
            incoming.Transition = transition.Type == BuiltInTransitions.Crossfade ? EdlTransition.Dissolve : EdlTransition.Wipe;
            incoming.TransitionFrames = total;
            incoming.OutgoingReel = outgoing.Reel;
            incoming.OutgoingSource = outgoing.SourceOut;
        }
    }

    /// <summary>La secuencia que describe la lista (lo que entrega el lector).</summary>
    public TimelineDraft ToDraft(Rational rate, string? baseDirectory, InterchangeLimits limits, InterchangeReport report)
    {
        int nominal = NominalBase(rate);
        bool drop = DropFrame && SmpteTimecode.CanDropFrame(rate);
        long start = 0;
        if (Events.Count > 0)
        {
            string first = SmpteTimecode.FromFrame(Events.Min(e => e.RecordIn), rate, drop).Format();
            start = SmpteTimecode.Parse(first[..2] + (drop ? ":00:00;00" : ":00:00:00"), rate).Frame;
        }

        var project = new Project { Id = "edl", Name = Title };
        var sequence = new Sequence
        {
            Id = "edl-secuencia",
            Name = Title,
            FrameRate = rate,
            StartTimecode = Events.Count == 0 ? null : SmpteTimecode.FromFrame(start, rate, drop).Format(),
        };
        project.Sequences.Add(sequence);
        var v1 = new Track { Id = "edl-v1", Kind = TrackKind.Video, Name = "V1" };
        sequence.VideoTracks.Add(v1);
        var audio = new Dictionary<int, Track>();
        Track Audio(int index)
        {
            if (!audio.TryGetValue(index, out Track? track))
            {
                audio[index] = track = new Track { Id = $"edl-a{index + 1}", Kind = TrackKind.Audio, Name = $"A{index + 1}" };
            }

            return track;
        }

        var media = new Dictionary<string, MediaRef>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        int clips = 0;
        foreach (EdlEvent edlEvent in Events)
        {
            foreach ((long frame, string name) in edlEvent.Markers)
            {
                sequence.Markers.Add(new Marker { Id = $"edl-m{sequence.Markers.Count}", Time = Time.FromFrames(frame - start, rate), Name = name });
            }

            if (edlEvent.IsBlack || edlEvent.RecordOut <= edlEvent.RecordIn)
            {
                continue;
            }

            if (edlEvent.Reel == "AX" && edlEvent.SourceFile is null && edlEvent.ClipName is null)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Media, "Un evento AX sin archivo no se sabe a qué medio va.", "AX", null, Time.FromFrames(edlEvent.RecordIn - start, rate));
                continue;
            }

            List<Track> targets = edlEvent.Channel switch
            {
                "V" => [v1],
                "A" or "A1" => [Audio(0)],
                "A2" => [Audio(1)],
                "AA" or "A1/A2" => [Audio(0), Audio(1)],
                "B" or "AA/V" => [v1, Audio(0), Audio(1)],
                "A/V" => [v1, Audio(0)],
                _ => [],
            };
            if (targets.Count == 0)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Unknown, $"Canal «{edlEvent.Channel}» desconocido.", edlEvent.Reel, null, Time.FromFrames(edlEvent.RecordIn - start, rate));
                continue;
            }

            // El carrete manda: una EDL a menudo solo pone «SOURCE FILE» en el primer evento de cada uno.
            string key = edlEvent.Reel;
            if (media.TryGetValue(key, out MediaRef? source) && edlEvent.SourceFile is { } other && files.TryGetValue(source.Id, out string? known) && known != other)
            {
                key = edlEvent.Reel + "|" + other;
                source = media.GetValueOrDefault(key);
            }

            if (source is not null && edlEvent.SourceFile is { } late && !files.ContainsKey(source.Id))
            {
                files[source.Id] = late;
                source.Path = SequenceBuilder.NormalizePath(late, baseDirectory) ?? source.Path;
                source.Name = edlEvent.ClipName ?? source.Name;
            }

            if (source is null)
            {
                string? path = edlEvent.SourceFile is { } file ? SequenceBuilder.NormalizePath(file, baseDirectory) : null;
                if (edlEvent.SourceFile is not null && path is null)
                {
                    report.Add(IssueSeverity.Info, IssueKind.Media, "La ruta del medio es de red o no se entiende: se buscará por el nombre.", edlEvent.SourceFile);
                }

                source = new MediaRef
                {
                    Id = $"edl-medio-{media.Count}",
                    Path = path ?? edlEvent.ClipName ?? edlEvent.Reel,
                    Name = edlEvent.ClipName ?? edlEvent.Reel,
                    Reel = edlEvent.Reel,
                    FrameRate = rate,
                };
                media[key] = source;
                if (edlEvent.SourceFile is { } file2)
                {
                    files[source.Id] = file2;
                }

                project.Media.Add(source);
            }

            Time startTime = Time.FromFrames(edlEvent.RecordIn - start, rate);
            Time duration = Time.FromFrames(edlEvent.RecordOut - edlEvent.RecordIn, rate);
            foreach (Track track in targets)
            {
                if (++clips > limits.MaxObjects)
                {
                    throw new InterchangeException($"La EDL pasa de {limits.MaxObjects} clips.");
                }

                var clip = new Clip { Id = $"edl-c{clips}", MediaId = source.Id, Start = startTime };
                if (edlEvent.SpeedTenths is { } tenths)
                {
                    ClipSpeed speed = tenths == 0
                        ? new ClipSpeed { Freeze = true }
                        : new ClipSpeed { Rate = new Rational(Math.Abs(tenths), nominal * 10L), Reverse = tenths < 0 };
                    if (speed.Freeze && track.Kind == TrackKind.Audio)
                    {
                        speed = new ClipSpeed { Rate = new Rational(1, 1) };
                        report.Add(IssueSeverity.Approximate, IssueKind.Speed, "Un congelado no vale en el audio: va a 1×.", source.Name, null, startTime);
                    }

                    if (!RetimeMath.IsValidRate(speed.Rate))
                    {
                        report.Add(IssueSeverity.Lost, IssueKind.Speed, "Velocidad fuera de 0,1× a 100×: va a 1×.", source.Name, null, startTime);
                        speed = new ClipSpeed();
                    }

                    if (speed.IsIdentity)
                    {
                        clip.SourceIn = Time.FromFrames(edlEvent.SourceIn, rate);
                        clip.SourceOut = clip.SourceIn + duration;
                    }
                    else
                    {
                        clip.Speed = speed;
                        clip.Duration = duration;
                        ClipTimeMap map = ClipTimeMap.Create(startTime, duration, Time.FromFrames(edlEvent.SourceIn, rate), speed);
                        clip.SourceIn = map.SourceIn;
                        clip.SourceOut = map.SourceOut;
                    }
                }
                else
                {
                    clip.SourceIn = Time.FromFrames(edlEvent.SourceIn, rate);
                    clip.SourceOut = clip.SourceIn + duration;
                }

                if (edlEvent.Transition != EdlTransition.Cut && edlEvent.TransitionFrames > 0)
                {
                    Clip? previous = track.Clips.Count > 0 ? track.Clips[^1] : null;
                    if (previous is not null && previous.End == startTime && previous.Speed is null && clip.Speed is null)
                    {
                        track.Transitions.Add(new TransitionInstance
                        {
                            Id = $"edl-t{clips}",
                            Type = BuiltInTransitions.Crossfade,
                            From = previous.Id,
                            To = clip.Id,
                            Duration = Time.Min(Time.FromFrames(edlEvent.TransitionFrames, rate), duration),
                            Alignment = TransitionAlignment.After,
                        });
                    }
                    else
                    {
                        report.Add(IssueSeverity.Lost, IssueKind.Transition, "Una transición sin clip pegado antes no se puede poner.", source.Name, null, startTime);
                    }
                }

                track.Clips.Add(clip);
                source.HasVideo |= track.Kind == TrackKind.Video;
                source.HasAudio |= track.Kind == TrackKind.Audio;
                source.Duration = Time.Max(source.Duration, clip.SourceOut);
            }
        }

        sequence.AudioTracks.AddRange(audio.OrderBy(p => p.Key).Select(p => p.Value));
        sequence.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        foreach (Track track in sequence.AllTracks)
        {
            track.SortClips();
        }

        return new TimelineDraft(project, sequence);
    }

    public static int NominalBase(Rational rate)
    {
        try
        {
            return SmpteTimecode.NominalBase(rate);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InterchangeException($"La EDL necesita una tasa de 24, 25, 30, 48, 50 o 60 cuadros (o sus variantes NTSC); la secuencia va a {rate}.");
        }
    }

    private static int ChannelOrder(string channel) => channel switch { "V" => 0, "A" => 1, _ => 2 };

    /// <summary>Números de evento: los dos lados de una transición comparten el suyo.</summary>
    private static void Number(List<EdlEvent> events)
    {
        int number = 0;
        foreach (EdlEvent edlEvent in events)
        {
            edlEvent.Number = ++number;
        }
    }

    /// <summary>Un texto de una sola línea, sin caracteres de control.</summary>
    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private static string Sanitize(string text)
    {
        var builder = new StringBuilder();
        foreach (char c in text.ToUpperInvariant())
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            {
                builder.Append(c);
            }
        }

        return builder.Length > 0 && builder.ToString() is not ("BL" or "BLK" or "BLACK" or "AX") ? builder.ToString() : "CINTA";
    }
}
