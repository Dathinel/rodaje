using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>Para qué se toma un color del visor con el cuentagotas.</summary>
public enum ColorPick
{
    /// <summary>El color clave del croma (sin croma ni color).</summary>
    Key,

    /// <summary>Un punto que debería ser gris: fija temperatura y tinte de «Color y LUT» (sin color).</summary>
    WhiteBalance,

    /// <summary>El tono que elige el calificador HSL (con el color, sin el calificador).</summary>
    Qualifier,
}

/// <summary>
/// Comparar un efecto (A/B): los efectos marcados se apagan solo en la copia que recibe el reproductor, sin tocar el
/// proyecto ni el historial; la exportación nunca los ve. Y el pedido del cuentagotas con su propósito.
/// </summary>
public sealed partial class EditorSession
{
    private readonly HashSet<string> _previewOff = new(StringComparer.Ordinal);

    /// <summary>Cambió el conjunto de efectos apagados solo en la vista previa.</summary>
    public event EventHandler? PreviewOffChanged;

    /// <summary>Se pidió un cuentagotas en el visor: (clip, efecto, para qué).</summary>
    public event EventHandler<(string ClipId, string EffectId, ColorPick Purpose)>? ColorPickRequested;

    /// <summary>Si el efecto está apagado solo en la vista previa (la «B» del A/B).</summary>
    public bool IsPreviewOff(string effectId) => _previewOff.Contains(effectId);

    /// <summary>Apaga o vuelve a encender un efecto solo en la vista previa.</summary>
    public void SetPreviewOff(string effectId, bool off)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrEmpty(effectId);
        if (off ? _previewOff.Add(effectId) : _previewOff.Remove(effectId))
        {
            _playback.Reload(PlaybackProject());
            PreviewOffChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Vuelve a ver todos los efectos (al cambiar de clip o de proyecto).</summary>
    public void ClearPreviewOff()
    {
        VerifyAccess();
        if (_previewOff.Count > 0)
        {
            _previewOff.Clear();
            _playback.Reload(PlaybackProject());
            PreviewOffChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RequestColorPick(string clipId, string effectId, ColorPick purpose)
    {
        VerifyAccess();
        ColorPickRequested?.Invoke(this, (clipId, effectId, purpose));
    }

    /// <summary>La copia del reproductor con los efectos apagados para comparar.</summary>
    private Project WithPreviewOff(Project project)
    {
        if (_previewOff.Count == 0)
        {
            return project;
        }

        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.VideoTracks)
            {
                foreach (Clip clip in track.Clips)
                {
                    foreach (EffectInstance effect in clip.Effects)
                    {
                        if (_previewOff.Contains(effect.Id))
                        {
                            effect.Enabled = false;
                        }
                    }
                }
            }
        }

        return project;
    }
}
