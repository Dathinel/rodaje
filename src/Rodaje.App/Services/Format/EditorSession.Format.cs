using Rodaje.App.Services.Format;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

public sealed partial class EditorSession
{
    /// <summary>
    /// El selector de formato del reproductor: medidas, cuadros por segundo y cómo quedan los clips, en un paso del
    /// historial. Desde ahí el primer video importado ya no impone su formato (quien eligió 9:16 quiere 9:16).
    /// </summary>
    public bool ChangeFormat(int width, int height, Rational frameRate, ClipFit fit)
    {
        VerifyAccess();
        if (width == Sequence.Width && height == Sequence.Height && frameRate == Sequence.FrameRate)
        {
            return false;
        }

        // Lo imposible no llega al comando, que lanza ArgumentException (no EditException) y cerraría la app desde un clic.
        if (!ProjectFormats.IsValidSize(width, height) || frameRate.Num <= 0 || frameRate.Den <= 0)
        {
            Notify(NotificationSeverity.Warning, "Formato del proyecto",
                $"{width}×{height} no es un tamaño válido: pares, de {ProjectFormats.MinSide} a {ProjectFormats.MaxSide} píxeles.");
            return false;
        }

        fit = ProjectFormats.Normalize(fit);

        if (!Execute(SequenceFormatEdit.Command(SequenceId, width, height, frameRate, fit, _ids)))
        {
            return false;
        }

        _templateProject = Project;
        return true;
    }
}
