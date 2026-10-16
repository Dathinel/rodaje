using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Packages;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tareas 100 y 103: los desplegables de Dinámica, Transición, Filtros (LUT), Seguimiento, Texto y Máscaras se rehacían en
/// cada refresco, también dentro de su propio <c>SelectionChanged</c> y con la lista abierta, lo que cierra la app desde
/// WinUI (tarea 91). Aquí, sin ventana, cada lista se arma con la misma función que usa la vista
/// (<see cref="DynamicsChoices"/>, <see cref="TransitionChoices"/>…) y se muestra con la misma decisión
/// (<see cref="ComboChoices.Show{T, TBox}"/>) en un desplegable falso que falla como WinUI: elegir cualquier opción y
/// refrescar no la rehace, con la lista abierta no se toca, y los índices que manda WinUI al vaciar o rehacer no eligen nada.
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

    // --- La decisión común ---

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
    public void Show_RehaceSoloSiCambio_YNuncaAbierto()
    {
        var box = new FakeChoiceBox();
        bool updating = false;
        ChoiceList<string?> first = List<string?>(("Mezcla", null), ("Voces", "b1"));
        ChoiceList<string?> shown = ComboScenario.Show(box, new ChoiceList<string?>(), first, 1, ref updating);
        Assert.Equal(1, box.Rebuilds);
        Assert.Equal(["Mezcla", "Voces"], box.Items);
        Assert.Equal(1, box.SelectedIndex);

        // La misma lista, otra instancia: solo cambia la elección.
        shown = ComboScenario.Show(box, shown, List<string?>(("Mezcla", null), ("Voces", "b1")), 0, ref updating);
        Assert.Equal(1, box.Rebuilds);
        Assert.Equal(0, box.SelectedIndex);

        // Abierta: ni elementos ni elección.
        box.IsDropDownOpen = true;
        ChoiceList<string?> added = List<string?>(("Mezcla", null), ("Voces", "b1"), ("Música", "b2"));
        Assert.Same(shown, ComboScenario.Show(box, shown, added, 2, ref updating));
        Assert.Equal(0, box.SelectedIndex);

        // Cerrada: se rehace, y una elección fuera de la lista queda en −1.
        box.IsDropDownOpen = false;
        shown = ComboScenario.Show(box, shown, added, 7, ref updating);
        Assert.Same(added, shown);
        Assert.Equal(2, box.Rebuilds);
        Assert.Equal(-1, box.SelectedIndex);
    }

    [Fact]
    public void ElDesplegableFalso_FallaComoWinUI_SiSeRehaceDentroDeSuEvento()
    {
        // Lo que hacían los paneles antes: el SelectionChanged editaba y refrescaba en el acto con una lista que cambió.
        var box = new FakeChoiceBox();
        bool updating = false;
        ChoiceList<string?> shown = ComboScenario.Show(box, new ChoiceList<string?>(),
            List<string?>(("Su propio audio", null), ("Pista «A2»", "t2"), ("Una pista que ya no está", "gone")), 2, ref updating);
        box.SelectionChanged = () => shown = ComboScenario.Show(box, shown,
            List<string?>(("Su propio audio", null), ("Pista «A2»", "t2")), box.SelectedIndex, ref updating);
        Assert.Throws<InvalidOperationException>(() => box.Pick(1));
    }

    // --- Dinámica ---

    private static Project AudioProject(out Sequence sequence)
    {
        var a1 = new Track { Id = "a1", Kind = TrackKind.Audio, Name = "A1" };
        a1.Clips.Add(new Clip { Id = "c1", MediaId = "m1", Start = Time(0), SourceIn = Time(0), SourceOut = Time(5) });
        var a2 = new Track { Id = "a2", Kind = TrackKind.Audio, Name = "A2" };
        sequence = new Sequence { Id = "s" };
        sequence.AudioTracks.Add(a1);
        sequence.AudioTracks.Add(a2);
        sequence.AudioBuses.Add(new AudioBus { Id = "b1", Name = "Voces" });
        var project = new Project { Id = "p", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m1", Path = "voz.wav", Name = "voz.wav", HasAudio = true });
        return project;
    }

    private static Rodaje.Core.Timing.Time Time(double seconds) => Rodaje.Core.Timing.Time.FromSeconds(seconds);

    [Theory]
    [InlineData("c1")]
    [InlineData(null)]
    public void Dinamica_ElegirCadaDestino_NoRehaceLaLista(string? clip)
    {
        // Refresh corre en cada cuadro al reproducir y después del SelectionChanged del destino.
        Project project = AudioProject(out Sequence sequence);
        int chosen = ComboScenario.ChooseEach(
            owner =>
            {
                (ChoiceList<DynamicsOwner> list, DynamicsOwner shown) = DynamicsChoices.Targets(project, sequence, clip, owner);
                return (list, list.IndexOf(shown));
            },
            DynamicsChoices.Targets(project, sequence, clip, null).Chosen);
        Assert.Equal(clip is null ? 4 : 5, chosen);
    }

    [Fact]
    public void Dinamica_Destinos_TextoYDuenoPorOmision()
    {
        Project project = AudioProject(out Sequence sequence);
        (ChoiceList<DynamicsOwner> list, DynamicsOwner chosen) = DynamicsChoices.Targets(project, sequence, "c1", null);
        Assert.Equal(["Clip «voz.wav»", "Pista «A1»", "Pista «A2»", "Bus «Voces»", "Mezcla"], list.Labels);
        Assert.Equal(new DynamicsOwner(DynamicsOwnerKind.Clip, "c1"), chosen);

        // Sin clip, la mezcla; un dueño que ya no está tampoco se queda.
        Assert.Equal(DynamicsOwner.Mix(sequence), DynamicsChoices.Targets(project, sequence, null, null).Chosen);
        Assert.Equal(DynamicsOwner.Mix(sequence), DynamicsChoices.Targets(project, sequence, null, new DynamicsOwner(DynamicsOwnerKind.Track, "quitada")).Chosen);
        Assert.Equal(new DynamicsOwner(DynamicsOwnerKind.Bus, "b1"),
            DynamicsChoices.Targets(project, sequence, "c1", new DynamicsOwner(DynamicsOwnerKind.Bus, "b1")).Chosen);
    }

    [Fact]
    public void Dinamica_ElegirCadaPreajuste_NoRehaceLaLista()
    {
        // «Personalizado» (−2) no se puede elegir: solo dice que el compresor no es ningún preajuste.
        int chosen = ComboScenario.ChooseEach(
            index => DynamicsChoices.Presets(index != -1, index >= 0 ? AudioPresets.All[index] : null),
            -1,
            index => index != -2);
        Assert.Equal(AudioPresets.All.Count + 1, chosen);
    }

    [Fact]
    public void Dinamica_Preajustes_LaEleccion()
    {
        (ChoiceList<int> list, int none) = DynamicsChoices.Presets(false, null);
        Assert.Equal(AudioPresets.All.Count + 2, list.Count);
        Assert.Equal((-1, -2), (list.Values[0], list.Values[^1]));
        Assert.Equal(0, none);
        Assert.Equal(list.Count - 1, DynamicsChoices.Presets(true, null).Selected);
        Assert.Equal(AudioPresets.All.Count, DynamicsChoices.Presets(true, AudioPresets.All[^1]).Selected);
    }

    [Fact]
    public void Dinamica_ElegirCadaSidechain_NoRehaceLaLista()
    {
        Project project = AudioProject(out Sequence sequence);
        Assert.NotNull(project);
        Assert.Equal(3, ComboScenario.ChooseEach(key => DynamicsChoices.Sidechains(sequence.AudioTracks, key), (string?)null));
    }

    [Fact]
    public void Dinamica_SidechainQueYaNoEsta_SeMuestra_YElegirOtroRehaceDespuesDelEvento()
    {
        Project project = AudioProject(out Sequence sequence);
        Assert.NotNull(project);
        (ChoiceList<string?> list, int selected) = DynamicsChoices.Sidechains(sequence.AudioTracks, "gone");
        Assert.Equal(["Su propio audio", "Pista «A1»", "Pista «A2»", "Una pista que ya no está"], list.Labels);
        Assert.Equal(3, selected);
        Assert.Equal(0, DynamicsChoices.Sidechains(sequence.AudioTracks, "").Selected);

        // Elegir «Pista «A2»» quita «Una pista que ya no está»: el refresco rehace la lista, por eso va después del evento.
        var box = new FakeChoiceBox();
        bool updating = false;
        string? state = "gone";
        var later = new Queue<Action>();
        ChoiceList<string?> shown = ComboScenario.Show(box, new ChoiceList<string?>(), list, selected, ref updating);
        box.SelectionChanged = () =>
        {
            if (!updating && shown.TryGet(box.SelectedIndex, out string? key))
            {
                later.Enqueue(() => state = key);
            }
        };
        box.Pick(2);
        Assert.Equal("gone", state);
        later.Dequeue()();
        (ChoiceList<string?> next, int nextSelected) = DynamicsChoices.Sidechains(sequence.AudioTracks, state);
        shown = ComboScenario.Show(box, shown, next, nextSelected, ref updating);
        Assert.Equal(2, box.Rebuilds);
        Assert.Equal(3, box.Items.Count);
        Assert.Equal(2, box.SelectedIndex);
    }

    // --- Transición ---

    private static InstalledPackage Package(string id, string name, PackageOrigin origin, params PackageItem[] items) => new(
        new PackageManifest
        {
            FormatVersion = 1,
            Id = id,
            Version = PackageVersion.Parse("1.0.0"),
            Kind = PackageKind.Content,
            Name = name,
            Author = "Dathinel",
            License = "CC0-1.0",
            Apps = ["rodaje"],
            Items = items,
        },
        Path.Combine(Path.GetTempPath(), "rodaje-sin-carpeta", id),
        origin);

    private static PackageScan Scan(params InstalledPackage[] packages) => new(packages, []);

    private static readonly PackageScan Transitions = Scan(
        Package("rodaje-transiciones", "Transiciones", PackageOrigin.Installed,
            new PackageItem("barrido", ItemKind.Transition, "Barrido suave"), new PackageItem("destello", ItemKind.Transition, "Destello")),
        Package(PackageLibrary.OwnPackageId, "Propios", PackageOrigin.Own, new PackageItem("mia", ItemKind.Transition, "La mía")));

    [Theory]
    [InlineData(TrackKind.Video)]
    [InlineData(TrackKind.Audio)]
    public void Transicion_ElegirCadaTipo_NoRehaceLaLista(TrackKind kind)
    {
        (string Type, string Preset) first = (TransitionEditor.TypesFor(kind)[0].Type, "");
        int chosen = ComboScenario.ChooseEach(choice => TransitionChoices.Types(kind, Transitions, choice.Type, choice.Preset), first);
        Assert.Equal(TransitionEditor.TypesFor(kind).Count + (kind == TrackKind.Audio ? 0 : 3), chosen);
    }

    [Fact]
    public void Transicion_DePaquete_ConSuOrigen_YLaQueFaltaSeMuestraIgual()
    {
        int builtIn = TransitionEditor.TypesFor(TrackKind.Video).Count;
        (ChoiceList<(string Type, string Preset)> list, _) = TransitionChoices.Types(TrackKind.Video, Transitions, TransitionEditor.TypesFor(TrackKind.Video)[0].Type, "");
        Assert.Equal(builtIn + 3, list.Count);
        Assert.Equal("Barrido suave · Transiciones", list.Labels[builtIn]);
        Assert.Equal("La mía · Mía", list.Labels[^1]);
        Assert.Equal(BuiltInTransitions.Preset, list.Values[builtIn].Type);

        (ChoiceList<(string Type, string Preset)> missing, int selected) = TransitionChoices.Types(TrackKind.Video, Transitions, BuiltInTransitions.Preset, "otro@1.0.0/falta");
        Assert.Equal(builtIn + 4, missing.Count);
        Assert.Equal(missing.Count - 1, selected);
        Assert.Equal("otro@1.0.0/falta (falta el paquete)", missing.Labels[selected]);

        // Elegir otro tipo desde la que falta rehace la lista: por eso la edición va después del evento.
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(missing, list, false));
    }

    [Fact]
    public void Transicion_DeImagenEnUnaPistaDeAudio_SeMuestraConSuNombre()
    {
        string image = TransitionEditor.TypesFor(TrackKind.Video).Select(d => d.Type).First(t => !TransitionEditor.TypesFor(TrackKind.Audio).Any(a => a.Type == t));
        (ChoiceList<(string Type, string Preset)> list, int selected) = TransitionChoices.Types(TrackKind.Audio, Transitions, image, "");
        Assert.Equal(list.Count - 1, selected);
        Assert.Equal(EffectUi.Name(image), list.Labels[selected]);
    }

    // --- Filtros (LUT) ---

    private static readonly PackageScan Luts = Scan(Package("rodaje-filtros", "Paquete", PackageOrigin.Installed,
        new PackageItem("cine", ItemKind.Lut, "Cine", "cine.cube"), new PackageItem("frio", ItemKind.Lut, "Frío", "frio.cube")));

    [Fact]
    public void Lut_ElegirCadaUnaInstalada_NoRehaceLaLista()
    {
        // «Cargar .cube…» (nulo) abre un diálogo: no se elige aquí.
        int chosen = ComboScenario.ChooseEach(current => LutChoices.Build(Luts, current!), (string?)"", value => value is not null);
        Assert.Equal(3, chosen);
    }

    [Fact]
    public void Lut_UnArchivo_OUnPaqueteQueFalta_SeMuestranAntesDeCargar()
    {
        (ChoiceList<string?> list, int none) = LutChoices.Build(Luts, "");
        Assert.Equal(["Sin LUT", "Cine · Paquete", "Frío · Paquete", LutChoices.LoadCubeLabel], list.Labels);
        Assert.Equal(0, none);
        Assert.Null(list.Values[^1]);

        string file = LutReference.FilePrefix + "mio.cube";
        (ChoiceList<string?> withFile, int selected) = LutChoices.Build(Luts, file);
        Assert.Equal(3, selected);
        Assert.EndsWith(" (archivo)", withFile.Labels[selected], StringComparison.Ordinal);
        Assert.Equal(LutChoices.LoadCubeLabel, withFile.Labels[^1]);
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(list, withFile, false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(list, withFile, true));

        (ChoiceList<string?> missing, int missingSelected) = LutChoices.Build(Luts, "otro@1.0.0/lut");
        Assert.EndsWith(" (falta el paquete)", missing.Labels[missingSelected], StringComparison.Ordinal);
    }

    // --- Seguimiento ---

    private static Project VideoProject(out Sequence sequence, out Clip tracked)
    {
        var v1 = new Track { Id = "v1", Kind = TrackKind.Video, Name = "V1" };
        tracked = new Clip { Id = "c", MediaId = "m", Start = Time(0), SourceIn = Time(0), SourceOut = Time(10) };
        tracked.Effects.Add(new EffectInstance { Id = "k1", Type = BuiltInEffects.Mask });
        var named = new EffectInstance { Id = "k2", Type = BuiltInEffects.Mask };
        named.Options[O.Name] = "Cara";
        tracked.Effects.Add(named);
        v1.Clips.Add(tracked);
        var v2 = new Track { Id = "v2", Kind = TrackKind.Video, Name = "V2" };
        v2.Clips.Add(new Clip { Id = "img", MediaId = "m", Start = Time(2), SourceIn = Time(0), SourceOut = Time(3) });
        v2.Clips.Add(new Clip { Id = "titulo", Start = Time(6), SourceIn = Time(0), SourceOut = Time(2), Generator = new ClipGenerator { Kind = GeneratorKinds.Text, Text = "Hola" } });
        v2.Clips.Add(new Clip { Id = "lejos", MediaId = "m", Start = Time(20), SourceIn = Time(0), SourceOut = Time(2) });
        sequence = new Sequence { Id = "s" };
        sequence.VideoTracks.Add(v1);
        sequence.VideoTracks.Add(v2);
        var project = new Project { Id = "p", Sequences = [sequence] };
        project.Media.Add(new MediaRef { Id = "m", Path = "plano.mp4", Name = "plano.mp4", HasVideo = true });
        return project;
    }

    [Theory]
    [InlineData(PointTrackKind.Point)]
    [InlineData(PointTrackKind.Two)]
    [InlineData(PointTrackKind.Corners)]
    [InlineData(PointTrackKind.Region)]
    public void Seguimiento_ElegirCadaUso_NoRehaceLaLista(PointTrackKind kind)
    {
        int chosen = ComboScenario.ChooseEach(use => TrackingChoices.ForKind(kind, use), TrackUse.FollowPosition);
        Assert.Equal(TrackingChoices.Uses.Count(u => TrackApply.Supports(kind, u.Use)), chosen);
    }

    [Fact]
    public void Seguimiento_UnUsoQueNoAdmite_VuelveAlPrimero()
    {
        Assert.Equal(0, TrackingChoices.ForKind(PointTrackKind.Point, TrackUse.CornerPin).Selected);
        Assert.Equal(0, TrackingChoices.ForKind(PointTrackKind.Point, null).Selected);
        (ChoiceList<TrackUse> uses, int stabilize) = TrackingChoices.ForKind(PointTrackKind.Region, TrackUse.Stabilize);
        Assert.Equal(TrackUse.Stabilize, uses.Values[stabilize]);
    }

    [Theory]
    [InlineData(TrackUse.FollowPosition, 2)]
    [InlineData(TrackUse.FollowAll, 2)]
    [InlineData(TrackUse.CornerPin, 1)]
    [InlineData(TrackUse.Mask, 3)]
    public void Seguimiento_ElegirCadaDestino_NoRehaceLaLista(TrackUse use, int count)
    {
        Project project = VideoProject(out Sequence sequence, out Clip tracked);
        Assert.Equal(count, ComboScenario.ChooseEach(target => TrackingChoices.Targets(project, sequence, tracked, use, target), (string?)null));
    }

    [Fact]
    public void Seguimiento_Destinos_TextoYAyuda()
    {
        Project project = VideoProject(out Sequence sequence, out Clip tracked);
        Assert.Equal(["plano.mp4 (V2)", "Hola (V2)"], TrackingChoices.Targets(project, sequence, tracked, TrackUse.FollowPosition, null).List.Labels);
        Assert.Equal(["Una máscara nueva (elipse)", "Máscara 1", "Cara"], TrackingChoices.Targets(project, sequence, tracked, TrackUse.Mask, null).List.Labels);
        Assert.Equal(2, TrackingChoices.Targets(project, sequence, tracked, TrackUse.Mask, "k2").Selected);
        Assert.Equal(1, TrackingChoices.Targets(project, sequence, tracked, TrackUse.FollowAll, "titulo").Selected);
        Assert.Equal(0, TrackingChoices.Targets(project, sequence, tracked, TrackUse.FollowAll, "quitado").Selected);

        (ChoiceList<string?> none, int selected) = TrackingChoices.Targets(project, sequence, tracked, TrackUse.Stabilize, null);
        Assert.Equal((0, -1), (none.Count, selected));

        sequence.VideoTracks[1].Clips.Clear();
        (ChoiceList<string?> help, int helpSelected) = TrackingChoices.Targets(project, sequence, tracked, TrackUse.CornerPin, null);
        Assert.Equal(["Pon un video o una imagen encima de este clip"], help.Labels);
        Assert.Null(help.Values[0]);
        Assert.Equal(0, helpSelected);
    }

    // --- Texto ---

    private static readonly PackageScan TextStyles = Scan(Package("rodaje-titulos", "Títulos", PackageOrigin.Installed,
        new PackageItem("neon", ItemKind.TextStyle, "Neón", "neon.json"), new PackageItem("cine", ItemKind.TextStyle, "Cine", "cine.json")));

    private static TextStyle StyleOf(LibraryElement element)
    {
        TextStyle style = TitleStyles.Default();
        if (!element.IsBuiltIn)
        {
            style.Source = element.Reference;
        }

        return style;
    }

    [Fact]
    public void Texto_ElegirCadaEstilo_NoRehaceLaLista()
    {
        LibraryElement builtIn = TextStyleChoices.Build(TextStyles).Values[0];
        int chosen = ComboScenario.ChooseEach(
            element =>
            {
                // Otro escaneo igual en cada refresco: los elementos son iguales aunque sean otras instancias.
                ChoiceList<LibraryElement> styles = TextStyleChoices.Build(Scan([.. TextStyles.Packages]));
                return (styles, TextStyleChoices.IndexOf(styles, StyleOf(element)));
            },
            builtIn);
        Assert.Equal(3, chosen);
    }

    [Fact]
    public void Texto_UnEstiloCambiado_NoEligeNinguno()
    {
        ChoiceList<LibraryElement> styles = TextStyleChoices.Build(TextStyles);
        Assert.Equal(["Texto", "Neón · Títulos", "Cine · Títulos"], styles.Labels);
        Assert.Equal(0, TextStyleChoices.IndexOf(styles, TitleStyles.Default()));

        TextStyle changed = TitleStyles.Default();
        changed.Size += 10;
        Assert.Equal(-1, TextStyleChoices.IndexOf(styles, changed));

        changed.Source = "otro@1.0.0/falta";
        Assert.Equal(-1, TextStyleChoices.IndexOf(styles, changed));
    }

    // --- Máscaras ---

    private static Clip MaskedClip(out EffectInstance mask)
    {
        var clip = new Clip { Id = "c", MediaId = "m" };
        clip.Effects.Add(new EffectInstance { Id = "color", Type = BuiltInEffects.Color });
        clip.Effects.Add(new EffectInstance { Id = "apagado", Type = BuiltInEffects.Color, Enabled = false });
        mask = new EffectInstance { Id = "k", Type = BuiltInEffects.Mask };
        clip.Effects.Add(mask);
        return clip;
    }

    /// <summary>Lo que hace <c>MasksPanel.ChooseTarget</c> con las opciones de la máscara.</summary>
    private static void SetTarget(EffectInstance mask, string choice)
    {
        mask.Options[O.Target] = choice == MaskTargetChoices.EffectsZone ? "" : choice;
        mask.Options[O.Use] = choice == MaskTargetChoices.EffectsZone ? "effects" : "";
    }

    [Fact]
    public void Mascaras_ElegirCadaDestino_NoRehaceLaLista()
    {
        Clip clip = MaskedClip(out EffectInstance mask);
        int chosen = ComboScenario.ChooseEach(
            choice =>
            {
                SetTarget(mask, choice);
                return MaskTargetChoices.Build(clip, mask);
            },
            "");
        Assert.Equal(4, chosen);
    }

    [Fact]
    public void Mascaras_Destinos_TextoYUnEfectoQueYaNoEsta()
    {
        Clip clip = MaskedClip(out EffectInstance mask);
        (ChoiceList<string> list, int selected) = MaskTargetChoices.Build(clip, mask);
        Assert.Equal(["La capa (recorta el clip)", "Zona de los efectos con «Dónde» (no recorta)", "Solo «" + EffectUi.Name(BuiltInEffects.Color) + "»",
            "Solo «" + EffectUi.Name(BuiltInEffects.Color) + "» (desactivado)"], list.Labels);
        Assert.Equal(0, selected);

        mask.Options[O.Use] = "effects";
        Assert.Equal(1, MaskTargetChoices.Build(clip, mask).Selected);

        mask.Options[O.Target] = "quitado";
        (ChoiceList<string> missing, int missingSelected) = MaskTargetChoices.Build(clip, mask);
        Assert.Equal("Un efecto que ya no está", missing.Labels[missingSelected]);
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(missing, list, false));

        // La zona de los efectos no coincide nunca con el id de un efecto.
        Assert.StartsWith("\u0001", MaskTargetChoices.EffectsZone, StringComparison.Ordinal);
    }
}
