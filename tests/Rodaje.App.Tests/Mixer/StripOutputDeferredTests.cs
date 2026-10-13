using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Mixer;

/// <summary>
/// Tarea 112: la salida elegida en una tira se aplica después del evento (<c>StripView.ChooseOutput</c>). Si entre el evento y
/// el despacho se quitó el bus, antes no se hacía nada y el desplegable seguía mostrando la salida rechazada; ahora la tira se
/// refresca, como hace Máscaras con su destino. La decisión es <see cref="DeferredChoices.StripOutput"/>.
/// </summary>
public class StripOutputDeferredTests
{
    private static AudioBus Bus(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void LaMezclaOUnBusQueSigue_SeAplica()
    {
        AudioBus[] buses = [Bus("b1", "Voces"), Bus("b2", "Música")];
        Assert.Equal(DeferredOutcome.Apply, DeferredChoices.StripOutput(buses, null));
        Assert.Equal(DeferredOutcome.Apply, DeferredChoices.StripOutput(buses, "b2"));
    }

    [Fact]
    public void UnBusQuitado_SeRefresca()
    {
        Assert.Equal(DeferredOutcome.Refresh, DeferredChoices.StripOutput([Bus("b1", "Voces")], "b2"));
        Assert.Equal(DeferredOutcome.Refresh, DeferredChoices.StripOutput([], "b1"));
    }

    [Fact]
    public void BusQuitadoAntesDelDespacho_ElDesplegableVuelveALaSalidaQueTiene()
    {
        // La tira sale a la mezcla; la persona elige «Música» y, antes del despacho, se quita ese bus.
        var box = new FakeChoiceBox();
        AudioBus[] buses = [Bus("b1", "Voces"), Bus("b2", "Música")];
        string? output = null;
        (ChoiceList<string?> list, int selected) = StripOutputChoices.Build(buses, output);
        ChoiceList<string?> shown = ComboChoices.Show(box, new ChoiceList<string?>(), list, selected);
        string? picked = "sin elegir";
        box.SelectionChanged = () =>
        {
            if (shown.TryGet(box.SelectedIndex, out string? busId))
            {
                picked = busId;
            }
        };
        box.IsDropDownOpen = true;
        box.Pick(2);
        Assert.Equal("b2", picked);
        box.SelectionChanged = null;

        buses = [Bus("b1", "Voces")];
        DeferredOutcome outcome = DeferredChoices.StripOutput(buses, picked);
        Assert.Equal(DeferredOutcome.Refresh, outcome);

        // El refresco de la tira (Update) vuelve a armar la lista sin el bus y elige la salida que tiene: la mezcla.
        (ChoiceList<string?> next, int nextSelected) = StripOutputChoices.Build(buses, output);
        shown = ComboChoices.Show(box, shown, next, nextSelected);
        Assert.Equal(0, box.SelectedIndex);
        Assert.True(shown.TryGet(box.SelectedIndex, out string? showing));
        Assert.Null(showing);
        Assert.Equal(new[] { "Mezcla", "Voces" }, shown.Labels);
    }
}
