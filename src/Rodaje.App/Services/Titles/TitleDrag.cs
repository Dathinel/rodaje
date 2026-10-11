using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Titles;

/// <summary>Arrastrar un estilo de texto de la biblioteca a la línea de tiempo: dónde cae el título nuevo.</summary>
public static class TitleDrag
{
    /// <summary>Formato del arrastre; el texto es la referencia del estilo (o <see cref="TitleStyles.BuiltInReference"/>).</summary>
    public const string DragFormat = "rodaje/estilo-texto";

    /// <summary>
    /// La pista bajo el puntero si el título cabe en ella: de video, sin bloquear, que no sea la principal ni la de
    /// subtítulos, y libre en <c>[at, at + duración)</c>. Si no, <c>null</c>: el título va a la pista libre más alta.
    /// </summary>
    public static Track? TargetTrack(Sequence sequence, Track? row, Time at)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (row is not { Kind: TrackKind.Video, Locked: false, Role: null } || sequence.VideoTracks.IndexOf(row) < 1)
        {
            return null;
        }

        Time end = at + TitleEdits.DefaultDuration;
        return row.Clips.Exists(c => c.Start < end && c.End > at) ? null : row;
    }
}
