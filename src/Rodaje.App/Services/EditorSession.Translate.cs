using Rodaje.App.Services.Translate;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>«Traducir pista»: la pista traducida entra en un solo paso del historial.</summary>
public sealed partial class EditorSession
{
    /// <summary>La pista que se ofrece traducir: la del clip de texto seleccionado o la primera de subtítulos.</summary>
    public Track? TranslatableTrack() => SubtitleTrackTranslation.DefaultTrack(Sequence, SelectedClipIds);

    /// <summary>
    /// Crea la pista traducida encima de <paramref name="sourceTrackId"/> con <paramref name="texts"/> (por id de clip)
    /// y deja seleccionados sus clips. Los clips que se borraron mientras se traducía no se copian.
    /// </summary>
    public bool AddTranslatedTrack(string sourceTrackId, IReadOnlyDictionary<string, string> texts, TranslationLanguage target)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(target);
        if (Sequence.FindTrack(sourceTrackId) is not { } source)
        {
            return false;
        }

        string name = SubtitleTrackTranslation.TrackName(Sequence, source, target);
        SequenceEditCommand command = SubtitleTrackTranslation.Command(SequenceId, sourceTrackId, texts, name, _ids);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds.Take(command.CreatedIds.Count - 1));
        return true;
    }
}
