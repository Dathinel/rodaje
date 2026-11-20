using Rodaje.Core.Model;

namespace Rodaje.App.Views.Analysis;

/// <summary>Qué clips seleccionados entran en «Ajustar al ritmo».</summary>
public static class RhythmClips
{
    /// <summary>
    /// Los clips seleccionados que no son la música ni su grupo enlazado y se pueden montar al ritmo. Quedan fuera los clips
    /// de audio sin enlazar (otra música, la voz, efectos de sonido): son lechos de sonido, no tomas de un montaje, y acortarlos
    /// al siguiente pulso los dejaba en un trozo de décimas de segundo (R1 del repaso del 19-nov, con Ctrl+A).
    /// </summary>
    public static IReadOnlyList<string> Montage(Sequence sequence, IEnumerable<string> selected, Clip music)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(music);
        return [.. selected.Where(id => id != music.Id && sequence.FindClip(id) is { } found
            && (music.LinkId is null || found.Clip.LinkId != music.LinkId)
            && !(found.Track.Kind == TrackKind.Audio && found.Clip.LinkId is null))];
    }
}
