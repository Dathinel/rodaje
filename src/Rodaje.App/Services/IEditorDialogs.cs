namespace Rodaje.App.Services;

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>Las preguntas que el editor le hace a la persona; la ventana las implementa con diálogos de Windows y de WinUI.</summary>
public interface IEditorDialogs
{
    /// <summary>El proyecto tiene cambios sin guardar: guardar, descartarlos o cancelar lo que se iba a hacer.</summary>
    Task<UnsavedChangesChoice> AskSaveChangesAsync(string projectName);

    /// <summary>Un <c>.rodaje</c> para abrir, o <c>null</c> si se canceló.</summary>
    Task<string?> PickProjectToOpenAsync();

    /// <summary>Dónde guardar el proyecto, o <c>null</c> si se canceló.</summary>
    Task<string?> PickProjectSavePathAsync(string suggestedFileName);

    /// <summary>Videos o audios para importar; vacío si se canceló.</summary>
    Task<IReadOnlyList<string>> PickMediaFilesAsync();
}
