using Rodaje.App.Views.Transcript;
using Rodaje.Core.Commands;

namespace Rodaje.App.Services;

public sealed partial class EditorSession
{
    public bool RebuildTranscript(TranscriptPanelModel model, string? guideTrackId = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Rebuild(Project, Sequence, guideTrackId, _editVersion);
    }

    public void SelectTranscriptWords(TranscriptPanelModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Select(model.SelectedWords.Select(i => model.Document.Words[i].ClipId).Distinct());
    }

    public bool ExecuteTranscript(ICommand? command) => command is not null && Execute(command);
}
