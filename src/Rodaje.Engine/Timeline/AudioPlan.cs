namespace Rodaje.Engine.Timeline;

/// <summary>Clip de audio en muestras de 48 kHz: suena en <c>[StartSample, EndSample)</c> de la línea de tiempo.</summary>
internal sealed record AudioClipPlan(string ClipId, SourceKey Source, long StartSample, long EndSample, long SourceInSample);

internal sealed record AudioTrackPlan(string TrackId, IReadOnlyList<AudioClipPlan> Clips);

internal sealed record AudioPlan(IReadOnlyList<AudioTrackPlan> Tracks)
{
    public static AudioPlan Empty { get; } = new([]);

    /// <summary>Mismas pistas con los mismos clips en el mismo orden: lo que suena no cambia.</summary>
    public bool SoundsLike(AudioPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Tracks.Count != other.Tracks.Count)
        {
            return false;
        }

        for (int t = 0; t < Tracks.Count; t++)
        {
            AudioTrackPlan a = Tracks[t];
            AudioTrackPlan b = other.Tracks[t];
            if (a.TrackId != b.TrackId || !a.Clips.SequenceEqual(b.Clips))
            {
                return false;
            }
        }

        return true;
    }
}
