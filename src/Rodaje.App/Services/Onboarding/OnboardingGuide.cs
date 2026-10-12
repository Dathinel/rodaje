using Rodaje.App.Services.Settings;

namespace Rodaje.App.Services.Onboarding;

/// <summary>Dónde se apoya un paso del recorrido.</summary>
public enum TourTarget
{
    Library,
    Player,
    Tools,
    Timeline,
    Inspector,
    Export,
}

/// <summary>Un paso del recorrido inicial.</summary>
public sealed record TourStep(TourTarget Target, string Title, string Text);

/// <summary>Una pista que se muestra la primera vez que se usa algo.</summary>
public sealed record Hint(string Id, string Title, string Text);

/// <summary>
/// El recorrido del primer arranque (seis pasos: importar, ver, herramientas, línea de tiempo, ajustes y exportar) y las
/// pistas contextuales, cada una una sola vez. Se puede saltar y volver a ver desde Ayuda; lo visto queda en los ajustes.
/// </summary>
public static class OnboardingGuide
{
    public const string FirstImport = "importar";
    public const string FirstEdit = "editar";
    public const string FirstExport = "exportar";
    public const string FirstFullScreen = "pantalla-completa";
    public const string FirstWatchedFolder = "carpeta-vigilada";

    public static IReadOnlyList<TourStep> Tour { get; } =
    [
        new(TourTarget.Library, "1 · Importa tus medios",
            "Arrastra videos y audios aquí (o Ctrl+I). Doble clic en uno lo pone al final de la línea de tiempo."),
        new(TourTarget.Player, "2 · Mira el resultado",
            "Espacio reproduce y pausa; J, K y L van hacia atrás, paran y avanzan. F o doble clic pone el visor en pantalla completa."),
        new(TourTarget.Tools, "3 · Herramientas rápidas",
            "Quitar silencios, subtítulos automáticos, color, ruido… Cada una actúa sobre lo que tengas seleccionado."),
        new(TourTarget.Timeline, "4 · La línea de tiempo",
            "Arrastra los clips para moverlos, sus bordes para recortarlos y S corta en el cursor. Ctrl+Z deshace cualquier paso."),
        new(TourTarget.Inspector, "5 · Ajusta cada clip",
            "Posición, texto, velocidad, filtros y máscaras del clip elegido. En Trabajos ves las exportaciones y los proxies."),
        new(TourTarget.Export, "6 · Exporta",
            "Elige un preajuste (YouTube, Reels…) o varias versiones de una vez y sigue editando mientras se exporta."),
    ];

    public static IReadOnlyList<Hint> Hints { get; } =
    [
        new(FirstImport, "Medios en la biblioteca", "Doble clic o arrastrar lleva un medio a la línea de tiempo. Clic derecho para más opciones."),
        new(FirstEdit, "Todo se deshace", "Ctrl+Z deshace y Ctrl+Y rehace. El panel derecho, en Historial, lleva a cualquier paso anterior."),
        new(FirstExport, "Se exporta en segundo plano", "Puedes seguir editando. El avance, el orden de la cola y pausarla están en Trabajos."),
        new(FirstFullScreen, "Pantalla completa", "Mueve el ratón para ver los controles. Esc o doble clic vuelve al editor."),
        new(FirstWatchedFolder, "Carpetas vigiladas", "Lo nuevo que llegue a la carpeta aparece al pie de la biblioteca para importarlo con un clic."),
    ];

    /// <summary>La pista <paramref name="id"/> si todavía no se mostró (y la anota como mostrada); nula si no toca.</summary>
    public static Hint? TakeHint(AppSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.HintsEnabled || settings.HintsShown.Contains(id) || Hints.FirstOrDefault(h => h.Id == id) is not { } hint)
        {
            return null;
        }

        settings.HintsShown.Add(id);
        return hint;
    }

    /// <summary>«Volver a mostrar las pistas».</summary>
    public static void ResetHints(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.HintsShown.Clear();
        settings.HintsEnabled = true;
    }
}
