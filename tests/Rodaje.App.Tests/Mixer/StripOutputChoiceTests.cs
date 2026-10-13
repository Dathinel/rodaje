using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Mixer;

/// <summary>
/// Tarea 101: el desplegable «Salida» de cada tira del mezclador se vaciaba y volvía a llenar en cada refresco (al mover un
/// fader, al reproducir y dentro de su propio <c>SelectionChanged</c>, porque elegir una salida edita y refresca la tira),
/// el mismo patrón que cerraba la app en la tarea 91. Ahora la tira arma una <see cref="ChoiceList{T}"/> (la mezcla como nulo
/// y cada bus por su id) y solo la rehace si cambió. Aquí, sin ventana, la decisión que toma la vista con esa lista.
/// </summary>
public class StripOutputChoiceTests
{
    /// <summary>La lista que arma <c>StripView.UpdateOutput</c>, con la misma función.</summary>
    private static ChoiceList<string?> Outputs(params AudioBus[] buses) => StripOutputChoices.Build(buses, null).List;

    /// <summary>La salida que muestra la vista para <paramref name="output"/>.</summary>
    private static int Selected(AudioBus[] buses, string? output) => StripOutputChoices.Build(buses, output).Selected;

    private static AudioBus Bus(string id, string name) => new() { Id = id, Name = name };

    [Fact]
    public void RefrescoSinCambiosDeBuses_NoRehaceLaLista()
    {
        ChoiceList<string?> shown = Outputs(Bus("b1", "Voces"), Bus("b2", "Música"));

        // Silenciar o poner solo un bus, o tocar sus inserciones, refresca el mezclador pero no cambia la salida.
        AudioBus voces = Bus("b1", "Voces");
        voces.Muted = true;
        voces.Solo = true;
        ChoiceList<string?> next = Outputs(voces, Bus("b2", "Música"));

        Assert.False(next.Differs(shown));
        Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, next, isDropDownOpen: false));
    }

    [Fact]
    public void ConLaListaAbierta_NoSeToca()
    {
        ChoiceList<string?> shown = Outputs(Bus("b1", "Voces"));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, Outputs(Bus("b1", "Voces")), isDropDownOpen: true));

        // Ni siquiera si se añadió un bus mientras estaba abierta: el próximo refresco con la lista cerrada la pone al día.
        ChoiceList<string?> added = Outputs(Bus("b1", "Voces"), Bus("b2", "Música"));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, added, isDropDownOpen: true));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, added, isDropDownOpen: false));
    }

    [Fact]
    public void BusAnadidoQuitadoRenombradoOReordenado_SeRehace()
    {
        ChoiceList<string?> shown = Outputs(Bus("b1", "Voces"), Bus("b2", "Música"));
        Assert.True(Outputs(Bus("b1", "Voces")).Differs(shown));
        Assert.True(Outputs(Bus("b1", "Voces"), Bus("b2", "Música"), Bus("b3", "Efectos")).Differs(shown));
        Assert.True(Outputs(Bus("b1", "Diálogos"), Bus("b2", "Música")).Differs(shown));
        Assert.True(Outputs(Bus("b2", "Música"), Bus("b1", "Voces")).Differs(shown));
    }

    [Fact]
    public void DosBusesConElMismoNombre_SeDistinguenPorSuId()
    {
        // Se quita un «Bus» y se crea otro con el mismo nombre: el texto no basta, la elección lleva al bus nuevo.
        ChoiceList<string?> shown = Outputs(Bus("b1", "Bus"));
        ChoiceList<string?> next = Outputs(Bus("b2", "Bus"));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, next, isDropDownOpen: false));
        Assert.True(next.TryGet(1, out string? busId));
        Assert.Equal("b2", busId);
    }

    [Fact]
    public void ElegirLaMezcla_EsElegirNulo()
    {
        ChoiceList<string?> list = Outputs(Bus("b1", "Voces"));
        Assert.True(list.TryGet(0, out string? mix));
        Assert.Null(mix);
        Assert.True(list.TryGet(1, out string? bus));
        Assert.Equal("b1", bus);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void IndicesQueMandaWinUIAlVaciarORehacer_NoEligenNada(int index)
    {
        // Al rehacer, el desplegable pasa por −1; un índice viejo puede quedar fuera de la lista nueva.
        Assert.False(Outputs(Bus("b1", "Voces")).TryGet(index, out _));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("b1", 1)]
    [InlineData("b2", 2)]
    [InlineData("quitado", 0)]
    public void SalidaMostrada_LaDelBusOLaMezcla(string? output, int expected)
    {
        Assert.Equal(expected, Selected([Bus("b1", "Voces"), Bus("b2", "Música")], output));
    }

    [Fact]
    public void ElegirUnBusYRefrescar_SoloMueveLaEleccion()
    {
        // Lo que pasa al elegir «Música»: la edición cambia la salida de la pista y el mezclador refresca la tira con los
        // mismos buses. La lista mostrada sigue siendo la misma; solo cambia la elección.
        ChoiceList<string?> shown = Outputs(Bus("b1", "Voces"), Bus("b2", "Música"));
        Assert.True(shown.TryGet(2, out string? chosen));

        ChoiceList<string?> afterEdit = Outputs(Bus("b1", "Voces"), Bus("b2", "Música"));
        Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, afterEdit, isDropDownOpen: false));
        Assert.Equal(2, afterEdit.IndexOf(chosen));
        Assert.Equal(2, Selected([Bus("b1", "Voces"), Bus("b2", "Música")], chosen));
    }

    [Fact]
    public void ElegirCadaSalida_NoRehaceLaLista()
    {
        // La misma función y la misma decisión que la tira, en un desplegable que falla como WinUI si se rehace dentro de su
        // SelectionChanged o abierto.
        AudioBus[] buses = [Bus("b1", "Voces"), Bus("b2", "Música"), Bus("b3", "Voces")];
        Assert.Equal(4, ComboScenario.ChooseEach(output => StripOutputChoices.Build([.. buses.Select(b => Bus(b.Id, b.Name))], output), (string?)null));
    }

    [Fact]
    public void LaSalidaElegida_SoloSeAplicaSiElBusSigue()
    {
        AudioBus[] buses = [Bus("b1", "Voces")];
        Assert.True(StripOutputChoices.StillThere(buses, null));
        Assert.True(StripOutputChoices.StillThere(buses, "b1"));
        Assert.False(StripOutputChoices.StillThere(buses, "b2"));
        Assert.False(StripOutputChoices.StillThere([], "b1"));
    }
}
