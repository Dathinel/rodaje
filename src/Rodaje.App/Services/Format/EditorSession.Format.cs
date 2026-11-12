using Rodaje.App.Services.Format;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

public sealed partial class EditorSession
{
    /// <summary>
    /// El selector de formato del reproductor: medidas, cuadros por segundo y cómo quedan los clips, en un paso del
    /// historial. Desde ahí el primer video importado ya no impone su formato (quien eligió 9:16 quiere 9:16).
    /// </summary>
    /// <param name="colorSpace">Espacio de color de la secuencia (D-054, D-055); <c>null</c> deja el que tiene.</param>
    public bool ChangeFormat(int width, int height, Rational frameRate, ClipFit fit, SequenceColorSpace? colorSpace = null)
    {
        VerifyAccess();
        SequenceColorSpace space = colorSpace ?? Sequence.ColorSpace;
        if (width == Sequence.Width && height == Sequence.Height && frameRate == Sequence.FrameRate && space == Sequence.ColorSpace)
        {
            return false;
        }

        // Lo imposible no llega al comando, que lanza ArgumentException (no EditException) y cerraría la app desde un clic.
        if (!ProjectFormats.IsValidSize(width, height))
        {
            Notify(NotificationSeverity.Warning, "Formato del proyecto",
                $"{width}×{height} no es un tamaño válido: pares, de {ProjectFormats.MinSide} a {ProjectFormats.MaxSide} píxeles.");
            return false;
        }

        if (!Sequence.IsValidFrameRate(frameRate))
        {
            Notify(NotificationSeverity.Warning, "Formato del proyecto",
                $"{frameRate} no es una tasa válida: de 1 a {Sequence.MaxFrameRate} cuadros por segundo.");
            return false;
        }

        fit = ProjectFormats.Normalize(fit);

        if (!Execute(SequenceFormatEdit.Command(SequenceId, width, height, frameRate, fit, space, _ids)))
        {
            return false;
        }

        _templateProject = Project;
        return true;
    }
}
