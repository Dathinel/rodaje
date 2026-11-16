using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>Lo que sobrevive en OpenTimelineIO: la regla de <see cref="Fidelity.Project"/> (D-062).</summary>
internal static class OtioFidelity
{
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report) =>
        Project(project, sequence, options, report, withMetadata: true);

    public static TimelineDraft ProjectWithoutMetadata(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report) =>
        Project(project, sequence, options, report, withMetadata: false);

    /// <summary>Sin la marca de Rodaje cada pila de OTIO es un compuesto propio: uno usado dos veces vuelve como dos copias.</summary>
    private static void Unshare(Model.Project project, Sequence main)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        int copies = 0;
        void Visit(Sequence sequence)
        {
            foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
            {
                foreach (Clip clip in track.Clips.Where(c => c.SequenceId is not null).OrderBy(c => c.Start))
                {
                    Sequence inner = project.FindSequence(clip.SequenceId!)!;
                    if (!used.Add(inner.Id))
                    {
                        inner = inner.Clone();
                        inner.Id = FormattableString.Invariant($"{inner.Id}~{copies++}");
                        project.Sequences.Insert(0, inner);
                        clip.SequenceId = inner.Id;
                    }

                    Visit(inner);
                }
            }
        }

        Visit(main);
    }

    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report, bool withMetadata)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);

        Model.Project copy = project.Clone();
        Sequence mainCopy = copy.FindSequence(sequence.Id)
            ?? throw new ArgumentException("La secuencia principal no está en el proyecto.", nameof(sequence));

        List<Sequence> compounds = DraftCopy.CompoundsOf(copy, mainCopy, options.Limits);

        if (options.VideoTracks is not null)
        {
            var selectedTracks = new HashSet<int>(options.VideoTracks);
            for (int i = mainCopy.VideoTracks.Count - 1; i >= 0; i--)
            {
                if (!selectedTracks.Contains(i))
                {
                    mainCopy.VideoTracks.RemoveAt(i);
                }
            }
        }

        if (!options.IncludeAudio)
        {
            mainCopy.AudioTracks.Clear();
        }

        copy.Sequences = [.. compounds, mainCopy];

        if (withMetadata)
        {
            FilterUsedMedia(copy);
            return new TimelineDraft(copy, mainCopy);
        }

        Unshare(copy, mainCopy);
        foreach (Sequence seq in copy.Sequences)
        {
            ProjectSequenceStandard(seq, seq == mainCopy, options, report);
        }

        ProjectMediaStandard(copy, mainCopy.FrameRate, report);
        FilterUsedMedia(copy);
        return new TimelineDraft(copy, mainCopy);
    }

    private static void ProjectSequenceStandard(Sequence seq, bool isMain, InterchangeOptions options, InterchangeReport report)
    {
        seq.AudioBuses.Clear();
        seq.MasterEffects.Clear();
        seq.MasterGainDb = 0;

        if (isMain)
        {
            string tc = seq.StartTimecode ?? options.DefaultStartTimecode;
            if (SmpteTimecode.TryParse(tc, seq.FrameRate, out SmpteTimecode parsedTc))
            {
                seq.StartTimecode = parsedTc.Format();
            }
            else
            {
                seq.StartTimecode = SmpteTimecode.FromFrame(0, seq.FrameRate, SmpteTimecode.CanDropFrame(seq.FrameRate)).Format();
            }
        }
        else
        {
            seq.StartTimecode = null;
        }

        foreach (Track track in seq.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                clip.Effects.Clear();
                clip.Color = null;

                if (clip.Speed is not null)
                {
                    if (clip.Speed.Freeze)
                    {
                        // Se conserva congelado
                    }
                    else if (clip.Speed.Curve is not null)
                    {
                        double avg = (double)(clip.SourceOut.Ticks - clip.SourceIn.Ticks) / clip.Duration.Ticks;
                        clip.Speed = new ClipSpeed { Rate = OtioTime.RationalFromDouble(avg) };
                        report.Add(IssueSeverity.Approximate, IssueKind.Speed, "Curva de velocidad convertida en velocidad media.");
                    }
                }
            }

            foreach (TransitionInstance transition in track.Transitions)
            {
                if (transition.Type != BuiltInTransitions.Crossfade)
                {
                    transition.Type = BuiltInTransitions.Crossfade;
                }

                transition.Curve = null;
                transition.Ease = null;
            }
        }

        foreach (Marker marker in seq.Markers)
        {
            marker.Color = OtioColor.FromOtioColor(OtioColor.ToOtioColor(marker.Color));
        }

        foreach (Region region in seq.Regions)
        {
            region.Color = OtioColor.FromOtioColor(OtioColor.ToOtioColor(region.Color));
        }

        seq.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        seq.Regions.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private static void ProjectMediaStandard(Model.Project project, Rational mainRate, InterchangeReport report)
    {
        var videoMediaIds = project.Sequences
            .SelectMany(s => s.VideoTracks.SelectMany(t => t.Clips))
            .Where(c => c.Generator is null && c.SequenceId is null)
            .Select(c => c.MediaId)
            .ToHashSet(StringComparer.Ordinal);
        var audioMediaIds = project.Sequences
            .SelectMany(s => s.AudioTracks.SelectMany(t => t.Clips))
            .Where(c => c.Generator is null && c.SequenceId is null)
            .Select(c => c.MediaId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (MediaRef media in project.Media)
        {
            media.HasVideo = videoMediaIds.Contains(media.Id);
            media.HasAudio = audioMediaIds.Contains(media.Id);
            media.Width = 0;
            media.Height = 0;
            media.SampleRate = 0;
            media.AudioChannels = 0;
            media.Reel = null;

            if (media.Timecode is not null && SmpteTimecode.TryParse(media.Timecode, media.FrameRate ?? mainRate, out SmpteTimecode mTc))
            {
                media.Timecode = mTc.Frame == 0 ? null : mTc.Format();
            }
            else
            {
                media.Timecode = null;
            }
        }
    }

    private static void FilterUsedMedia(Model.Project project)
    {
        var usedMediaIds = project.Sequences
            .SelectMany(s => s.AllTracks.SelectMany(t => t.Clips))
            .Where(c => c.Generator is null && c.SequenceId is null)
            .Select(c => c.MediaId)
            .ToHashSet(StringComparer.Ordinal);

        project.Media = [.. project.Media.Where(m => usedMediaIds.Contains(m.Id))];
    }
}
