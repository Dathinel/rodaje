using Rodaje.App.Services.Packages;

namespace Rodaje.App.Services.Effects;

/// <summary>Arma el <see cref="EffectContent"/> de la ventana y lo mantiene al día con los paquetes y la sesión.</summary>
public static class AppEffectContent
{
    /// <summary>Lee lo instalado y descarta lo leído cuando cambian los paquetes.</summary>
    public static EffectContent Create(PackageService packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var content = new EffectContent(new InstalledContentLoader(() => packages.Installed));
        packages.Changed += (_, _) => content.Invalidate();
        return content;
    }

    /// <summary>
    /// Une la sesión: la carpeta del proyecto (LUT sueltas con ruta relativa) y los avisos de contenido dañado, que
    /// vuelven al hilo de la interfaz. Se llama una vez, en ese hilo.
    /// </summary>
    public static void AttachSession(EffectContent content, EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(session);
        SynchronizationContext? ui = SynchronizationContext.Current;
        content.ProjectFolder = Folder(session.FilePath);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorSession.FilePath))
            {
                content.ProjectFolder = Folder(session.FilePath);
            }
        };
        session.ProjectOpened += (_, _) =>
        {
            content.ProjectFolder = Folder(session.FilePath);

            // Los decodificadores de las máscaras de imagen del proyecto anterior quedaban abiertos hasta cerrar la app.
            content.CloseMattes();
        };
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
    }

    private static string? Folder(string? projectFile) => projectFile is null ? null : Path.GetDirectoryName(Path.GetFullPath(projectFile));
}
