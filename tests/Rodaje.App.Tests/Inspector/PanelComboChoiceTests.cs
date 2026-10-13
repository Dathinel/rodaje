using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Model;
using Rodaje.Core.Effects;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tarea 100: los desplegables de Seguimiento, Dinámica, Filtros (LUT), Transición y Texto (estilo) se rehacían en cada
/// refresco, también dentro de su propio <c>SelectionChanged</c> y con la lista abierta (Dinámica, en cada cuadro al
/// reproducir), lo que cierra la app desde WinUI (tarea 91). Aquí, sin ventana, la decisión de la vista: elegir cualquier
/// opción y volver a armar la lista no la rehace, con la lista abierta no se toca, y los índices que manda WinUI al vaciar o
/// rehacer no eligen nada.
/// </summary>
public class PanelComboChoiceTests
{
    private static ChoiceList<T> List<T>(params (string Label, T Value)[] items)
    {
        var list = new ChoiceList<T>();
        foreach ((string label, T value) in items)
        {
            list.Add(label, value);
        }

        return list;
    }

    [Fact]
    public void ListaIgual_SoloElige_YAbiertaNoSeToca()
    {
        ChoiceList<string?> shown = List<string?>(("Su propio audio", null), ("Pista «A2»", "t2"));
        ChoiceList<string?> next = List<string?>(("Su propio audio", null), ("Pista «A2»", "t2"));
        Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, next, isDropDownOpen: false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, next, isDropDownOpen: true));

        ChoiceList<string?> renamed = List<string?>(("Su propio audio", null), ("Pista «Voz»", "t2"));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, renamed, isDropDownOpen: false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, renamed, isDropDownOpen: true));
    }

    [Fact]
    public void MismoTexto_OtroValor_SeRehace()
    {
        // Dos clips con el mismo nombre que cambian de lugar: el texto no basta.
        ChoiceList<string?> shown = List<string?>(("Clip (V2)", "a"), ("Clip (V2)", "b"));
        ChoiceList<string?> next = List<string?>(("Clip (V2)", "b"), ("Clip (V2)", "a"));
        Assert.True(next.Differs(shown));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, next, false));
    }

    [Fact]
    public void Vacia_YPrimeraVez()
    {
        Assert.False(new ChoiceList<int>().Differs(new ChoiceList<int>()));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(new ChoiceList<int>(), List(("Sin compresor", -1)), false));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(List(("Sin compresor", -1)), new ChoiceList<int>(), false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void IndicesFueraDeLaLista_NoEligenNada(int index)
    {
        ChoiceList<string?> list = List<string?>(("Sin LUT", ""), ("Cargar .cube…", null));
        Assert.False(list.TryGet(index, out _));
        Assert.False(new ChoiceList<TrackUse>().TryGet(index, out _));
    }

    [Fact]
    public void ValorNulo_SeElige()
    {
        // «Cargar .cube…» y «Su propio audio» representan nulo: elegirlos no es «nada elegido».
        ChoiceList<string?> list = List<string?>(("Sin LUT", ""), ("Cargar .cube…", null));
        Assert.True(list.TryGet(1, out string? value));
        Assert.Null(value);
        Assert.Equal(1, list.IndexOf(null));
        Assert.True(list.Contains(null));
    }

    [Fact]
    public void Dinamica_ElegirCadaDestino_NoRehaceLaLista()
    {
        // Refresh corría en cada cuadro al reproducir y dentro del SelectionChanged del destino.
        var sequence = new Sequence { Id = "s" };
        DynamicsOwner[] owners =
        [
            new(DynamicsOwnerKind.Clip, "c1"), new(DynamicsOwnerKind.Track, "a1"), new(DynamicsOwnerKind.Bus, "b1"), DynamicsOwner.Mix(sequence),
        ];
        ChoiceList<DynamicsOwner> Build()
        {
            var list = new ChoiceList<DynamicsOwner>();
            foreach (DynamicsOwner owner in owners)
            {
                // Otra instancia igual en cada refresco, como en la vista.
                list.Add(owner.Kind + " " + owner.Id, owner with { });
            }

            return list;
        }

        ChoiceList<DynamicsOwner> shown = Build();
        for (int index = 0; index < shown.Count; index++)
        {
            Assert.True(shown.TryGet(index, out DynamicsOwner chosen));
            ChoiceList<DynamicsOwner> next = Build();
            Assert.Equal(index, next.IndexOf(chosen));
            Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, next, false));
            Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, next, true));
        }
    }

    [Fact]
    public void Dinamica_Preajustes_SiempreLaMismaLista()
    {
        static ChoiceList<int> Build()
        {
            var list = new ChoiceList<int>();
            list.Add("Sin compresor", -1);
            for (int i = 0; i < AudioPresets.All.Count; i++)
            {
                list.Add(AudioPresets.All[i].Name, i);
            }

            list.Add("Personalizado", -2);
            return list;
        }

        ChoiceList<int> shown = Build();
        Assert.Equal(AudioPresets.All.Count + 2, shown.Count);
        for (int index = 0; index < shown.Count; index++)
        {
            Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, Build(), false));
        }
    }

    [Fact]
    public void Dinamica_SidechainQueYaNoEsta_ElegirOtraRehaceLaLista()
    {
        // Por eso el cambio de sidechain va después del evento: la lista cambia al elegir.
        ChoiceList<string?> shown = List<string?>(("Su propio audio", null), ("Pista «A2»", "t2"), ("Una pista que ya no está", "gone"));
        ChoiceList<string?> next = List<string?>(("Su propio audio", null), ("Pista «A2»", "t2"));
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, next, false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, next, true));
    }

    [Fact]
    public void Transicion_ElegirOtroTipo_NoRehace_SalvoDesdeUnPaqueteQueFalta()
    {
        (string Type, string Preset)[] types = [("crossfade", ""), ("dip", ""), ("wipe", ""), ("preset", "pkg/item")];
        ChoiceList<(string Type, string Preset)> Build((string Type, string Preset) current)
        {
            var list = new ChoiceList<(string Type, string Preset)>();
            foreach ((string type, string preset) in types)
            {
                list.Add(type + preset, (type, preset));
            }

            if (list.IndexOf(current) < 0)
            {
                list.Add(current.Preset + " (falta el paquete)", current);
            }

            return list;
        }

        ChoiceList<(string Type, string Preset)> shown = Build(types[0]);
        for (int index = 0; index < shown.Count; index++)
        {
            Assert.True(shown.TryGet(index, out (string Type, string Preset) chosen));
            ChoiceList<(string Type, string Preset)> next = Build(chosen);
            Assert.Equal(index, next.IndexOf(chosen));
            Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, next, false));
        }

        ChoiceList<(string Type, string Preset)> missing = Build(("preset", "otro/falta"));
        Assert.Equal(types.Length + 1, missing.Count);
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(missing, Build(types[1]), false));
    }

    [Fact]
    public void Lut_ElegirUnaInstalada_NoRehace_YCargarUnArchivoSi()
    {
        ChoiceList<string?> Build(string current)
        {
            var list = new ChoiceList<string?>();
            list.Add("Sin LUT", "");
            list.Add("Cine · Paquete", "pkg/cine");
            list.Add("Frío · Paquete", "pkg/frio");
            if (current.Length > 0 && !list.Contains(current))
            {
                list.Add(current + " (archivo)", current);
            }

            list.Add("Cargar .cube…", null);
            return list;
        }

        ChoiceList<string?> shown = Build("");
        foreach (string current in (string[])["", "pkg/cine", "pkg/frio"])
        {
            Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, Build(current), false));
        }

        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, Build("file:mio.cube"), false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, Build("file:mio.cube"), true));
    }

    [Theory]
    [InlineData(PointTrackKind.Point)]
    [InlineData(PointTrackKind.Two)]
    [InlineData(PointTrackKind.Corners)]
    [InlineData(PointTrackKind.Region)]
    public void Seguimiento_ElegirCadaUso_NoRehaceLaLista(PointTrackKind kind)
    {
        ChoiceList<TrackUse> Build()
        {
            var list = new ChoiceList<TrackUse>();
            foreach (TrackUse use in Enum.GetValues<TrackUse>())
            {
                if (TrackApply.Supports(kind, use))
                {
                    list.Add(use.ToString(), use);
                }
            }

            return list;
        }

        ChoiceList<TrackUse> shown = Build();
        Assert.True(shown.Count > 0);
        for (int index = 0; index < shown.Count; index++)
        {
            Assert.True(shown.TryGet(index, out TrackUse use));
            ChoiceList<TrackUse> next = Build();
            Assert.Equal(index, next.IndexOf(use));
            Assert.Equal(ComboUpdate.Select, ComboChoices.Update(shown, next, false));
        }
    }
}
