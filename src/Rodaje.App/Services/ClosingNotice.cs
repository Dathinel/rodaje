using Rodaje.App.Services.Format;

namespace Rodaje.App.Services;

/// <summary>La pregunta antes de cerrar la ventana con trabajos en curso (exportaciones, módulos de IA, acciones largas).</summary>
public static class ClosingNotice
{
    public const string Title = "Hay trabajos en curso";

    public const string Confirm = "Cerrar y cancelar";

    /// <summary>Con un diálogo abierto no se puede preguntar antes de cerrar: se avisa en lugar de no hacer nada.</summary>
    public const string DialogOpenTitle = "Hay un diálogo abierto";

    public const string DialogOpen = "Ciérralo para poder salir de Rodaje.";

    /// <summary>El texto de la pregunta, o <c>null</c> si no hay nada en curso y se puede cerrar sin preguntar.</summary>
    public static string? Describe(int active) => active <= 0 ? null : string.Create(SpanishNumbers.Format,
        $"{(active == 1 ? "Hay un trabajo en curso" : $"Hay {active} trabajos en curso")} (exportación, módulo de IA o análisis). Si cierras ahora se cancela y se pierde lo avanzado.");
}
