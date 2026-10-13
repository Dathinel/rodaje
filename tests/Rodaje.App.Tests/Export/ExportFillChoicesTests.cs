using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Inspector;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Tarea 112: <c>ExportView.Fill</c> repetía a mano lo que hace <c>ComboBoxChoices.Show</c> y, al rehacer, vaciaba los
/// elementos sin quitar antes la elección. Ahora arma listas de solo textos (<see cref="ComboChoices.FromLabels"/>) y usa
/// la misma decisión que los demás desplegables. Aquí, con un desplegable falso, lo que hace <c>Fill</c>.
/// </summary>
public class ExportFillChoicesTests
{
    /// <summary>Lo que hace <c>Fill</c>: lo que muestra el desplegable (sus textos) contra los textos nuevos.</summary>
    private static void Fill(FakeChoiceBox box, IEnumerable<string> labels, int selected)
    {
        ChoiceList<string> shown = ComboChoices.FromLabels(box.Items.Cast<string>());
        ChoiceList<string> next = ComboChoices.FromLabels(labels);
        _ = ComboChoices.Show(box, shown, next, selected);
    }

    [Fact]
    public void ListaDeSoloTextos_CadaElementoEsSuTexto()
    {
        ChoiceList<string> list = ComboChoices.FromLabels(["MP4", "MOV"]);
        Assert.Equal(new[] { "MP4", "MOV" }, list.Labels);
        Assert.Equal(new[] { "MP4", "MOV" }, list.Values);
        Assert.False(ComboChoices.FromLabels(["MP4", "MOV"]).Differs(list));
        Assert.True(ComboChoices.FromLabels(["MP4"]).Differs(list));
    }

    [Fact]
    public void MismasOpciones_SoloMueveLaEleccion()
    {
        var box = new FakeChoiceBox();
        Fill(box, ["MP4", "MOV", "MKV"], 0);
        int rebuilds = box.Rebuilds;

        Fill(box, ["MP4", "MOV", "MKV"], 2);
        Assert.Equal(rebuilds, box.Rebuilds);
        Assert.Equal(2, box.SelectedIndex);
    }

    [Fact]
    public void OpcionesNuevas_QuitaLaEleccionAntesDeVaciar()
    {
        // El caso de la revisión: vaciar con un elemento elegido manda un SelectionChanged a medio vaciar. Ahora la elección
        // pasa a −1 con los elementos todavía puestos, y después se vacía.
        var box = new FakeChoiceBox();
        Fill(box, ["Toda la secuencia (0:10)", "Entre marcas"], 1);
        var itemsWhenUnselected = new List<int>();
        box.SelectionChanged = () =>
        {
            if (box.SelectedIndex == -1)
            {
                itemsWhenUnselected.Add(box.Items.Count);
            }
        };

        Fill(box, ["Toda la secuencia (0:09)", "Entre marcas"], 0);
        Assert.Equal(new[] { 2 }, itemsWhenUnselected);
        Assert.Equal(new object[] { "Toda la secuencia (0:09)", "Entre marcas" }, box.Items);
        Assert.Equal(0, box.SelectedIndex);
    }

    [Fact]
    public void ConLaListaAbierta_NoSeTocaNada()
    {
        var box = new FakeChoiceBox();
        Fill(box, ["MP4", "MOV"], 0);
        box.IsDropDownOpen = true;

        Fill(box, ["MP4"], 1);
        Assert.Equal(new object[] { "MP4", "MOV" }, box.Items);
        Assert.Equal(0, box.SelectedIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(5)]
    public void EleccionFueraDeLaLista_NoEligeNada(int selected)
    {
        var box = new FakeChoiceBox();
        Fill(box, ["MP4", "MOV"], 0);
        Fill(box, ["MP4", "MOV"], selected);
        Assert.Equal(-1, box.SelectedIndex);
    }
}
