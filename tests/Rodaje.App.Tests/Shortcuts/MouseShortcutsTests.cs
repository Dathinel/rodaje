using Rodaje.App.Services.Shortcuts;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Shortcuts;

public class MouseShortcutsTests
{
    private static readonly MouseChord Wheel = new(MouseInput.Wheel);
    private static readonly MouseChord CtrlWheel = new(MouseInput.Wheel, KeyModifiers.Control);
    private static readonly MouseChord Middle = new(MouseInput.Middle);
    private static readonly MouseChord X1 = new(MouseInput.X1);
    private static readonly MouseChord X2 = new(MouseInput.X2);

    private static ShortcutRegistry Catalog(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = preset;
        return registry;
    }

    [Fact]
    public void DeFabrica_CadaEstiloYZonaTieneSuRaton()
    {
        MouseMap rodaje = Catalog().Mouse;
        Assert.Equal(MouseGestures.ScrollTracks, rodaje.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(MouseGestures.ScrollTime, rodaje.Get(MouseContext.Timeline, new MouseChord(MouseInput.Wheel, KeyModifiers.Shift)));
        Assert.Equal(MouseGestures.TrackHeight, rodaje.Get(MouseContext.Timeline, new MouseChord(MouseInput.Wheel, KeyModifiers.Alt)));
        Assert.Equal(MouseGestures.ScrollTime, rodaje.Get(MouseContext.Timeline, new MouseChord(MouseInput.Tilt)));
        Assert.Equal(MouseGestures.Zoom, rodaje.Get(MouseContext.Timeline, CtrlWheel));
        Assert.Equal(MouseGestures.Pan, rodaje.Get(MouseContext.Timeline, Middle));
        Assert.Equal(ActionIds.PreviousFrame, rodaje.Get(MouseContext.Timeline, X1));
        Assert.Equal(MouseGestures.Frames, rodaje.Get(MouseContext.Viewer, Wheel));

        MouseMap agile = Catalog(ShortcutPreset.Agile).Mouse;
        Assert.Equal(ActionIds.Undo, agile.Get(MouseContext.Timeline, X1));
        Assert.Equal(ActionIds.Redo, agile.Get(MouseContext.Library, X2));

        MouseMap classic = Catalog(ShortcutPreset.Classic).Mouse;
        Assert.Equal(MouseGestures.Zoom, classic.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(MouseGestures.ScrollTime, classic.Get(MouseContext.Timeline, CtrlWheel));
        Assert.Equal(MouseGestures.ScrollTracks, classic.Get(MouseContext.Timeline, new MouseChord(MouseInput.Wheel, KeyModifiers.Shift)));
    }

    [Fact]
    public void LosClicsYLaRuedaDeLaBiblioteca_SonFijos()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange left = customizer.AssignMouse(MouseContext.Timeline, new MouseChord(MouseInput.Left), ActionIds.Undo);
        ShortcutChange wheel = customizer.AssignMouse(MouseContext.Library, Wheel, MouseGestures.Frames);

        Assert.False(left.Changed);
        Assert.Contains("izquierdo y derecho", left.Message, StringComparison.Ordinal);
        Assert.False(wheel.Changed);
        Assert.NotNull(MouseGestures.Fixed(MouseContext.Library, Wheel));
    }

    [Fact]
    public void LaRueda_SoloAceptaAccionesDeRueda_YArrastrarNecesitaUnBoton()
    {
        MouseMap mouse = Catalog().Mouse;

        Assert.NotNull(mouse.Reason(MouseContext.Timeline, Wheel, ActionIds.Undo));
        Assert.NotNull(mouse.Reason(MouseContext.Timeline, X1, MouseGestures.Zoom));
        Assert.NotNull(mouse.Reason(MouseContext.Timeline, Wheel, MouseGestures.Pan));
        Assert.NotNull(mouse.Reason(MouseContext.Viewer, Wheel, MouseGestures.Zoom));
        Assert.Null(mouse.Reason(MouseContext.Viewer, CtrlWheel, MouseGestures.ViewerZoom));
        Assert.Null(mouse.Reason(MouseContext.Library, Middle, ActionIds.TogglePlay));
    }

    [Fact]
    public void Conflicto_IntercambiarPasaLaOtraAccionALaCombinacionDeOrigen()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        ShortcutChange change = customizer.AssignMouse(MouseContext.Timeline, Wheel, MouseGestures.Zoom, choice: ConflictChoice.Swap);

        Assert.True(change.Changed);
        Assert.Equal(MouseGestures.Zoom, registry.Mouse.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(MouseGestures.ScrollTracks, registry.Mouse.Get(MouseContext.Timeline, CtrlWheel));
        Assert.Contains("se intercambiaron", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicto_QuitarDejaLaAccionEnLasDosYLaOtraSinEsaCombinacion()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        customizer.AssignMouse(MouseContext.Timeline, Wheel, MouseGestures.Zoom, choice: ConflictChoice.Take);

        Assert.Equal(MouseGestures.Zoom, registry.Mouse.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(MouseGestures.Zoom, registry.Mouse.Get(MouseContext.Timeline, CtrlWheel));
        Assert.Null(registry.Mouse.FindChord(MouseContext.Timeline, MouseGestures.ScrollTracks));
        Assert.NotNull(registry.Mouse.FindChord(MouseContext.Timeline, MouseGestures.ScrollTime));
    }

    [Fact]
    public void Mover_ConFrom_DejaLibreElOrigen()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);
        var altX1 = new MouseChord(MouseInput.X1, KeyModifiers.Alt);

        customizer.AssignMouse(MouseContext.Timeline, altX1, ActionIds.PreviousFrame, from: X1, choice: ConflictChoice.Take);

        Assert.Null(registry.Mouse.Get(MouseContext.Timeline, X1));
        Assert.Equal(ActionIds.PreviousFrame, registry.Mouse.Get(MouseContext.Timeline, altX1));
    }

    [Fact]
    public void CadaZonaEsIndependiente()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        customizer.AssignMouse(MouseContext.Viewer, X1, ActionIds.Undo);

        Assert.Equal(ActionIds.Undo, registry.Mouse.Get(MouseContext.Viewer, X1));
        Assert.Equal(ActionIds.PreviousFrame, registry.Mouse.Get(MouseContext.Timeline, X1));
        Assert.Equal(ActionIds.PreviousFrame, registry.Mouse.Get(MouseContext.Library, X1));
    }

    [Fact]
    public void Quitar_RestablecerBotonZonaYDeshacer()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        customizer.RemoveMouse(MouseContext.Timeline, Middle);
        customizer.AssignMouse(MouseContext.Timeline, X2, ActionIds.Split);
        Assert.Null(registry.Mouse.Get(MouseContext.Timeline, Middle));

        customizer.ResetMouse(MouseContext.Timeline, MouseInput.Middle);
        Assert.Equal(MouseGestures.Pan, registry.Mouse.Get(MouseContext.Timeline, Middle));
        Assert.Equal(ActionIds.Split, registry.Mouse.Get(MouseContext.Timeline, X2));

        customizer.ResetMouseContext(MouseContext.Timeline);
        Assert.Equal(ActionIds.NextFrame, registry.Mouse.Get(MouseContext.Timeline, X2));

        Assert.NotNull(customizer.UndoLast());
        Assert.Equal(ActionIds.Split, registry.Mouse.Get(MouseContext.Timeline, X2));
    }

    [Fact]
    public void ElRaton_SeGuardaPorEstilo()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);
        customizer.AssignMouse(MouseContext.Timeline, X1, ActionIds.Undo);

        registry.Preset = ShortcutPreset.Classic;
        Assert.Equal(ActionIds.PreviousFrame, registry.Mouse.Get(MouseContext.Timeline, X1));
        registry.Preset = ShortcutPreset.Rodaje;
        Assert.Equal(ActionIds.Undo, registry.Mouse.Get(MouseContext.Timeline, X1));
    }

    [Fact]
    public void VolverALoDeFabrica_NoDejaCambioGuardado()
    {
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);

        customizer.AssignMouse(MouseContext.Timeline, X1, ActionIds.Undo);
        customizer.AssignMouse(MouseContext.Timeline, X1, ActionIds.PreviousFrame);

        Assert.Empty(registry.Mouse.GetOverrides(ShortcutPreset.Rodaje));
    }

    [Fact]
    public void Guardar_YLeer_ConservaElRaton()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        ShortcutRegistry original = Catalog();
        var customizer = new ShortcutCustomizer(original);
        customizer.AssignMouse(MouseContext.Timeline, Wheel, MouseGestures.Zoom);
        customizer.RemoveMouse(MouseContext.Viewer, X2);
        new ShortcutStore(path).Save(original);

        ShortcutRegistry loaded = Catalog();
        Assert.Null(new ShortcutStore(path).Load(loaded));

        Assert.Equal(MouseGestures.Zoom, loaded.Mouse.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(MouseGestures.ScrollTracks, loaded.Mouse.Get(MouseContext.Timeline, CtrlWheel));
        Assert.Null(loaded.Mouse.Get(MouseContext.Viewer, X2));
        string json = File.ReadAllText(path);
        Assert.Contains("\"timeline\"", json, StringComparison.Ordinal);
        Assert.Contains("Wheel", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchivoViejo_SinRaton_SigueValiendo()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        File.WriteAllText(path, """{ "version": 1, "presets": { "Rodaje": { "edit.split": ["X"] } } }""");

        ShortcutRegistry loaded = Catalog();
        Assert.Null(new ShortcutStore(path).Load(loaded));

        Assert.Equal(ActionIds.Split, loaded.FindAction(KeyChord.Parse("X")));
        Assert.Equal(MouseGestures.ScrollTracks, loaded.Mouse.Get(MouseContext.Timeline, Wheel));
    }

    [Fact]
    public void ArchivoConCombinacionesInvalidas_LasDescarta()
    {
        using var dir = new TempDirectory();
        string path = dir.File(ShortcutStore.FileName);
        File.WriteAllText(path, """
            { "version": 2, "presets": {},
              "mouse": { "Rodaje": { "timeline": { "Left": "edit.undo", "Wheel": "edit.undo", "X1": "edit.split", "Nada": "x" } } } }
            """);

        ShortcutRegistry loaded = Catalog();
        Assert.Null(new ShortcutStore(path).Load(loaded));

        Assert.Null(loaded.Mouse.Get(MouseContext.Timeline, new MouseChord(MouseInput.Left)));
        Assert.Equal(MouseGestures.ScrollTracks, loaded.Mouse.Get(MouseContext.Timeline, Wheel));
        Assert.Equal(ActionIds.Split, loaded.Mouse.Get(MouseContext.Timeline, X1));
    }

    [Fact]
    public void ExportarEImportar_ReemplazaTodoYSePuedeDeshacer()
    {
        using var dir = new TempDirectory();
        string path = dir.File("mis-atajos.json");
        ShortcutRegistry source = Catalog();
        source.Rebind(ActionIds.Split, KeyChord.Parse("X"), replacing: KeyChord.Parse("S"));
        new ShortcutCustomizer(source).AssignMouse(MouseContext.Timeline, X1, ActionIds.Undo);
        ShortcutStore.Export(source, path);

        ShortcutRegistry target = Catalog();
        var customizer = new ShortcutCustomizer(target);
        customizer.AssignMouse(MouseContext.Viewer, X2, ActionIds.Redo);
        ShortcutChange import = customizer.Import(path);

        Assert.True(import.Changed);
        Assert.Equal(ActionIds.Split, target.FindAction(KeyChord.Parse("X")));
        Assert.Equal(ActionIds.Undo, target.Mouse.Get(MouseContext.Timeline, X1));
        Assert.Equal(ActionIds.NextFrame, target.Mouse.Get(MouseContext.Viewer, X2));

        customizer.Undo(import);
        Assert.Equal(ActionIds.Redo, target.Mouse.Get(MouseContext.Viewer, X2));
        Assert.Null(target.FindAction(KeyChord.Parse("X")));
    }

    [Fact]
    public void Importar_ArchivoQueNoEsDeAtajos_NoCambiaNada()
    {
        using var dir = new TempDirectory();
        string path = dir.File("otro.json");
        File.WriteAllText(path, "[1, 2, 3]");
        ShortcutRegistry registry = Catalog();
        var customizer = new ShortcutCustomizer(registry);
        customizer.AssignMouse(MouseContext.Timeline, X1, ActionIds.Undo);

        ShortcutChange change = customizer.Import(path);

        Assert.False(change.Changed);
        Assert.Contains("no es un mapa de atajos", change.Message, StringComparison.Ordinal);
        Assert.Equal(ActionIds.Undo, registry.Mouse.Get(MouseContext.Timeline, X1));
    }

    [Theory]
    [InlineData("Ctrl+Wheel", MouseInput.Wheel, KeyModifiers.Control)]
    [InlineData("Ctrl+Shift+Alt+X2", MouseInput.X2, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData("tilt", MouseInput.Tilt, KeyModifiers.None)]
    public void MouseChord_IdaYVuelta(string text, MouseInput input, KeyModifiers modifiers)
    {
        Assert.True(MouseChord.TryParse(text, out MouseChord chord));
        Assert.Equal(new MouseChord(input, modifiers), chord);
        Assert.True(MouseChord.TryParse(chord.ToInvariantString(), out MouseChord again));
        Assert.Equal(chord, again);
    }

    [Fact]
    public void MouseChord_SeMuestraEnEspanol() =>
        Assert.Equal("Ctrl+Mayús+Rueda", new MouseChord(MouseInput.Wheel, KeyModifiers.Control | KeyModifiers.Shift).ToString());

    [Fact]
    public void BindingSlots_DeUnaTecla_DanSusOchoCombinacionesConReservadasYAltGr()
    {
        ShortcutRegistry registry = Catalog();
        new ShortcutCustomizer(registry).AssignToKey(KeyChord.Parse("Ctrl+S"), ActionIds.Split, ConflictChoice.Take);

        IReadOnlyList<BindingSlot> s = BindingSlots.ForKey(registry, KeyChord.Parse("S").Key);
        IReadOnlyList<BindingSlot> f4 = BindingSlots.ForKey(registry, KeyChord.Parse("F4").Key);

        Assert.Equal(8, s.Count);
        Assert.Equal(ActionIds.Split, s[0].ActionId);
        Assert.False(s[0].Customized);
        BindingSlot ctrl = s.Single(x => x.Modifiers == KeyModifiers.Control);
        Assert.Equal(ActionIds.Split, ctrl.ActionId);
        Assert.True(ctrl.Customized);
        Assert.NotNull(s.Single(x => x.Modifiers == (KeyModifiers.Control | KeyModifiers.Alt)).Warning);
        Assert.True(f4.Single(x => x.Modifiers == KeyModifiers.Alt).IsLocked);
    }

    [Fact]
    public void BindingSlots_DelRaton_MarcanFijosYCambiados()
    {
        ShortcutRegistry registry = Catalog();
        new ShortcutCustomizer(registry).AssignMouse(MouseContext.Timeline, X1, ActionIds.Undo);

        IReadOnlyList<BindingSlot> left = BindingSlots.ForMouse(registry, MouseContext.Timeline, MouseInput.Left);
        IReadOnlyList<BindingSlot> x1 = BindingSlots.ForMouse(registry, MouseContext.Timeline, MouseInput.X1);

        Assert.All(left, slot => Assert.True(slot.IsLocked));
        Assert.Equal(ActionIds.Undo, x1[0].ActionId);
        Assert.True(x1[0].Customized);
        Assert.Equal(BindingSlots.NoAction, x1[1].Label);
    }

    [Fact]
    public void Choices_LaRuedaSoloOfreceAccionesDeRuedaDeLaZona_YBuscaSinTildes()
    {
        ShortcutRegistry registry = Catalog();

        IReadOnlyList<BindingChoice> wheel = BindingSlots.MouseChoices(registry, MouseContext.Viewer, Wheel, null);
        IReadOnlyList<BindingChoice> button = BindingSlots.MouseChoices(registry, MouseContext.Timeline, X1, "arrastrar");
        IReadOnlyList<BindingChoice> keys = BindingSlots.KeyChoices(registry, "rapido");

        Assert.Equal([MouseGestures.Frames, MouseGestures.ViewerZoom], wheel.Select(c => c.Id).Order());
        Assert.Contains(button, c => c.Id == MouseGestures.Pan);
        Assert.Contains(keys, c => c.Id == ActionIds.SpeedUp);
    }
}

public class TimelineWheelTests
{
    [Theory]
    [InlineData(MouseGestures.ScrollTracks, false, false, false, MouseGestures.ScrollTracks)]
    [InlineData(MouseGestures.ScrollTracks, false, false, true, MouseGestures.ScrollTime)]
    [InlineData(MouseGestures.ScrollTracks, true, false, true, MouseGestures.ScrollTracks)]
    [InlineData(MouseGestures.ScrollTime, true, false, false, MouseGestures.ScrollTracks)]
    [InlineData(MouseGestures.ScrollTime, true, true, false, MouseGestures.ScrollTime)]
    [InlineData(MouseGestures.Zoom, false, false, true, MouseGestures.Zoom)]
    [InlineData(MouseGestures.TrackHeight, true, false, true, MouseGestures.TrackHeight)]
    public void LaRuedaEfectiva_DependeDeDondeEstaYDeSiSeVenTodasLasPistas(string mapped, bool overHeaders, bool tilt, bool allVisible, string expected)
    {
        Assert.Equal(expected, MouseGestures.EffectiveTimelineWheel(mapped, overHeaders, tilt, allVisible));
    }

    [Fact]
    public void ElAltoDeLasPistas_EsUnGestoDeRuedaDeLaLineaDeTiempo()
    {
        MouseGesture gesture = MouseGestures.Find(MouseGestures.TrackHeight)!;
        Assert.Equal(MouseGestureKind.Axis, gesture.Kind);
        Assert.True(gesture.WorksIn(MouseContext.Timeline));
        Assert.False(gesture.WorksIn(MouseContext.Viewer));
    }
}
