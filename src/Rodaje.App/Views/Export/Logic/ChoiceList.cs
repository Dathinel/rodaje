namespace Rodaje.App.Views.Export;

/// <summary>Si una lista de opciones de la vista sigue igual: entonces se mueve la elección sin rehacer los elementos.</summary>
public static class ChoiceList
{
    public static bool Same(IEnumerable<string> current, IEnumerable<string> next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        return current.SequenceEqual(next, StringComparer.Ordinal);
    }
}
