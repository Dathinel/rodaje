namespace Rodaje.App.Services.Inspector;

/// <summary>Los elementos de un desplegable de la vista y la elección, sin WinUI.</summary>
/// <remarks>
/// Cambiar los elementos de un <c>ComboBox</c> de WinUI dentro de su propio <c>SelectionChanged</c> (elegir algo edita
/// y la edición refresca el panel) o con la lista abierta falla en código nativo y cierra la app sin excepción
/// administrada ni rastro. Por eso las vistas solo rehacen los elementos si cambió la lista (<see cref="NeedsRebuild"/>),
/// traducen el índice elegido con <see cref="ChoiceList{T}.TryGet"/>, que nunca sale de la lista, y aplican la elección
/// después del evento (<see cref="Choose{T}"/>).
/// </remarks>
public static class ComboChoices
{
    /// <summary>
    /// Si hay que rehacer los elementos: solo cuando la lista cambió (algún texto, en orden). Es la única comparación de
    /// «lista igual» de las vistas: la usan <see cref="ChoiceList{T}.Differs"/> y Exportar (desplegables y tarjetas).
    /// </summary>
    public static bool NeedsRebuild(IEnumerable<string> shown, IEnumerable<string> next)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(next);
        return !shown.SequenceEqual(next, StringComparer.Ordinal);
    }

    /// <summary>
    /// Una lista de solo textos (cada elemento representa su propio texto), para los desplegables que eligen por posición,
    /// como los de Exportar.
    /// </summary>
    public static ChoiceList<string> FromLabels(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var list = new ChoiceList<string>();
        foreach (string label in labels)
        {
            list.Add(label, label);
        }

        return list;
    }

    /// <summary>
    /// Qué hace la vista con un desplegable que muestra <paramref name="shown"/> para que muestre <paramref name="next"/>:
    /// nada con la lista abierta, rehacer los elementos solo si cambiaron y, si no, solo elegir.
    /// </summary>
    public static ComboUpdate Update<T>(ChoiceList<T> shown, ChoiceList<T> next, bool isDropDownOpen)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(next);
        return isDropDownOpen ? ComboUpdate.Keep : next.Differs(shown) ? ComboUpdate.Rebuild : ComboUpdate.Select;
    }

    /// <summary>
    /// Muestra <paramref name="next"/> con <paramref name="selected"/> elegido en <paramref name="box"/> y devuelve lo que
    /// muestra ahora: <paramref name="shown"/> si estaba abierto (no se toca nada) o no hacía falta rehacerlo. Es lo que hace
    /// <c>ComboBoxChoices.Show</c> con un <c>ComboBox</c> de WinUI; aquí, sin WinUI, para probarlo con un desplegable falso.
    /// </summary>
    /// <param name="item">El elemento de cada posición; por omisión, su texto.</param>
    /// <param name="retry">Si se da, un refresco que no toca nada por tener la lista abierta queda pendiente para cuando se cierre.</param>
    public static ChoiceList<T> Show<T, TBox>(TBox box, ChoiceList<T> shown, ChoiceList<T> next, int selected, Func<int, object>? item = null,
        ClosedListRetry? retry = null)
        where TBox : IChoiceBox
    {
        if (box is null)
        {
            throw new ArgumentNullException(nameof(box));
        }

        ComboUpdate update = Update(shown, next, box.IsDropDownOpen);
        if (update == ComboUpdate.Keep)
        {
            _ = retry?.Defer(isDropDownOpen: true);
            return shown;
        }

        if (update == ComboUpdate.Rebuild)
        {
            box.SelectedIndex = -1;
            box.ClearItems();
            for (int i = 0; i < next.Count; i++)
            {
                box.AddItem(item?.Invoke(i) ?? next.Labels[i]);
            }

            shown = next;
        }

        selected = selected >= 0 && selected < shown.Count ? selected : -1;
        if (box.SelectedIndex != selected)
        {
            box.SelectedIndex = selected;
        }

        return shown;
    }

    /// <summary>
    /// Lo que hace la vista en el <c>SelectionChanged</c> de un desplegable que edita y refresca el panel: anota la elección
    /// (<paramref name="note"/>, dentro del evento) y deja para después del evento, con <see cref="UiDeferral.RunAfterEvent"/>,
    /// la acción que devuelve (aplicar y refrescar). Refrescar dentro del propio evento puede rehacer el desplegable y cerrar
    /// la app desde WinUI (tarea 91, «Escala»).
    /// </summary>
    /// <param name="shown">Lo que muestra el desplegable.</param>
    /// <param name="selectedIndex">El índice elegido; uno que no es de la lista (−1 al vaciarse) no hace nada.</param>
    /// <param name="updating">Si el cambio lo hizo el propio refresco del panel: no hace nada.</param>
    /// <param name="note">
    /// Anota la elección y devuelve lo que se hace después del evento; nulo si no hay nada que hacer (la misma de antes o el
    /// panel ya no tiene a qué aplicarla). No debe refrescar el panel ni tocar el desplegable.
    /// </param>
    /// <param name="tryEnqueue">La cola del hilo de la interfaz (en la app, <c>DispatcherQueue.TryEnqueue</c>).</param>
    /// <param name="onException">El registro de fallos (ver <see cref="UiDeferral.RunAfterEvent"/>).</param>
    /// <returns>Si quedó una acción en la cola.</returns>
    public static bool Choose<T>(ChoiceList<T> shown, int selectedIndex, bool updating, Func<T, Action?> note, Func<Action, bool> tryEnqueue,
        Func<Exception, bool> onException)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(tryEnqueue);
        ArgumentNullException.ThrowIfNull(onException);
        if (updating || !shown.TryGet(selectedIndex, out T value) || note(value) is not { } apply)
        {
            return false;
        }

        return UiDeferral.RunAfterEvent(tryEnqueue, apply, onException);
    }
}

/// <summary>
/// El refresco de un desplegable que no se hizo porque la lista estaba abierta (<see cref="ComboUpdate.Keep"/>) y que se
/// repite al cerrarla. Si WinUI manda el <c>SelectionChanged</c> con la lista todavía abierta y la acción diferida llega
/// antes de que se cierre, el refresco no toca el desplegable y, sin esto, quedaría desfasado hasta el próximo cambio.
/// </summary>
public sealed class ClosedListRetry
{
    /// <summary>Si hay un refresco pendiente para cuando se cierre la lista.</summary>
    public bool Pending { get; private set; }

    /// <summary>
    /// Si la lista está abierta, anota el refresco pendiente y devuelve <c>true</c>: el refresco no debe tocar el desplegable.
    /// </summary>
    public bool Defer(bool isDropDownOpen)
    {
        Pending |= isDropDownOpen;
        return isDropDownOpen;
    }

    /// <summary>
    /// Lo que hace la vista en el <c>DropDownClosed</c>: si quedó un refresco pendiente, lo deja (una vez) para después del
    /// evento con <see cref="UiDeferral.RunAfterEvent"/>.
    /// </summary>
    public void Closed(Func<Action, bool> tryEnqueue, Action refresh, Func<Exception, bool> onException)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        if (!Pending)
        {
            return;
        }

        Pending = false;
        _ = UiDeferral.RunAfterEvent(tryEnqueue, refresh, onException);
    }
}

/// <summary>Lo que <see cref="ComboChoices.Show{T, TBox}"/> necesita de un desplegable (en la app, un <c>ComboBox</c> de WinUI).</summary>
public interface IChoiceBox
{
    bool IsDropDownOpen { get; }

    int SelectedIndex { get; set; }

    void ClearItems();

    void AddItem(object item);
}

/// <summary>Lo que hace la vista con un desplegable al refrescar (<see cref="ComboChoices.Update{T}"/>).</summary>
public enum ComboUpdate
{
    /// <summary>La lista está abierta: no se toca nada (el próximo refresco la pone al día).</summary>
    Keep,

    /// <summary>Los elementos no cambiaron: solo se elige, si es otro.</summary>
    Select,

    /// <summary>Cambiaron los elementos: se rehacen.</summary>
    Rebuild,
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
    /// La lista del desplegable (el texto de cada parámetro animado y su nombre, en el orden de la vista) y el que se
    /// muestra (ver <see cref="Choose"/>).
    /// </summary>
    public static (ChoiceList<string> List, int Selected) Build(IReadOnlyList<(string Name, string Label)> animated, string? remembered)
    {
        ArgumentNullException.ThrowIfNull(animated);
        var list = new ChoiceList<string>();
        foreach ((string name, string label) in animated)
        {
            list.Add(label, name);
        }

        return (list, Choose(list.Values, remembered).Selected);
    }

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
