namespace Rodaje.App.Services.Inspector;

/// <summary>Los elementos de un desplegable de la vista y la elección, sin WinUI.</summary>
/// <remarks>
/// Cambiar los elementos de un <c>ComboBox</c> de WinUI dentro de su propio <c>SelectionChanged</c> (elegir algo edita
/// y la edición refresca el panel) o con la lista abierta falla en código nativo y cierra la app sin excepción
/// administrada ni rastro. Por eso las vistas solo rehacen los elementos si cambió la lista (<see cref="NeedsRebuild"/>)
/// y traducen el índice elegido con <see cref="Picked"/>, que nunca sale de la lista.
/// </remarks>
public static class ComboChoices
{
    /// <summary>Si hay que rehacer los elementos: solo cuando la lista cambió.</summary>
    public static bool NeedsRebuild(IReadOnlyList<string> shown, IReadOnlyList<string> next)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(next);
        return !shown.SequenceEqual(next, StringComparer.Ordinal);
    }

    /// <summary>
    /// El elemento elegido en <paramref name="selectedIndex"/>; nulo si el índice no es de la lista (−1 al vaciarse, o uno
    /// viejo tras rehacerla).
    /// </summary>
    public static T? Picked<T>(IReadOnlyList<T> items, int selectedIndex)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;
    }
}

/// <summary>
/// Lo que ofrece un desplegable de la vista: el texto de cada elemento y lo que representa, en el mismo orden. La vista
/// arma la lista nueva en cada refresco y solo rehace los elementos si <see cref="Differs"/> (ver <see cref="ComboChoices"/>).
/// </summary>
/// <typeparam name="T">Lo que representa cada elemento (puede ser nulo, por ejemplo «ninguno»).</typeparam>
public sealed class ChoiceList<T>
{
    private readonly List<string> _labels = [];
    private readonly List<T> _values = [];

    public IReadOnlyList<string> Labels => _labels;

    public IReadOnlyList<T> Values => _values;

    public int Count => _values.Count;

    public void Add(string label, T value)
    {
        ArgumentNullException.ThrowIfNull(label);
        _labels.Add(label);
        _values.Add(value);
    }

    /// <summary>La posición del primer elemento que representa <paramref name="value"/>; −1 si no está.</summary>
    public int IndexOf(T value) => _values.IndexOf(value);

    public bool Contains(T value) => _values.Contains(value);

    /// <summary>
    /// Lo que representa el elemento elegido en <paramref name="selectedIndex"/>; falso si el índice no es de la lista (−1 al
    /// vaciarse, o uno viejo tras rehacerla).
    /// </summary>
    public bool TryGet(int selectedIndex, out T value)
    {
        if (selectedIndex >= 0 && selectedIndex < _values.Count)
        {
            value = _values[selectedIndex];
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Si hay que rehacer los elementos para pasar de <paramref name="shown"/> a esta lista: cambió un texto o lo que representa.</summary>
    public bool Differs(ChoiceList<T> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return ComboChoices.NeedsRebuild(shown._labels, _labels) || !shown._values.SequenceEqual(_values, EqualityComparer<T>.Default);
    }
}

/// <summary>El desplegable de la gráfica de keyframes (Propiedades): qué parámetros animados ofrece y cuál se muestra.</summary>
/// <remarks>
/// La vista rehacía sus elementos en cada refresco: al elegir «Escala» (el refresco iba dentro del SelectionChanged) y en
/// cada cuadro al reproducir con la lista abierta. Ver <see cref="ComboChoices"/>.
/// </remarks>
public static class GraphParameterChoice
{
    /// <summary>
    /// Los parámetros que se ofrecen (los animados, en el orden de la vista) y cuál se muestra: el recordado si sigue
    /// animado, si no el primero; −1 si no hay ninguno.
    /// </summary>
    public static (IReadOnlyList<string> Names, int Selected) Choose(IReadOnlyList<string> animated, string? remembered)
    {
        ArgumentNullException.ThrowIfNull(animated);
        if (animated.Count == 0)
        {
            return ([], -1);
        }

        int index = -1;
        for (int i = 0; remembered is not null && i < animated.Count; i++)
        {
            if (string.Equals(animated[i], remembered, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        return (animated, index >= 0 ? index : 0);
    }
}
