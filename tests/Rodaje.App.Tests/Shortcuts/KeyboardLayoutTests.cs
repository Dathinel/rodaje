using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shortcuts;

public class KeyboardLayoutTests
{
    private static readonly IKeyboardLayout Es = FakeKeyboardLayout.Spanish;

    private static KeyChord K(string text) => KeyChord.Parse(text);

    private static ShortcutRegistry Catalog(ShortcutPreset preset = ShortcutPreset.Rodaje)
    {
        var registry = new ShortcutRegistry();
        registry.RegisterRange(EditorShortcuts.Catalog);
        registry.Preset = preset;
        return registry;
    }

    [Theory]
    [InlineData("Alt+[", "Alt+'")]
    [InlineData("Alt+]", "Alt+¡")]
    [InlineData("`", "Ñ")]
    [InlineData("/", "Ç")]
    [InlineData("\\", "º")]
    [InlineData(";", "`")]
    [InlineData("'", "´")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+-", "Ctrl+-")]
    [InlineData("<", "<")]
    [InlineData("Ctrl+Mayús+Z", "Ctrl+Mayús+Z")]
    [InlineData("Supr", "Supr")]
    [InlineData("Ctrl+Num +", "Ctrl+Num +")]
    public void Format_ConDistribucionEspañola_DaLoImpresoEnLaTecla(string catalogText, string shown)
    {
        Assert.Equal(shown, KeyNames.Format(K(catalogText), Es));
    }

    [Fact]
    public void Format_SinDistribucion_SigueConLosNombresEstadounidenses()
    {
        Assert.Equal("Alt+[", KeyNames.Format(K("Alt+["), null));
        Assert.Equal("Alt+OemOpenBracket", K("Alt+[").ToInvariantString());
    }

    [Fact]
    public void ElFormatoDeArchivo_NoDependeDeLaDistribucion()
    {
        KeyChord chord = new(0xC0, KeyModifiers.Control);
        Assert.Equal("Ctrl+OemTilde", chord.ToInvariantString());
        Assert.True(KeyChord.TryParse(chord.ToInvariantString(), out KeyChord back));
        Assert.Equal(chord, back);
    }

    [Theory]
    [InlineData("Ctrl+Ñ", 0xC0, KeyModifiers.Control)]
    [InlineData("ñ", 0xC0, KeyModifiers.None)]
    [InlineData("Alt+'", 0xDB, KeyModifiers.Alt)]
    [InlineData("ç", 0xBF, KeyModifiers.None)]
    [InlineData("Ctrl++", 0xBB, KeyModifiers.Control)]
    [InlineData("S", 'S', KeyModifiers.None)]
    public void TryParse_ConDistribucion_EntiendeLoQueSeVe(string text, int key, KeyModifiers modifiers)
    {
        Assert.True(KeyNames.TryParse(text, Es, out KeyChord chord));
        Assert.Equal(new KeyChord(key, modifiers), chord);
    }

    [Fact]
    public void TryParse_SinDistribucion_LaComillaEsLaEstadounidense()
    {
        Assert.True(KeyNames.TryParse("Alt+'", null, out KeyChord chord));
        Assert.Equal(new KeyChord(0xDE, KeyModifiers.Alt), chord);
    }

    [Fact]
    public void ElTecladoDibujado_NoSolapaTeclasYCabeEnSuTamaño()
    {
        IReadOnlyList<KeyCap> caps = KeyboardGeometry.SpanishIso;
        Assert.Equal(102, caps.Count);
        Assert.All(caps, c => Assert.True(c.X >= 0 && c.Y >= 0 && c.X + c.Width <= KeyboardGeometry.Width && c.Y + c.Height <= KeyboardGeometry.Height));
        for (int i = 0; i < caps.Count; i++)
        {
            for (int j = i + 1; j < caps.Count; j++)
            {
                KeyCap a = caps[i];
                KeyCap b = caps[j];
                bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Assert.False(overlap, $"Se solapan {a} y {b}");
            }
        }

        Assert.Equal(caps.Count, caps.Select(c => c.ScanCode).Distinct().Count());
    }

    [Fact]
    public void ElTecladoEspañol_EtiquetaCadaTeclaComoEstaImpresa()
    {
        IReadOnlyList<KeyCapState> keys = KeyboardGeometry.Map(Catalog(), Es);

        Assert.Equal("Ñ", ByScan(keys, 0x27).Label);
        Assert.Equal("Ç", ByScan(keys, 0x2B).Label);
        Assert.Equal("º", ByScan(keys, 0x29).Label);
        Assert.Equal("'", ByScan(keys, 0x0C).Label);
        Assert.Equal("¡", ByScan(keys, 0x0D).Label);
        Assert.Equal("+", ByScan(keys, 0x1B).Label);
        Assert.Equal("-", ByScan(keys, 0x35).Label);
        Assert.Equal("<", ByScan(keys, 0x56).Label);
        Assert.Equal("Q", ByScan(keys, 0x10).Label);
        Assert.Equal("Espacio", ByScan(keys, 0x39).Label);
        Assert.Equal("Bloq Mayús", ByScan(keys, 0x3A).Label);
    }

    [Fact]
    public void ElTecladoEspañol_PoneCadaAccionEnLaTeclaFisicaCorrecta()
    {
        // En el estilo Clásico, Recortar inicio es Alt+[ (VK_OEM_4): en un teclado español es la tecla «'», a la derecha del 0.
        IReadOnlyList<KeyCapState> keys = KeyboardGeometry.Map(Catalog(ShortcutPreset.Classic), Es);
        KeyCapState quote = ByScan(keys, 0x0C);
        Assert.Equal(ActionIds.TrimStart, quote.For(KeyModifiers.Alt)?.Action.Id);
        Assert.Null(quote.For(KeyModifiers.None));

        // Acercar con Ctrl++: la tecla «+» del español está junto a la P, no junto al 0 como en EE. UU.
        KeyCapState plus = ByScan(Catalog(), 0x1B);
        Assert.Equal(ActionIds.ZoomIn, plus.For(KeyModifiers.Control)?.Action.Id);
    }

    [Fact]
    public void SinDistribucion_UsaLaPosicionEstadounidense()
    {
        IReadOnlyList<KeyCapState> keys = KeyboardGeometry.Map(Catalog(ShortcutPreset.Classic), null);
        KeyCapState bracket = ByScan(keys, 0x1A);
        Assert.Equal("[", bracket.Label);
        Assert.Equal(ActionIds.TrimStart, bracket.For(KeyModifiers.Alt)?.Action.Id);
    }

    [Fact]
    public void CadaTecla_ListaSusAccionesPrimeroSola()
    {
        KeyCapState s = ByScan(KeyboardGeometry.Map(Catalog(), Es), 0x1F);
        Assert.Equal([ActionIds.Split, ActionIds.Save, ActionIds.SaveAs], s.Bindings.Select(b => b.Action.Id));
        Assert.Equal(ActionIds.TogglePlay, ByScan(KeyboardGeometry.Map(Catalog(), Es), 0x39).For(KeyModifiers.None)?.Action.Id);
    }

    [Fact]
    public void LasTeclasInertes_NoMuestranAcciones()
    {
        var registry = new ShortcutRegistry();
        registry.Register(new ShortcutAction("x", "Algo", "Grupo", ["Ctrl+Alt+K"]));
        IReadOnlyList<KeyCapState> keys = KeyboardGeometry.Map(registry, Es);
        Assert.All(keys.Where(k => k.Cap.IsInert), k => Assert.Empty(k.Bindings));
        Assert.Equal(5, keys.Count(k => k.Cap.Modifier != KeyModifiers.None));
    }

    private static KeyCapState ByScan(ShortcutRegistry registry, int scan) => ByScan(KeyboardGeometry.Map(registry, Es), scan);

    private static KeyCapState ByScan(IReadOnlyList<KeyCapState> keys, int scan) => keys.Single(k => k.Cap.ScanCode == scan);
}
