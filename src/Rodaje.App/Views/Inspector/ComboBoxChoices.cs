using Microsoft.UI.Xaml.Controls;
using Rodaje.App.Services.Inspector;

namespace Rodaje.App.Views.Inspector;

/// <summary>Muestra una <see cref="ChoiceList{T}"/> en un <c>ComboBox</c> sin rehacerlo dentro de su propio evento.</summary>
/// <remarks>
/// Cambiar los elementos de un desplegable de WinUI dentro de su propio <c>SelectionChanged</c> o con la lista abierta falla
/// en código nativo y cierra la app sin rastro (tarea 91). Aquí los elementos solo se rehacen si cambió la lista, nunca con
/// la lista abierta, y la selección solo se toca si es otra.
/// </remarks>
internal static class ComboBoxChoices
{
    /// <summary>
    /// Muestra <paramref name="next"/> con <paramref name="selected"/> elegido y devuelve lo que muestra ahora el desplegable:
    /// <paramref name="shown"/> si estaba abierto (no se toca nada) o no hacía falta rehacerlo.
    /// </summary>
    /// <param name="item">El elemento de cada posición; por omisión, su texto.</param>
    public static ChoiceList<T> Show<T>(ComboBox box, ChoiceList<T> shown, ChoiceList<T> next, int selected, Func<int, object>? item = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(next);
        if (box.IsDropDownOpen)
        {
            return shown;
        }

        if (next.Differs(shown))
        {
            box.SelectedIndex = -1;
            box.Items.Clear();
            for (int i = 0; i < next.Count; i++)
            {
                box.Items.Add(item?.Invoke(i) ?? next.Labels[i]);
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
}
