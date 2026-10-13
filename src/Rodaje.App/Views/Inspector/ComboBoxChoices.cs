using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Views.Inspector;

/// <summary>Muestra una <see cref="ChoiceList{T}"/> en un <c>ComboBox</c> sin rehacerlo dentro de su propio evento.</summary>
/// <remarks>
/// Cambiar los elementos de un desplegable de WinUI dentro de su propio <c>SelectionChanged</c> o con la lista abierta falla
/// en código nativo y cierra la app sin rastro (tarea 91). Aquí los elementos solo se rehacen si cambió la lista, nunca con
/// la lista abierta, y la selección solo se toca si es otra. La decisión es <see cref="ComboChoices.Show{T, TBox}"/>, que las
/// pruebas ejercen sin WinUI.
/// </remarks>
internal static class ComboBoxChoices
{
    /// <summary>
    /// Muestra <paramref name="next"/> con <paramref name="selected"/> elegido y devuelve lo que muestra ahora el desplegable:
    /// <paramref name="shown"/> si estaba abierto (no se toca nada) o no hacía falta rehacerlo.
    /// </summary>
    /// <param name="item">El elemento de cada posición; por omisión, su texto.</param>
    /// <param name="retry">Si se da, lo que no se tocó por tener la lista abierta se repite al cerrarla (ver <see cref="DispatcherQueueLater.RetryWhenClosed"/>).</param>
    public static ChoiceList<T> Show<T>(ComboBox box, ChoiceList<T> shown, ChoiceList<T> next, int selected, Func<int, object>? item = null,
        ClosedListRetry? retry = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        return ComboChoices.Show(new Box(box), shown, next, selected, item, retry);
    }

    /// <summary>Un <c>ComboBox</c> de WinUI como <see cref="IChoiceBox"/>.</summary>
    private sealed class Box(ComboBox box) : IChoiceBox
    {
        public bool IsDropDownOpen => box.IsDropDownOpen;

        public int SelectedIndex
        {
            get => box.SelectedIndex;
            set => box.SelectedIndex = value;
        }

        public void ClearItems() => box.Items.Clear();

        public void AddItem(object item) => box.Items.Add(item);
    }
}
