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

        if (!Execute(SequenceFormatEdit.Command(SequenceId, width, height, frameRate, fit, _ids)))
        {
            return false;
        }

        _templateProject = Project;
        return true;
    }
}
