using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Presentation;

namespace Rodaje.App.Services;

/// <summary>Un lado del corte que se ve en el visor: el cuadro del medio (sin efectos) y su rótulo.</summary>
/// <param name="MediaId">Medio del clip; <c>null</c> si no hay clip de ese lado o es un título o un compuesto.</param>
public sealed record TrimPreviewFrame(string? MediaId, Time SourceTime, string Caption);

/// <summary>
/// Los dos cuadros del corte mientras se arrastra un roll o un slide: en un roll, el último del clip que sale y el
/// primero del que entra; en un slide, el último del vecino de la izquierda y el primero del de la derecha.
/// </summary>
public sealed record TrimPreview(TrimPreviewFrame Left, TrimPreviewFrame Right)
{
    /// <summary>
    /// Los cuadros de ahora para el roll del corte de salida de <paramref name="clipId"/> o el slide de ese clip;
    /// <c>null</c> si el clip ya no está.
    /// </summary>
    public static TrimPreview? Of(Sequence sequence, string clipId, bool roll)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.FindClip(clipId) is not { } found)
        {
            return null;
        }

        Track track = found.Track;
        Clip clip = found.Clip;
        int index = track.Clips.IndexOf(clip);
        Clip? previous = index > 0 && track.Clips[index - 1].End == clip.Start ? track.Clips[index - 1] : null;
        Clip? next = index + 1 < track.Clips.Count && track.Clips[index + 1].Start == clip.End ? track.Clips[index + 1] : null;
        Time frame = Time.FromFrames(1, sequence.FrameRate);
        Clip? left = roll ? clip : previous;
        return new TrimPreview(LastFrame(left, frame), FirstFrame(next));
    }

    private static TrimPreviewFrame LastFrame(Clip? clip, Time frame) => clip is null
        ? new TrimPreviewFrame(null, Time.Zero, "Sin clip")
        : new TrimPreviewFrame(MediaOf(clip), Source(clip, clip.End - frame), "Sale · " + Timecode.Format(clip.End));

    private static TrimPreviewFrame FirstFrame(Clip? clip) => clip is null
        ? new TrimPreviewFrame(null, Time.Zero, "Sin clip")
        : new TrimPreviewFrame(MediaOf(clip), Source(clip, clip.Start), "Entra · " + Timecode.Format(clip.Start));

    private static string? MediaOf(Clip clip) => clip.Generator is null && !clip.IsCompound ? clip.MediaId : null;

    private static Time Source(Clip clip, Time at) => Time.Max(Time.Zero, clip.ToSourceTime(Time.Max(at, clip.Start)));
}

public sealed partial class EditorSession
{
    private TrimPreview? _trimPreview;

    /// <summary>Cambió <see cref="CurrentTrimPreview"/>: el visor muestra o quita los dos cuadros del corte.</summary>
    public event EventHandler? TrimPreviewChanged;

    /// <summary>Los dos cuadros del corte mientras dura un roll o un slide; <c>null</c> el resto del tiempo.</summary>
    public TrimPreview? CurrentTrimPreview
    {
        get => _trimPreview;
        set
        {
            if (_trimPreview != value)
            {
                _trimPreview = value;
                TrimPreviewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
