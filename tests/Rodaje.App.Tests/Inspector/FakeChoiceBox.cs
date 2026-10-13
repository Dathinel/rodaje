using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Un desplegable falso que se porta como el <c>ComboBox</c> de WinUI en lo que importa para la tarea 91: cambiar la
/// selección lanza <see cref="SelectionChanged"/> en el acto, y rehacer los elementos dentro de ese evento o con la lista
/// abierta falla (en WinUI, en código nativo y sin rastro; aquí, con una excepción).
/// </summary>
internal sealed class FakeChoiceBox : IChoiceBox
{
    private int _selected = -1;
    private bool _inSelectionChanged;

    public List<object> Items { get; } = [];

    public bool IsDropDownOpen { get; set; }

    /// <summary>Cuántas veces se vaciaron los elementos para rehacerlos.</summary>
    public int Rebuilds { get; private set; }

    /// <summary>Lo que hace la vista al cambiar la selección (el <c>SelectionChanged</c> del desplegable).</summary>
    public Action? SelectionChanged { get; set; }

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

    /// <summary>La persona elige el elemento <paramref name="index"/> (con la lista abierta, que se cierra al elegir).</summary>
    public void Pick(int index)
    {
        IsDropDownOpen = false;
        SelectedIndex = index;
    }

    private void Raise()
    {
        _inSelectionChanged = true;
        try
        {
            SelectionChanged?.Invoke();
        }
        finally
        {
            _inSelectionChanged = false;
        }
    }

    private void Guard()
    {
        if (_inSelectionChanged)
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
    /// Muestra la lista que arma <paramref name="build"/> para <paramref name="initial"/> y elige cada opción como la persona:
    /// el <c>SelectionChanged</c> solo apunta la elección, que se aplica después del evento (como <c>Later</c>), y el refresco
    /// vuelve a armar la lista con lo elegido y la muestra. Comprueba que la lista no se rehace, que queda elegida esa opción y
    /// que con la lista abierta no se toca nada.
    /// </summary>
    /// <param name="build">La función de la vista: la lista y la posición elegida para lo que muestra el panel.</param>
    /// <param name="choosable">Qué opciones se pueden elegir sin salir del panel (por omisión, todas).</param>
    /// <returns>Cuántas opciones se eligieron.</returns>
    public static int ChooseEach<T>(Func<T, (ChoiceList<T> List, int Selected)> build, T initial, Func<T, bool>? choosable = null)
    {
        var box = new FakeChoiceBox();
        bool updating = false;
        T state = initial;
        var later = new Queue<Action>();
        (ChoiceList<T> list, int selected) = build(state);
        ChoiceList<T> shown = Show(box, new ChoiceList<T>(), list, selected, ref updating);
        Assert.Same(list, shown);
        int rebuilds = box.Rebuilds;
        box.SelectionChanged = () =>
        {
            if (!updating && shown.TryGet(box.SelectedIndex, out T value))
            {
                later.Enqueue(() => state = value);
            }
        };

        int chosen = 0;
        for (int index = 0; index < shown.Count; index++)
        {
            if (choosable is not null && !choosable(shown.Values[index]))
            {
                continue;
            }

            chosen++;
            box.IsDropDownOpen = true;
            box.Pick(index);
            while (later.TryDequeue(out Action? apply))
            {
                apply();
            }

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

    /// <summary>Lo que hace la vista al refrescar: <see cref="ComboChoices.Show{T, TBox}"/> con su bandera de «actualizando».</summary>
    public static ChoiceList<T> Show<T>(FakeChoiceBox box, ChoiceList<T> shown, ChoiceList<T> next, int selected, ref bool updating)
    {
        updating = true;
        try
        {
            return ComboChoices.Show(box, shown, next, selected);
        }
        finally
        {
            updating = false;
        }
    }
}
