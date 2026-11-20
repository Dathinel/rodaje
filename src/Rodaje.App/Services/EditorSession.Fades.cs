using Rodaje.App.Services.Audio;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Fundidos de audio hasta el cursor: «Fundido de entrada hasta el cursor» y «Fundido de salida desde el cursor».</summary>
public sealed partial class EditorSession
{
    /// <summary>
    /// Pone en los clips de audio seleccionados (o en el audio enlazado al video seleccionado) un fundido que termina
    /// (entrada) o empieza (salida) en el cursor; los clips que no pasan por el cursor no cambian. Un paso del historial.
    /// </summary>
    public bool FadeToPlayhead(FadeHandle handle)
    {
        VerifyAccess();
        IReadOnlyList<string> candidates = LoudnessNormalizer.AudioClips(this);
        Time at = EditPosition;
        var targets = new List<(string ClipId, double Seconds)>();
        foreach (string id in candidates)
        {
            if (Sequence.FindClip(id) is not { } found || at <= found.Clip.Start || at >= found.Clip.End)
            {
                continue;
            }

            Time length = handle == FadeHandle.In ? at - found.Clip.Start : found.Clip.End - at;
            targets.Add((id, Math.Round(length.ToSeconds(), 3)));
        }

        if (targets.Count == 0)
        {
            Notify(NotificationSeverity.Informational, "Sin clip bajo el cursor",
                candidates.Count == 0
                    ? "Selecciona un clip de audio (o un video con su audio enlazado) para ponerle el fundido."
                    : "Lleva el cursor dentro del clip de audio: el fundido va del borde del clip al cursor.");
            return false;
        }

        return Execute(FadeHandles.SetMany(SequenceId, targets, handle, _ids));
    }
}
