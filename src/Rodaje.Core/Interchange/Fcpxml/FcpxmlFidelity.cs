using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Lo que sobrevive en este formato: la regla de <see cref="Fidelity.Project"/> (D-062).</summary>
internal static class FcpxmlFidelity
{
    public static TimelineDraft Project(Model.Project project, Sequence sequence, InterchangeOptions options, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);

        Model.Project projectCopy = project.Clone();
        Sequence mainCopy = projectCopy.FindSequence(sequence.Id)
            ?? throw new ArgumentException("La secuencia principal no está en el proyecto.", nameof(sequence));

        List<Sequence> compounds = DraftCopy.CompoundsOf(projectCopy, mainCopy, options.Limits);
        projectCopy.Sequences = [.. compounds, mainCopy];

        foreach (Sequence seq in projectCopy.Sequences)
        {
            ProjectSequence(seq, report);
        }

        foreach (MediaRef media in projectCopy.Media)
        {
            if (media.Timecode is not null && !SmpteTimecode.TryParse(media.Timecode, media.FrameRate ?? mainCopy.FrameRate, out _))
            {
                media.Timecode = null;
                report.Add(IssueSeverity.Info, IssueKind.Rounding, "Código de tiempo del medio no válido.");
            }
        }

        return new TimelineDraft(projectCopy, mainCopy);
    }

    private static void ProjectSequence(Sequence sequence, InterchangeReport report)
    {
        sequence.AudioBuses.Clear();
        sequence.MasterEffects.Clear();
        sequence.MasterGainDb = 0;

        foreach (Region region in sequence.Regions)
        {
            sequence.Markers.Add(new Marker
            {
                Id = region.Id,
                Name = region.Name,
                Time = region.Start,
            });
            report.Add(IssueSeverity.Approximate, IssueKind.Marker, $"Región «{region.Name}» convertida en marcador al inicio.");
        }

        sequence.Regions.Clear();

        if (sequence.StartTimecode is not null && !SmpteTimecode.TryParse(sequence.StartTimecode, sequence.FrameRate, out _))
        {
            sequence.StartTimecode = null;
            report.Add(IssueSeverity.Info, IssueKind.Rounding, "Código de tiempo de la secuencia no válido.");
        }

        for (int i = 0; i < sequence.VideoTracks.Count; i++)
        {
            Track track = sequence.VideoTracks[i];
            bool isSpine = i == 0;
            ProjectTrack(track, sequence, isSpine, report);
        }

        for (int i = 0; i < sequence.AudioTracks.Count; i++)
        {
            Track track = sequence.AudioTracks[i];
            ProjectTrack(track, sequence, isSpine: false, report);
        }

        sequence.VideoTracks.RemoveAll(t => t.Clips.Count == 0);
        sequence.AudioTracks.RemoveAll(t => t.Clips.Count == 0);

        foreach (Marker marker in sequence.Markers)
        {
            marker.Color = null;
        }

        sequence.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private static void ProjectTrack(Track track, Sequence sequence, bool isSpine, InterchangeReport report)
    {
        var removedClipIds = new HashSet<string>(StringComparer.Ordinal);
        for (int c = track.Clips.Count - 1; c >= 0; c--)
        {
            Clip clip = track.Clips[c];
            if (clip.Generator is not null)
            {
                removedClipIds.Add(clip.Id);
                track.Clips.RemoveAt(c);
                sequence.Markers.Add(new Marker
                {
                    Id = clip.Id,
                    Name = clip.Generator.Text,
                    Time = clip.Start,
                });
                report.Add(IssueSeverity.Lost, IssueKind.Title, "El título se convierte en un hueco con un marcador.", clipId: clip.Id);
                continue;
            }

            ProjectClip(clip, report);
        }

        if (removedClipIds.Count > 0)
        {
            track.Transitions.RemoveAll(t => removedClipIds.Contains(t.From) || removedClipIds.Contains(t.To));
        }

        if (isSpine)
        {
            foreach (TransitionInstance transition in track.Transitions)
            {
                if (transition.Type != BuiltInTransitions.Crossfade)
                {
                    transition.Type = BuiltInTransitions.Crossfade;
                    report.Add(IssueSeverity.Approximate, IssueKind.Transition, "Transición convertida en fundido cruzado.");
                }

                transition.Curve = null;
                transition.Ease = null;
                transition.Parameters.Clear();
                transition.Options.Clear();
            }
        }
        else
        {
            if (track.Transitions.Count > 0)
            {
                report.Add(IssueSeverity.Lost, IssueKind.Transition, "Transición en pista secundaria o de audio no admitida en FCPXML.");
                track.Transitions.Clear();
            }
        }
    }

    private static void ProjectClip(Clip clip, InterchangeReport report)
    {
        if (clip.Color is not null)
        {
            clip.Color = null;
            report.Add(IssueSeverity.Lost, IssueKind.Color, "El color del clip se pierde en FCPXML.", clipId: clip.Id);
        }

        if (clip.GroupId is not null)
        {
            clip.GroupId = null;
            report.Add(IssueSeverity.Lost, IssueKind.Effect, "El grupo del clip se pierde en FCPXML.", clipId: clip.Id);
        }

        if (clip.LinkId is not null)
        {
            clip.LinkId = null;
            report.Add(IssueSeverity.Approximate, IssueKind.Effect, "El enlace del clip se pierde en FCPXML.", clipId: clip.Id);
        }

        if (clip.Speed is not null)
        {
            clip.Speed.PreservePitch = true;
            clip.Speed.FrameSampling = FrameSampling.Nearest;
            clip.Speed.Offset = Time.Zero;
            if (clip.Speed.Curve is not null)
            {
                clip.Speed.Curve = null;
                report.Add(IssueSeverity.Approximate, IssueKind.Speed, "Curva de velocidad aproximada.", clipId: clip.Id);
            }
        }

        var keptEffects = new List<EffectInstance>();
        foreach (EffectInstance effect in clip.Effects)
        {
            if (effect.Type == BuiltInEffects.Transform)
            {
                var copy = effect.Clone();
                copy.Options.Clear();

                double x = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.X)?.Value ?? 0;
                double y = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Y)?.Value ?? 0;
                double scale = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Scale)?.Value ?? 1;
                double scaleX = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleX)?.Value ?? 1;
                double scaleY = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.ScaleY)?.Value ?? 1;
                double rotation = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Rotation)?.Value ?? 0;
                double anchorX = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorX)?.Value ?? 0;
                double anchorY = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.AnchorY)?.Value ?? 0;
                double opacity = copy.Parameters.GetValueOrDefault(BuiltInEffects.TransformParameters.Opacity)?.Value ?? 1;

                copy.Parameters.Clear();
                copy.Parameters[BuiltInEffects.TransformParameters.X] = new Parameter { Value = x };
                copy.Parameters[BuiltInEffects.TransformParameters.Y] = new Parameter { Value = y };

                double totalX = scale * scaleX;
                double totalY = scale * scaleY;
                if (Math.Abs(totalX - totalY) < 1e-9)
                {
                    copy.Parameters[BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = totalX };
                    copy.Parameters[BuiltInEffects.TransformParameters.ScaleX] = new Parameter { Value = 1.0 };
                    copy.Parameters[BuiltInEffects.TransformParameters.ScaleY] = new Parameter { Value = 1.0 };
                }
                else
                {
                    copy.Parameters[BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = 1.0 };
                    copy.Parameters[BuiltInEffects.TransformParameters.ScaleX] = new Parameter { Value = totalX };
                    copy.Parameters[BuiltInEffects.TransformParameters.ScaleY] = new Parameter { Value = totalY };
                }

                copy.Parameters[BuiltInEffects.TransformParameters.Rotation] = new Parameter { Value = rotation };
                copy.Parameters[BuiltInEffects.TransformParameters.AnchorX] = new Parameter { Value = anchorX };
                copy.Parameters[BuiltInEffects.TransformParameters.AnchorY] = new Parameter { Value = anchorY };
                copy.Parameters[BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = opacity };

                keptEffects.Add(copy);
                report.Add(IssueSeverity.Approximate, IssueKind.Transform, "Transformación aproximada en FCPXML.", clipId: clip.Id);
            }
            else if (effect.Type == BuiltInEffects.Audio)
            {
                var copy = effect.Clone();
                copy.Options.Clear();
                double gainDb = copy.Parameters.GetValueOrDefault(BuiltInEffects.AudioParameters.GainDb)?.Value ?? 0;
                copy.Parameters.Clear();
                copy.Parameters[BuiltInEffects.AudioParameters.GainDb] = new Parameter { Value = gainDb };
                keptEffects.Add(copy);
                report.Add(IssueSeverity.Approximate, IssueKind.Audio, "Ajuste de volumen aproximado en FCPXML.", clipId: clip.Id);
            }
            else if (effect.Type == BuiltInEffects.Crop)
            {
                var copy = effect.Clone();
                copy.Options.Clear();
                keptEffects.Add(copy);
                report.Add(IssueSeverity.Approximate, IssueKind.Transform, "Recorte aproximado en FCPXML.", clipId: clip.Id);
            }
            else
            {
                report.Add(IssueSeverity.Lost, IssueKind.Effect, $"Efecto «{effect.Type}» no admitido en FCPXML.", clipId: clip.Id);
            }
        }

        clip.Effects = keptEffects;
    }
}
