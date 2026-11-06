using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Correspondencia entre la línea de tiempo y los cuadros del origen de un clip de video.</summary>
public readonly record struct ClipTiming
{
    public ClipTiming(Time start, Time sourceIn, Time sourceOut, Rational frameRate, bool isStillImage = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIn.Ticks, nameof(sourceIn));
        if (sourceOut <= sourceIn)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceOut),
                $"La salida del origen ({sourceOut}) debe ser posterior a la entrada ({sourceIn}).");
        }

        Start = start;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        FrameRate = frameRate;
        IsStillImage = isStillImage;
    }

    public static ClipTiming FromClip(Clip clip, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return new(clip.Start, clip.SourceIn, clip.SourceOut, frameRate);
    }

    public Time Start { get; }

    public Time SourceIn { get; }

    public Time SourceOut { get; }

    public Rational FrameRate { get; }

    public bool IsStillImage { get; }

    public Time Duration => SourceOut - SourceIn;

    public Time End => Start + Duration;

    /// <summary>Cuadro visible en <see cref="SourceIn"/>; puede empezar antes si la entrada cae a mitad de cuadro.</summary>
    public long FirstFrame => IsStillImage ? 0 : SourceIn.ToFrames(FrameRate);

    /// <summary>Último cuadro que alcanza a verse antes de <see cref="SourceOut"/>.</summary>
    public long LastFrame => IsStillImage ? 0 : (SourceOut - Time.FromTicks(1)).ToFrames(FrameRate);

    public Time ToSource(Time timeline) => SourceIn + (timeline - Start);

    public Time ToTimeline(Time source) => Start + (source - SourceIn);

    /// <summary>Lleva un instante de la línea de tiempo a <c>[Start, End)</c>.</summary>
    public Time Clamp(Time timeline) =>
        timeline < Start ? Start : timeline >= End ? End - Time.FromTicks(1) : timeline;

    /// <summary>Cuadro del origen que corresponde mostrar en <paramref name="timeline"/>, acotado al clip.</summary>
    public long FrameAt(Time timeline) => IsStillImage ? 0 : ToSource(Clamp(timeline)).ToFrames(FrameRate);

    /// <summary>Primer instante de la línea de tiempo en que se ve el cuadro, acotado al clip.</summary>
    public Time TimelineStartOf(long frameIndex)
    {
        long frame = Math.Clamp(frameIndex, FirstFrame, LastFrame);
        return Time.Max(Start, ToTimeline(Time.FromFrames(frame, FrameRate)));
    }
}
