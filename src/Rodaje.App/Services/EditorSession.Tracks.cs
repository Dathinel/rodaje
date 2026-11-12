using System.Globalization;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>Eliminar y reordenar pistas desde sus cabeceras; la principal no se elimina ni se mueve.</summary>
public sealed partial class EditorSession
{
    /// <summary>Elimina la pista con sus clips en un solo paso del historial; si tiene clips, pregunta antes.</summary>
    public async Task<bool> DeleteTrackAsync(string trackId)
    {
        VerifyAccess();
        if (Sequence.FindTrack(trackId) is not { } track)
        {
            return false;
        }

        if (track.Clips.Count > 0)
        {
            string clips = track.Clips.Count == 1 ? "un clip" : string.Create(CultureInfo.InvariantCulture, $"{track.Clips.Count} clips");
            if (!await _dialogs.ConfirmAsync(
                    "¿Eliminar la pista?",
                    $"«{track.Name}» tiene {clips}, que se eliminan con ella. Se puede deshacer.",
                    "Eliminar pista"))
            {
                return false;
            }
        }

        return Execute(Edits.DeleteTrack(SequenceId, trackId, _ids));
    }

    /// <summary>Lleva la pista a la posición <paramref name="newIndex"/> de su tipo.</summary>
    public bool ReorderTrack(string trackId, int newIndex)
    {
        VerifyAccess();
        return Execute(Edits.ReorderTrack(SequenceId, trackId, newIndex, _ids));
    }

    /// <summary>Sube o baja la pista un lugar en la línea de tiempo.</summary>
    public bool StepTrack(string trackId, bool up)
    {
        VerifyAccess();
        return Execute(Edits.StepTrack(SequenceId, trackId, up, _ids));
    }
}
