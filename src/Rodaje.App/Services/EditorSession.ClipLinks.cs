using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

/// <summary>
/// Enlazar y desenlazar con un clic, separar el audio de un clip de video a su pista, y copiar y pegar atributos. Actúan
/// sobre los clips seleccionados; cada orden es un paso del historial.
/// </summary>
public sealed partial class EditorSession
{
    private ClipAttributes? _copiedAttributes;

    /// <summary>Lo último copiado con <see cref="CopyAttributes"/>; nulo si no se copió nada.</summary>
    public ClipAttributes? CopiedAttributes => _copiedAttributes;

    /// <summary>
    /// Qué hace <see cref="ToggleLinkSelection"/> con la selección: enlazar si hay dos o más clips en pistas distintas que
    /// no son ya un mismo grupo, desenlazar si alguno está enlazado, o nada.
    /// </summary>
    public LinkAction LinkActionForSelection
    {
        get
        {
            Sequence sequence = Sequence;
            List<(Track Track, Clip Clip)> chosen = [.. _selection.Select(id => sequence.FindClip(id)).Where(f => f is not null).Select(f => f!.Value)];
            string? first = chosen.Count > 0 ? chosen[0].Clip.LinkId : null;
            bool sameGroup = first is not null && chosen.TrueForAll(m => m.Clip.LinkId == first);
            if (chosen.Count >= 2 && chosen.Select(m => m.Track).Distinct().Count() == chosen.Count && !sameGroup)
            {
                return LinkAction.Link;
            }

            return chosen.Exists(m => m.Clip.LinkId is not null) ? LinkAction.Unlink : LinkAction.None;
        }
    }

    /// <summary>Enlaza los seleccionados o, si eso no aplica, desenlaza sus grupos (un clic en el menú).</summary>
    public bool ToggleLinkSelection()
    {
        VerifyAccess();
        switch (LinkActionForSelection)
        {
            case LinkAction.Link:
                return Execute(Edits.LinkClips(SequenceId, [.. _selection.Where(id => Sequence.FindClip(id) is not null)], _ids));
            case LinkAction.Unlink:
                List<string> linked = OnePerLink(_selection).FindAll(id => Sequence.GetClip(id).Clip.LinkId is not null);
                return RunAsOneStep("Desenlazar", linked, id => Edits.UnlinkClips(SequenceId, id, _ids));
            default:
                return false;
        }
    }

    /// <summary>Hay algún clip de video seleccionado con sonido en su medio y sin su clip de audio.</summary>
    public bool CanDetachAudio => DetachAudioTargets().Count > 0;

    /// <summary>Separa el audio de los clips de video seleccionados a su pista y deja seleccionados también los nuevos.</summary>
    public bool DetachAudioOfSelection()
    {
        VerifyAccess();
        List<string> targets = DetachAudioTargets();
        if (targets.Count == 0)
        {
            return false;
        }

        var before = new HashSet<string>(Sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id), StringComparer.Ordinal);
        bool done = RunAsOneStep("Separar audio", targets, id => Edits.DetachAudio(SequenceId, id, ids: _ids));
        if (done)
        {
            Select([.. _selection, .. Sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id).Where(id => !before.Contains(id))]);
        }

        return done;
    }

    /// <summary>Copia los atributos del primer clip seleccionado (o del indicado) y de sus enlazados.</summary>
    public bool CopyAttributes(string? clipId = null)
    {
        VerifyAccess();
        string? id = clipId ?? _selection.Find(c => Sequence.FindClip(c) is not null);
        if (id is null || Sequence.FindClip(id) is null)
        {
            return false;
        }

        _copiedAttributes = ClipAttributes.Capture(Sequence, id);
        OnPropertyChanged(nameof(CopiedAttributes));
        return true;
    }

    /// <summary>Pega en los seleccionados los atributos copiados de los tipos pedidos, en un paso del historial.</summary>
    public bool PasteAttributes(ClipAttributeKinds kinds = ClipAttributeKinds.All)
    {
        VerifyAccess();
        List<string> targets = _selection.FindAll(id => Sequence.FindClip(id) is not null);
        if (_copiedAttributes is not { } copied || targets.Count == 0)
        {
            return false;
        }

        return Execute(Edits.PasteAttributes(SequenceId, targets, copied, kinds, SpeedScope, _ids));
    }

    private List<string> DetachAudioTargets()
    {
        Sequence sequence = Sequence;
        return OnePerLink(_selection).FindAll(id =>
            sequence.FindClip(id) is { } found
            && found.Track.Kind == TrackKind.Video
            && found.Clip.Generator is null && found.Clip.Speed is not { Freeze: true }
            && Project.FindMedia(found.Clip.MediaId) is { HasAudio: true }
            && !sequence.GetLinkGroup(id).Exists(m => m.Track.Kind == TrackKind.Audio));
    }
}

/// <summary>Qué hace el botón de enlazar con la selección.</summary>
public enum LinkAction
{
    None,
    Link,
    Unlink,
}
