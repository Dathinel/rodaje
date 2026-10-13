using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Un desplegable falso que se porta como el <c>ComboBox</c> de WinUI en lo que importa para la tarea 91: cambiar la
/// selección lanza <see cref="SelectionChanged"/> en el acto, y rehacer los elementos dentro de ese evento o con la lista
/// abierta falla (en WinUI, en código nativo y sin rastro; aquí, con una excepción). Con
/// <see cref="SelectsWithListOpen"/>, el <c>SelectionChanged</c> llega con la lista todavía abierta (no se sabe en qué orden
/// lo hace WinUI) y la lista se cierra después con <see cref="Close"/>, que lanza <see cref="DropDownClosed"/>.
/// </summary>
internal sealed class FakeChoiceBox : IChoiceBox
{
    private int _selected = -1;
    // Cuántos SelectionChanged corren a la vez: Show pone −1 antes de vaciar y eso lanza otro dentro del primero, que al
    // terminar no debe dar por terminado el de fuera (con un booleano, rehacer dentro del evento pasaba sin fallar).
    private int _inSelectionChanged;

    public List<object> Items { get; } = [];

    public bool IsDropDownOpen { get; set; }

    /// <summary>Cuántas veces se vaciaron los elementos para rehacerlos.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Lo que hace la vista al cambiar la selección (el <c>SelectionChanged</c> del desplegable).</summary>
    public Action? SelectionChanged { get; set; }

    /// <summary>Lo que hace la vista al cerrarse la lista (el <c>DropDownClosed</c> del desplegable).</summary>
    public Action? DropDownClosed { get; set; }

    /// <summary>Si <see cref="Pick"/> elige con la lista abierta y la deja abierta hasta <see cref="Close"/>.</summary>
    public bool SelectsWithListOpen { get; set; }

    /// <summary>Si ahora mismo corre el <see cref="SelectionChanged"/>.</summary>
    public bool InSelectionChanged => _inSelectionChanged > 0;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < -1 || value >= Items.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"El índice {value} no es de la lista ({Items.Count} elementos).");
            }

            if (value != _selected)
            {
                _selected = value;
                Raise();
            }
        }
    }

    public void ClearItems()
    {
        Guard();
        Rebuilds++;
        Items.Clear();
        if (_selected != -1)
        {
            _selected = -1;
            Raise();
        }
    }

    public void AddItem(object item)
    {
        Guard();
        Items.Add(item);
    }

    /// <summary>
    /// La persona elige el elemento <paramref name="index"/> con la lista abierta: se cierra antes de elegir o, con
    /// <see cref="SelectsWithListOpen"/>, sigue abierta hasta <see cref="Close"/>.
    /// </summary>
    public void Pick(int index)
    {
        if (SelectsWithListOpen)
        {
            IsDropDownOpen = true;
        }
        else
        {
            Close();
        }

        SelectedIndex = index;
    }

    /// <summary>Se cierra la lista (si estaba abierta) y se lanza <see cref="DropDownClosed"/>.</summary>
    public void Close()
    {
        if (IsDropDownOpen)
        {
            IsDropDownOpen = false;
            DropDownClosed?.Invoke();
        }
    }

    private void Raise()
    {
        _inSelectionChanged++;
        try
        {
            SelectionChanged?.Invoke();
        }
        finally
        {
            _inSelectionChanged--;
        }
    }

    private void Guard()
    {
        if (InSelectionChanged)
        {
            throw new InvalidOperationException("Se rehizo el desplegable dentro de su propio SelectionChanged: WinUI cerraría la app.");
        }

        if (IsDropDownOpen)
        {
            throw new InvalidOperationException("Se rehizo el desplegable con la lista abierta: WinUI cerraría la app.");
        }
    }
}

/// <summary>Lo que hace un panel con un desplegable, con <see cref="FakeChoiceBox"/> y la misma función que arma su lista.</summary>
internal static class ComboScenario
{
    /// <summary>
    /// Muestra la lista que arma <paramref name="build"/> para <paramref name="initial"/> y elige cada opción como la persona,
    /// con el mismo <see cref="ComboChoices.Choose{T}"/> que las vistas y una cola falsa: el <c>SelectionChanged</c> solo anota
    /// la elección, que se aplica (una vez) después del evento, y el refresco vuelve a armar la lista con lo elegido y la
    /// muestra. Comprueba que nada se aplica ni se refresca dentro del evento, que la lista no se rehace, que queda elegida esa
    /// opción y que con la lista abierta no se toca nada. Lo hace dos veces: con la lista cerrada antes del evento y con el
    /// evento llegando con la lista abierta (el refresco se repite al cerrarla, <see cref="ClosedListRetry"/>).
    /// </summary>
    /// <param name="build">La función de la vista: la lista y la posición elegida para lo que muestra el panel.</param>
    /// <param name="choosable">Qué opciones se pueden elegir sin salir del panel (por omisión, todas).</param>
    /// <returns>Cuántas opciones se eligieron.</returns>
    public static int ChooseEach<T>(Func<T, (ChoiceList<T> List, int Selected)> build, T initial, Func<T, bool>? choosable = null)
    {
        int chosen = ChooseEach(build, initial, choosable, selectsWithListOpen: false);
        Assert.Equal(chosen, ChooseEach(build, initial, choosable, selectsWithListOpen: true));
        return chosen;
    }

    private static int ChooseEach<T>(Func<T, (ChoiceList<T> List, int Selected)> build, T initial, Func<T, bool>? choosable, bool selectsWithListOpen)
    {
        var box = new FakeChoiceBox { SelectsWithListOpen = selectsWithListOpen };
        bool updating = false;
        T state = initial;
        var later = new Queue<Action>();
        var retry = new ClosedListRetry();
        int applied = 0;
        (ChoiceList<T> list, int selected) = build(state);
        ChoiceList<T> shown = Show(box, new ChoiceList<T>(), list, selected, ref updating);
        Assert.Same(list, shown);
        int rebuilds = box.Rebuilds;

        bool Enqueue(Action action)
        {
            later.Enqueue(action);
            return true;
        }

        void Refresh()
        {
            Assert.False(box.InSelectionChanged, "El panel refrescó dentro del SelectionChanged del desplegable: WinUI puede cerrar la app.");
            (ChoiceList<T> next, int nextSelected) = build(state);
            shown = Show(box, shown, next, nextSelected, ref updating, retry);
        }

        box.SelectionChanged = () => ComboChoices.Choose(shown, box.SelectedIndex, updating, value => () =>
        {
            applied++;
            state = value;
            Refresh();
        }, Enqueue, _ => false);
        box.DropDownClosed = () => retry.Closed(Enqueue, Refresh, _ => false);

        int chosen = 0;
        for (int index = 0; index < shown.Count; index++)
        {
            if (choosable is not null && !choosable(shown.Values[index]))
            {
                continue;
            }

            chosen++;
            // Elegir la que ya está elegida no lanza SelectionChanged: no hay nada que aplicar.
            int before = applied;
            int expected = before + (box.SelectedIndex == index ? 0 : 1);
            box.IsDropDownOpen = true;
            box.Pick(index);
            Assert.True(applied == before, "La elección se aplicó dentro del SelectionChanged.");
            Drain(later);
            box.Close();
            Drain(later);
            Assert.Equal(expected, applied);
            Assert.False(retry.Pending);

            (ChoiceList<T> next, int nextSelected) = build(state);
            Assert.Equal(index, nextSelected);

            // Un refresco con la lista abierta (por ejemplo, al reproducir) no toca nada.
            box.IsDropDownOpen = true;
            Assert.Same(shown, Show(box, shown, next, nextSelected, ref updating));
            box.IsDropDownOpen = false;

            shown = Show(box, shown, next, nextSelected, ref updating);
            Assert.True(rebuilds == box.Rebuilds, $"Elegir «{shown.Labels[index]}» y refrescar no debe rehacer el desplegable.");
            Assert.Equal(index, box.SelectedIndex);
            Assert.Equal(shown.Count, box.Items.Count);
        }

        return chosen;
    }

    /// <summary>Hace lo que quedó en la cola falsa, como el hilo de la interfaz al terminar el evento.</summary>
    public static void Drain(Queue<Action> later)
    {
        while (later.TryDequeue(out Action? action))
        {
            action();
        }
    }

    /// <summary>Lo que hace la vista al refrescar: <see cref="ComboChoices.Show{T, TBox}"/> con su bandera de «actualizando».</summary>
    public static ChoiceList<T> Show<T>(FakeChoiceBox box, ChoiceList<T> shown, ChoiceList<T> next, int selected, ref bool updating,
        ClosedListRetry? retry = null)
    {
        updating = true;
        try
        {
            return ComboChoices.Show(box, shown, next, selected, retry: retry);
        }
        finally
        {
            updating = false;
        }
    }
}
