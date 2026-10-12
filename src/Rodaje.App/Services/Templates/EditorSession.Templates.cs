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
}
