using Rodaje.App.Services.Packages;

namespace Rodaje.App.Services.Effects;

/// <summary>Arma el <see cref="EffectContent"/> de la ventana y lo mantiene al día con la sesión y los paquetes.</summary>
public static class AppEffectContent
{
    /// <summary>Se llama en el hilo de la interfaz: los avisos de contenido dañado vuelven a ese hilo.</summary>
    public static EffectContent Create(PackageService packages, EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(session);
        var content = new EffectContent(new InstalledContentLoader(() => packages.Installed));
        SynchronizationContext? ui = SynchronizationContext.Current;
        content.ProjectFolder = Folder(session.FilePath);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorSession.FilePath))
            {
                content.ProjectFolder = Folder(session.FilePath);
            }
        };
        session.ProjectOpened += (_, _) => content.ProjectFolder = Folder(session.FilePath);
        packages.Changed += (_, _) => content.Invalidate();
        content.Problem += (_, e) =>
        {
            void Show() => session.Notify(NotificationSeverity.Warning, "No se pudo leer el contenido", $"«{e.Reference}»: {e.Message}");
            if (ui is null)
            {
                Show();
            }
            else
            {
                ui.Post(_ => Show(), null);
            }
        };
        return content;
    }

    private static string? Folder(string? projectFile) => projectFile is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectFile));
}
