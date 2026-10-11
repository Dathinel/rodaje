using Microsoft.UI.Xaml;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Settings;
using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Views.Player;
using Rodaje.Engine.Timeline;

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

    /// <summary>Miniaturas y formas de onda de los medios (biblioteca y línea de tiempo).</summary>
    public required IClipVisuals Visuals { get; init; }

    /// <summary>Paquetes y módulos instalados, el catálogo e instalar o desinstalar.</summary>
    public required PackageService Packages { get; init; }

    /// <summary>LUT y transiciones de paquete para el motor; nulo si no hay implementación (<see cref="EffectContentFactory"/>).</summary>
    public IEffectContent? Content { get; init; }

    /// <summary>Cambió la calidad de la vista previa (desde el reproductor o desde el menú Ver).</summary>
    public event EventHandler? PreviewQualityChanged;

    /// <summary>Guarda la calidad en los ajustes, la pasa al motor y avisa a quien la muestre.</summary>
    public void SetPreviewQuality(PreviewQuality quality)
    {
        Settings.Current.PreviewQuality = quality;
        Player.SetPreviewQuality(quality);
        PreviewQualityChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Punto donde se conecta el contenido de paquetes (LUT y transiciones) con el motor. La implementación vive en
/// <c>Services/Effects</c> y completa <see cref="Create"/>; si no está, el contenido queda nulo y el motor sigue sin
/// LUT y con las transiciones de paquete como fundido cruzado.
/// </summary>
internal static partial class EffectContentFactory
{
    public static IEffectContent? CreateOrNull(PackageService packages)
    {
        IEffectContent? content = null;
        Create(packages, ref content);
        return content;
    }

    static partial void Create(PackageService packages, ref IEffectContent? content);
}

/// <summary>Una vista del editor. <see cref="Attach"/> se llama una vez, en el hilo de la interfaz, tras crearla.</summary>
internal interface IEditorView
{
    void Attach(EditorServices services);
}
