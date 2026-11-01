using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Copiar clips, duplicarlos y pegarlos en el cursor (tapando o insertando); cada uno es un paso del historial.</summary>
public sealed partial class EditorSession
{
    /// <summary>
    /// Lo último copiado con «Copiar clips», o <c>null</c>. Sobrevive a abrir otro proyecto: al pegar, los medios que
    /// falten se traen por su archivo.
    /// </summary>
    public ClipClipboard? Clipboard { get; private set; }

    public bool CanPaste => Clipboard is { Items.Count: > 0 };

    /// <summary>
    /// Copia la selección (con enlazados y grupos; con «ignorar grupos», solo ella), las transiciones entre lo copiado y
    /// sus medios. <c>false</c> si no hay nada que copiar.
    /// </summary>
    public bool CopySelection()
    {
        VerifyAccess();
        ClipClipboard? copied;
        try
        {
            copied = ClipClipboard.Capture(Sequence, _selection, Timeline.IgnoreGroups, Project);
        }
        catch (EditException ex)
        {
            Notify(NotificationSeverity.Warning, "No se pudo copiar", ex.Message);
            return false;
        }

        if (copied is null)
        {
            return false;
        }

        Clipboard = copied;
        return true;
    }

    /// <summary>Duplica la selección a continuación de ella, con sus transiciones, y deja seleccionadas las copias.</summary>
    public bool DuplicateSelection()
    {
        VerifyAccess();
        if (_selection.Count == 0)
        {
            return false;
        }

        SequenceEditCommand command = Edits.DuplicateClips(SequenceId, _selection, _ids, alone: Timeline.IgnoreGroups);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds);
        return true;
    }

    /// <summary>
    /// Pega lo copiado con el primero en el cursor y deja seleccionado lo pegado: tapando lo que haya o, con
    /// <paramref name="insert"/>, corriendo lo que sigue para hacerle sitio. Un medio que no esté en este proyecto se
    /// importa antes por su archivo (un paso del historial aparte); si el archivo ya no existe, se avisa y no se pega nada.
    /// </summary>
    public async Task<bool> PasteAtPlayheadAsync(bool insert = false)
    {
        VerifyAccess();
        if (Clipboard is not { } copied)
        {
            return false;
        }

        Project project = Project;
        string sequenceId = SequenceId;
        Time position = EditPosition;
        bool IsCurrent() => ReferenceEquals(project, Project) && SequenceId == sequenceId && EditPosition == position;

        ClipClipboard? ready = await BringMediaAsync(copied, IsCurrent);
        if (!IsCurrent())
        {
            Notify(NotificationSeverity.Warning, "No se pudo pegar", "El proyecto, la secuencia o el cursor cambiaron mientras se importaban los medios. Vuelve a pegar en el destino deseado.");
            return false;
        }

        if (ready is null)
        {
            return false;
        }

        SequenceEditCommand command = Edits.PasteClips(sequenceId, ready, position, _ids, insert ? PlacementMode.Insert : PlacementMode.Overwrite);
        if (!Execute(command))
        {
            return false;
        }

        Select(command.CreatedIds);
        return true;
    }

    /// <summary>
    /// Lo copiado con sus medios en este proyecto: los que ya están con otro identificador se renombran y los que faltan
    /// se importan por su archivo. <c>null</c>, con aviso, si alguno no se puede traer.
    /// </summary>
    private async Task<ClipClipboard?> BringMediaAsync(ClipClipboard copied, Func<bool> isCurrent)
    {
        MediaResolution resolution = copied.ResolveMedia(Project);
        if (resolution.IsComplete)
        {
            return copied.WithMediaRenamed(resolution.Renamed);
        }

        List<MediaRef> missing = [.. resolution.ToImport.Where(m => m.Path.Length == 0 || !File.Exists(m.Path))];
        if (missing.Count > 0)
        {
            Notify(NotificationSeverity.Warning, "No se pudo pegar", MissingMediaMessage(missing));
            return null;
        }

        ImportResult imported = await ImportMediaAsync(resolution.ToImport.Select(m => m.Path), isCurrent, CancellationToken.None);
        if (!isCurrent())
        {
            return null;
        }

        // Importar devuelve los medios en el orden de los archivos que entraron; los que no se pudieron leer no vuelven.
        MediaResolution again = copied.ResolveMedia(Project);
        if (!again.IsComplete)
        {
            if (imported.Skipped.Count == 0)
            {
                Notify(NotificationSeverity.Warning, "No se pudo pegar", MissingMediaMessage(again.ToImport));
            }

            return null;
        }

        return copied.WithMediaRenamed(again.Renamed);
    }

    private static string MissingMediaMessage(IReadOnlyList<MediaRef> missing)
    {
        IEnumerable<string> names = missing.Select(m => m.Path.Length > 0 ? $"«{Path.GetFileName(m.Path)}» ({m.Path})" : $"«{(m.Name.Length > 0 ? m.Name : m.Id)}»");
        return missing.Count == 1
            ? $"El clip copiado usa un medio que no está en este proyecto y su archivo no existe: {names.First()}."
            : $"Lo copiado usa medios que no están en este proyecto y cuyos archivos no existen: {string.Join(", ", names)}.";
    }
}
