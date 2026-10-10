using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Player;

namespace Rodaje.App.Shell;

/// <summary>Páginas que se abren encima del editor.</summary>
internal enum ShellPage
{
    Shortcuts,
    Plugins,
}

/// <summary>Lo que la ventana ofrece a las vistas: páginas modales y la raíz de XAML para diálogos.</summary>
internal interface IShellHost
{
    XamlRoot? XamlRoot { get; }

    void ShowPage(ShellPage page);

    void ClosePage();

    /// <summary>Deja solo el reproductor en toda la pantalla, o vuelve a la distribución de siempre.</summary>
    void SetPlayerFullScreen(bool fullScreen);
}

/// <summary>
/// Todo lo que una vista necesita, en un solo objeto. La ventana crea las vistas desde XAML y luego llama a
/// <see cref="IEditorView.Attach"/>; ninguna vista busca servicios por su cuenta.
/// </summary>
internal sealed class EditorServices(
    EditorSession session,
    ShortcutRouter shortcuts,
    SettingsStore settings,
    PlayerEngine player,
    IShellHost shell)
{
    /// <summary>Única fuente de verdad: proyecto, historial, selección, transporte y archivo.</summary>
    public EditorSession Session { get; } = session;

    /// <summary>Para unir acciones (<see cref="ShortcutRouter.Bind"/>) y lanzarlas desde botones (<see cref="ShortcutRouter.Invoke"/>).</summary>
    public ShortcutRouter Shortcuts { get; } = shortcuts;

    /// <summary>Catálogo de acciones y teclas: las vistas registran aquí las suyas.</summary>
    public ShortcutRegistry ShortcutRegistry => Shortcuts.Registry;

    public SettingsStore Settings { get; } = settings;

    /// <summary>Solo para la vista del reproductor: la cadena de intercambio y el tamaño del panel.</summary>
    public PlayerEngine Player { get; } = player;

    public IShellHost Shell { get; } = shell;
}

/// <summary>Una vista del editor. <see cref="Attach"/> se llama una vez, en el hilo de la interfaz, tras crearla.</summary>
internal interface IEditorView
{
    void Attach(EditorServices services);
}
