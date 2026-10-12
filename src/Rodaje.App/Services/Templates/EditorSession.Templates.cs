using Rodaje.App.Services.Templates;
using Rodaje.Core.Model;

namespace Rodaje.App.Services;

public sealed partial class EditorSession
{
    /// <summary>El proyecto que salió de una plantilla; mientras siga abierto, importar no le cambia el formato.</summary>
    private Project? _templateProject;

    /// <summary>
    /// El proyecto abierto salió de una plantilla: el primer video importado no le impone su tamaño ni su tasa (quien
    /// eligió 9:16 quiere 9:16 aunque el video sea horizontal).
    /// </summary>
    public bool KeepsTemplateFormat => _templateProject is not null && ReferenceEquals(_templateProject, Project);

    /// <summary>Como <see cref="NewProjectAsync"/>, pero con el formato y las pistas de <paramref name="template"/>.</summary>
    public async Task<bool> NewProjectFromTemplateAsync(ProjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        VerifyAccess();
        if (RefuseWhileBusy() || !await ConfirmDiscardChangesAsync())
        {
            return false;
        }

        Project project = template.CreateProject(UntitledName, _ids);
        _templateProject = project;
        ReplaceProject(project, project.Sequences[0].Id, null, $"Proyecto nuevo: {template.Name}");
        return true;
    }

    /// <summary>«Guardar como plantilla»: el formato de la secuencia abierta y sus pistas con sus nombres.</summary>
    public ProjectTemplate CreateTemplateFromSequence(string name)
    {
        VerifyAccess();
        return ProjectTemplate.FromSequence(name, Sequence);
    }

    /// <summary>
    /// «Borrar plantilla propia»: pregunta antes, porque no se puede deshacer, y solo si la persona confirma la quita de
    /// <paramref name="store"/> y escribe el archivo. <c>false</c> si no era una propia de ese almacén o no se confirmó.
    /// </summary>
    /// <exception cref="IOException">No se pudo escribir el archivo de plantillas (no cambia nada).</exception>
    /// <exception cref="UnauthorizedAccessException">Sin permiso para escribir el archivo de plantillas (no cambia nada).</exception>
    public async Task<bool> DeleteUserTemplateAsync(ProjectTemplateStore store, string id)
    {
        ArgumentNullException.ThrowIfNull(store);
        VerifyAccess();
        if (store.UserTemplates.FirstOrDefault(t => t.Id == id) is not { } template)
        {
            return false;
        }

        if (!await _dialogs.ConfirmAsync(
                "¿Borrar la plantilla?",
                $"«{template.Name}» se borra de Archivo › Proyecto nuevo desde plantilla. No se puede deshacer; los proyectos ya creados con ella no cambian.",
                "Borrar"))
        {
            return false;
        }

        return store.Remove(id);
    }
}
